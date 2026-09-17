using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AddAsync(
        string serverName,
        string script,
        string rollbackScript,
        string databaseName,
        int rowsAffected,
        CancellationToken cancellationToken = default)
    {
        if (!SqlChangeParser.ContainsTrackedChanges(script))
        {
            return;
        }

        AppPaths.EnsureCreated();
        var executedAt = DateTime.Now;
        rollbackScript ??= string.Empty;
        databaseName ??= string.Empty;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO ScriptHistory (ServerName, Script, RollbackScript, DatabaseName, ExecutedAt, RowsAffected)
                    VALUES ($server, $script, $rollback, $database, $executedAt, $rows);
                    """;
                command.Parameters.AddWithValue("$server", serverName);
                command.Parameters.AddWithValue("$script", script);
                command.Parameters.AddWithValue("$rollback", rollbackScript);
                command.Parameters.AddWithValue("$database", databaseName);
                command.Parameters.AddWithValue("$executedAt", executedAt.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$rows", rowsAffected);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        await AppendHistoryFileAsync(serverName, script, rollbackScript, databaseName, executedAt, cancellationToken);
    }

    public async Task<IReadOnlyList<ScriptHistoryEntry>> GetAsync(string serverName, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();

        var fileEntries = await TryReadHistoryFileAsync(serverName, cancellationToken);
        if (fileEntries.Count > 0)
        {
            return fileEntries;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Id, ServerName, Script, IFNULL(RollbackScript, ''), ExecutedAt, RowsAffected, IFNULL(DatabaseName, '')
                FROM ScriptHistory
                WHERE ServerName = $server
                ORDER BY Id DESC
                LIMIT 200;
                """;
            command.Parameters.AddWithValue("$server", serverName);

            var entries = new List<ScriptHistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var script = reader.GetString(2);
                if (!SqlChangeParser.ContainsTrackedChanges(script))
                {
                    continue;
                }

                entries.Add(new ScriptHistoryEntry
                {
                    Id = reader.GetInt64(0),
                    ServerName = reader.GetString(1),
                    Script = script,
                    RollbackScript = reader.GetString(3),
                    ExecutedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    RowsAffected = reader.GetInt32(5),
                    DatabaseName = reader.FieldCount > 6 ? reader.GetString(6) : string.Empty
                });
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SqliteConnection CreateConnection()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDatabase,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ScriptHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerName TEXT NOT NULL,
                Script TEXT NOT NULL,
                RollbackScript TEXT NOT NULL DEFAULT '',
                DatabaseName TEXT NOT NULL DEFAULT '',
                ExecutedAt TEXT NOT NULL,
                RowsAffected INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ScriptHistory_ServerName ON ScriptHistory(ServerName);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "RollbackScript", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "DatabaseName", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(ScriptHistory);";
        var exists = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE ScriptHistory ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendHistoryFileAsync(
        string serverName,
        string script,
        string rollbackScript,
        string databaseName,
        DateTime executedAt,
        CancellationToken cancellationToken)
    {
        var path = AppPaths.GetHistoryFilePath(serverName);
        var rollback = string.IsNullOrWhiteSpace(rollbackScript)
            ? RollbackScriptBuilder.BuildBestEffort(script, databaseName)
            : rollbackScript.Trim();
        rollback = RollbackScriptBuilder.PrefixUse(rollback, databaseName);
        var databaseLine = string.IsNullOrWhiteSpace(databaseName)
            ? string.Empty
            : $"-- Database: {databaseName}{Environment.NewLine}";
        var block =
            $"-- {executedAt:dd.MM.yyyy HH:mm:ss}{Environment.NewLine}" +
            databaseLine +
            $"-- Original:{Environment.NewLine}" +
            $"{script.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            $"-- Rollback:{Environment.NewLine}" +
            $"{rollback}{Environment.NewLine}{Environment.NewLine}" +
            $"GO{Environment.NewLine}{Environment.NewLine}";

        await File.AppendAllTextAsync(path, block, cancellationToken);
    }

    private static async Task<IReadOnlyList<ScriptHistoryEntry>> TryReadHistoryFileAsync(
        string serverName,
        CancellationToken cancellationToken)
    {
        var path = AppPaths.GetHistoryFilePath(serverName);
        if (!File.Exists(path))
        {
            return [];
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return ParseHistoryFile(serverName, content);
    }

    private static readonly Regex GoSplitter = new(
        @"^\s*GO\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<ScriptHistoryEntry> ParseHistoryFile(string serverName, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var entries = new List<ScriptHistoryEntry>();
        var blocks = GoSplitter.Split(content);
        long id = 0;

        foreach (var rawBlock in blocks)
        {
            var block = rawBlock.Trim();
            if (block.Length == 0)
            {
                continue;
            }

            var executedAt = default(DateTime);
            var script = block;
            var lineBreak = block.IndexOfAny(['\r', '\n']);
            var firstLine = lineBreak >= 0 ? block[..lineBreak] : block;

            if (firstLine.StartsWith("-- ", StringComparison.Ordinal))
            {
                var stamp = firstLine[3..].Trim();
                if (DateTime.TryParseExact(
                        stamp,
                        "dd.MM.yyyy HH:mm:ss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsed))
                {
                    executedAt = parsed;
                    script = lineBreak >= 0 ? block[(lineBreak + 1)..].Trim() : string.Empty;
                }
            }

            var rollback = string.Empty;
            var databaseName = string.Empty;
            const string databaseMarker = "-- Database:";
            const string originalMarker = "-- Original:";
            const string rollbackMarker = "-- Rollback:";

            var databaseIndex = script.IndexOf(databaseMarker, StringComparison.OrdinalIgnoreCase);
            var originalIndex = script.IndexOf(originalMarker, StringComparison.OrdinalIgnoreCase);
            var rollbackIndex = script.IndexOf(rollbackMarker, StringComparison.OrdinalIgnoreCase);

            if (databaseIndex >= 0)
            {
                var databaseLineEnd = script.IndexOfAny(['\r', '\n'], databaseIndex);
                var raw = databaseLineEnd >= 0
                    ? script[(databaseIndex + databaseMarker.Length)..databaseLineEnd]
                    : script[(databaseIndex + databaseMarker.Length)..];
                databaseName = raw.Trim();
            }

            if (originalIndex >= 0 && rollbackIndex > originalIndex)
            {
                var original = script[(originalIndex + originalMarker.Length)..rollbackIndex].Trim();
                rollback = script[(rollbackIndex + rollbackMarker.Length)..].Trim();
                script = original;
            }

            if (string.IsNullOrWhiteSpace(databaseName))
            {
                databaseName = string.Join(", ", SqlChangeParser.ExtractUseDatabaseNames(script));
            }

            if (string.IsNullOrWhiteSpace(script) || !SqlChangeParser.ContainsTrackedChanges(script))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(rollback))
            {
                rollback = RollbackScriptBuilder.BuildBestEffort(script, databaseName);
            }
            else
            {
                rollback = RollbackScriptBuilder.PrefixUse(rollback, databaseName);
            }

            entries.Add(new ScriptHistoryEntry
            {
                Id = ++id,
                ServerName = serverName,
                Script = script,
                RollbackScript = rollback,
                DatabaseName = databaseName,
                ExecutedAt = executedAt,
                RowsAffected = -1
            });
        }

        entries.Reverse();
        return entries.Count <= 200 ? entries : entries.Take(200).ToList();
    }
}
