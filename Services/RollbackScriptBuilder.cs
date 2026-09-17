using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public sealed class RollbackScriptBuilder
{
    private const int MaxSnapshotRows = 2000;

    public async Task<RollbackCapture?> SnapshotBatchAsync(
        SqlConnection connection,
        string batch,
        string? databaseName,
        CancellationToken cancellationToken)
    {
        var changes = SqlChangeParser.Parse(batch);
        if (changes.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            await using var useCommand = connection.CreateCommand();
            useCommand.CommandText = $"USE {ParsedChange.Quote(databaseName)};";
            await useCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var snapshots = new List<ChangeSnapshot>();
        foreach (var change in changes)
        {
            snapshots.Add(await SnapshotAsync(connection, change, cancellationToken));
        }

        return new RollbackCapture(snapshots, databaseName);
    }

    public async Task<string?> ComposeAsync(
        SqlConnection connection,
        RollbackCapture capture,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        for (var i = capture.Snapshots.Count - 1; i >= 0; i--)
        {
            var sql = await ComposeOneAsync(connection, capture.Snapshots[i], cancellationToken);
            if (!string.IsNullOrWhiteSpace(sql))
            {
                parts.Add(sql.Trim());
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return PrefixUse(string.Join(Environment.NewLine + Environment.NewLine, parts), capture.DatabaseName);
    }

    public static string BuildBestEffort(string script, string? databaseName = null)
    {
        var changes = SqlChangeParser.Parse(script);
        if (changes.Count == 0)
        {
            return PrefixUse(
                """
                -- Automatic rollback is not available for this script.
                -- Original:
                """ + Environment.NewLine + script.Trim(),
                databaseName ?? SqlChangeParser.LastUseDatabaseName(script));
        }

        var parts = new List<string>();
        foreach (var change in changes.Reverse())
        {
            parts.Add(BestEffortFor(change));
        }

        return PrefixUse(
            string.Join(Environment.NewLine + Environment.NewLine, parts),
            databaseName ?? SqlChangeParser.LastUseDatabaseName(script));
    }

    public static string PrefixUse(string script, string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        var trimmed = script.TrimStart();
        if (trimmed.StartsWith("USE ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("USE[", StringComparison.OrdinalIgnoreCase))
        {
            return script;
        }

        return $"USE {ParsedChange.Quote(databaseName)};{Environment.NewLine}{Environment.NewLine}{script.Trim()}";
    }

    private static async Task<ChangeSnapshot> SnapshotAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        var snapshot = new ChangeSnapshot { Change = change };
        if (change.IsTempTable && change.Kind is not ChangeKind.SelectInto)
        {
            return snapshot;
        }

        if (change.Kind is ChangeKind.SelectInto)
        {
            return snapshot;
        }

        if (change.Kind is ChangeKind.Insert)
        {
            if (change.InsertColumns.Count == 0 && !change.IsTempTable)
            {
                snapshot.AllColumns = await LoadColumnNamesAsync(connection, change, cancellationToken);
            }

            return snapshot;
        }

        if (change.Kind is ChangeKind.InsertSelect or ChangeKind.BulkInsert)
        {
            snapshot.IdentityColumn = await LoadIdentityColumnAsync(connection, change, cancellationToken);
            if (snapshot.IdentityColumn is not null)
            {
                snapshot.IdentityBefore = await LoadIdentCurrentAsync(connection, change, cancellationToken);
            }

            return snapshot;
        }

        snapshot.AllColumns = await LoadColumnNamesAsync(connection, change, cancellationToken);
        snapshot.PrimaryKeyColumns = await LoadPrimaryKeyColumnsAsync(connection, change, cancellationToken);
        snapshot.IdentityColumn = await LoadIdentityColumnAsync(connection, change, cancellationToken);
        snapshot.BeforeRows = await LoadRowsAsync(connection, change, cancellationToken);
        return snapshot;
    }

    private static async Task<string> ComposeOneAsync(
        SqlConnection connection,
        ChangeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var change = snapshot.Change;
        return change.Kind switch
        {
            ChangeKind.Insert => ComposeInsertValues(change, snapshot),
            ChangeKind.InsertSelect or ChangeKind.BulkInsert =>
                await ComposeIdentityRangeAsync(connection, snapshot, cancellationToken),
            ChangeKind.Delete or ChangeKind.Truncate or ChangeKind.Merge => ComposeRestoreInsert(snapshot),
            ChangeKind.Update => ComposeUpdate(snapshot),
            ChangeKind.SelectInto => $"DROP TABLE IF EXISTS {change.QuotedTable};",
            _ => BestEffortFor(change)
        };
    }

    private static string ComposeInsertValues(ParsedChange change, ChangeSnapshot snapshot)
    {
        var columns = change.InsertColumns.Count > 0
            ? change.InsertColumns
            : snapshot.AllColumns;
        if (columns.Count == 0 || change.InsertValueRows.Count == 0)
        {
            return BestEffortFor(change);
        }

        var predicates = new List<string>();
        foreach (var row in change.InsertValueRows)
        {
            var parts = new List<string>();
            for (var i = 0; i < columns.Count && i < row.Count; i++)
            {
                var quoted = ParsedChange.Quote(columns[i]);
                var value = row[i];
                parts.Add(IsNullLiteral(value) ? $"{quoted} IS NULL" : $"{quoted} = {value}");
            }

            if (parts.Count > 0)
            {
                predicates.Add("(" + string.Join(" AND ", parts) + ")");
            }
        }

        if (predicates.Count == 0)
        {
            return BestEffortFor(change);
        }

        return $"DELETE FROM {change.QuotedTable}{Environment.NewLine}WHERE {string.Join($"{Environment.NewLine}   OR ", predicates)};";
    }

    private static async Task<string> ComposeIdentityRangeAsync(
        SqlConnection connection,
        ChangeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var change = snapshot.Change;
        if (snapshot.IdentityColumn is null)
        {
            return $"""
                -- Could not build a precise rollback for {change.Kind} into {change.QuotedTable}.
                -- The target table has no IDENTITY column.
                -- Original:
                -- {OneLine(change.OriginalSql)}
                """;
        }

        var after = await LoadIdentCurrentAsync(connection, change, cancellationToken);
        var before = snapshot.IdentityBefore ?? 0;
        var quotedIdentity = ParsedChange.Quote(snapshot.IdentityColumn);
        return
            $"DELETE FROM {change.QuotedTable}{Environment.NewLine}" +
            $"WHERE {quotedIdentity} > {FormatNumber(before)} AND {quotedIdentity} <= {FormatNumber(after)};";
    }

    private static string ComposeRestoreInsert(ChangeSnapshot snapshot)
    {
        var change = snapshot.Change;
        if (snapshot.BeforeRows is null || snapshot.BeforeRows.Rows.Count == 0)
        {
            return $"""
                -- No rows were captured before {change.Kind} on {change.QuotedTable}.
                -- Original:
                -- {OneLine(change.OriginalSql)}
                """;
        }

        return BuildInsertFromRows(change, snapshot);
    }

    private static string ComposeUpdate(ChangeSnapshot snapshot)
    {
        var change = snapshot.Change;
        if (snapshot.BeforeRows is null || snapshot.BeforeRows.Rows.Count == 0)
        {
            return $"""
                -- No rows were captured before UPDATE on {change.QuotedTable}.
                -- Original:
                -- {OneLine(change.OriginalSql)}
                """;
        }

        if (snapshot.PrimaryKeyColumns.Count == 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"-- No primary key on {change.QuotedTable}. Restoring by deleting the affected set is unsafe.");
            builder.AppendLine("-- Captured previous rows:");
            builder.Append(BuildInsertFromRows(change, snapshot));
            return builder.ToString();
        }

        var statements = new List<string>();
        foreach (DataRow row in snapshot.BeforeRows.Rows)
        {
            var sets = new List<string>();
            foreach (DataColumn column in snapshot.BeforeRows.Columns)
            {
                if (snapshot.IdentityColumn is not null
                    && string.Equals(column.ColumnName, snapshot.IdentityColumn, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (snapshot.PrimaryKeyColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                sets.Add($"{ParsedChange.Quote(column.ColumnName)} = {Literal(row[column])}");
            }

            var keys = snapshot.PrimaryKeyColumns
                .Select(key =>
                {
                    var quoted = ParsedChange.Quote(key);
                    var value = row[key];
                    return IsDbNull(value) ? $"{quoted} IS NULL" : $"{quoted} = {Literal(value)}";
                });

            if (sets.Count == 0)
            {
                continue;
            }

            statements.Add(
                $"UPDATE {change.QuotedTable}{Environment.NewLine}" +
                $"SET {string.Join($",{Environment.NewLine}    ", sets)}{Environment.NewLine}" +
                $"WHERE {string.Join(" AND ", keys)};");
        }

        return statements.Count == 0
            ? BestEffortFor(change)
            : string.Join(Environment.NewLine + Environment.NewLine, statements);
    }

    private static string BuildInsertFromRows(ParsedChange change, ChangeSnapshot snapshot)
    {
        var table = snapshot.BeforeRows!;
        var columns = table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToList();
        var quotedColumns = string.Join(", ", columns.Select(ParsedChange.Quote));
        var values = table.Rows.Cast<DataRow>()
            .Select(row => "(" + string.Join(", ", columns.Select(column => Literal(row[column]))) + ")");
        var builder = new StringBuilder();
        if (snapshot.IdentityColumn is not null)
        {
            builder.AppendLine($"SET IDENTITY_INSERT {change.QuotedTable} ON;");
        }

        builder.AppendLine($"INSERT INTO {change.QuotedTable} ({quotedColumns})");
        builder.Append("VALUES");
        builder.AppendLine();
        builder.Append(string.Join("," + Environment.NewLine, values));
        builder.AppendLine(";");
        if (snapshot.IdentityColumn is not null)
        {
            builder.Append($"SET IDENTITY_INSERT {change.QuotedTable} OFF;");
        }

        if (table.Rows.Count >= MaxSnapshotRows)
        {
            builder.AppendLine();
            builder.Append($"-- Snapshot reached {MaxSnapshotRows} rows and may be incomplete.");
        }

        return builder.ToString();
    }

    private static string BestEffortFor(ParsedChange change)
    {
        return change.Kind switch
        {
            ChangeKind.SelectInto => $"DROP TABLE IF EXISTS {change.QuotedTable};",
            ChangeKind.Insert when change.InsertValueRows.Count > 0 => ComposeInsertValues(change, new ChangeSnapshot { Change = change }),
            _ => $"""
                -- Review and complete this rollback for {change.Kind} on {change.QuotedTable}.
                -- Original:
                -- {OneLine(change.OriginalSql)}
                """
        };
    }

    private static async Task<DataTable?> LoadRowsAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        var where = string.IsNullOrWhiteSpace(change.WhereSql) ? string.Empty : " " + change.WhereSql.Trim();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = $"SELECT TOP ({MaxSnapshotRows}) * FROM {change.QuotedTable}{where};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var table = DataTableLoader.Load(reader, convertBinaryToHex: false);
        return table;
    }

    private static async Task<IReadOnlyList<string>> LoadColumnNamesAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        if (change.IsTempTable)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.name
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@table)
              AND c.is_computed = 0
            ORDER BY c.column_id;
            """;
        command.Parameters.AddWithValue("@table", change.ObjectIdName);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<string>> LoadPrimaryKeyColumnsAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        if (change.IsTempTable)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table)
              AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal;
            """;
        command.Parameters.AddWithValue("@table", change.ObjectIdName);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<string?> LoadIdentityColumnAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        if (change.IsTempTable)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TOP (1) c.name
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@table)
              AND c.is_identity = 1;
            """;
        command.Parameters.AddWithValue("@table", change.ObjectIdName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string name ? name : null;
    }

    private static async Task<decimal> LoadIdentCurrentAsync(
        SqlConnection connection,
        ParsedChange change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IDENT_CURRENT(@table);";
        command.Parameters.AddWithValue("@table", change.ObjectIdName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    private static string Literal(object? value)
    {
        if (IsDbNull(value))
        {
            return "NULL";
        }

        return value switch
        {
            bool flag => flag ? "1" : "0",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            DateTime dateTime => $"'{dateTime:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset offset => $"'{offset:yyyy-MM-dd HH:mm:ss.fff}'",
            Guid guid => $"'{guid}'",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal) + "'"
        };
    }

    private static bool IsDbNull(object? value) => value is null or DBNull;

    private static bool IsNullLiteral(string sql) =>
        sql.Equals("NULL", StringComparison.OrdinalIgnoreCase);

    private static string FormatNumber(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string OneLine(string sql) =>
        sql.ReplaceLineEndings(" ").Trim();

    public sealed class RollbackCapture
    {
        internal RollbackCapture(IReadOnlyList<ChangeSnapshot> snapshots, string? databaseName)
        {
            Snapshots = snapshots;
            DatabaseName = databaseName;
        }

        internal IReadOnlyList<ChangeSnapshot> Snapshots { get; }

        internal string? DatabaseName { get; }
    }

    internal sealed class ChangeSnapshot
    {
        public required ParsedChange Change { get; init; }
        public DataTable? BeforeRows { get; set; }
        public IReadOnlyList<string> AllColumns { get; set; } = [];
        public IReadOnlyList<string> PrimaryKeyColumns { get; set; } = [];
        public string? IdentityColumn { get; set; }
        public decimal? IdentityBefore { get; set; }
    }
}
