using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public sealed class SqlExecutionService : ISqlExecutionService
{
    private static readonly Regex GoSplitter = new(
        @"^\s*GO\s*(?:--.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RollbackScriptBuilder _rollback = new();

    public async Task<ServerInfo> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(16));";
        var version = await command.ExecuteScalarAsync(cancellationToken) as string;

        return new ServerInfo { ProductMajorVersion = version };
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        string connectionString,
        string script,
        CancellationToken cancellationToken = default)
    {
        var batches = SplitBatches(script);
        if (batches.Count == 0)
        {
            return new QueryExecutionResult
            {
                Success = true,
                Message = "Nothing to execute.",
                RowsAffected = 0
            };
        }

        var tables = new List<DataTable>();
        var messages = new List<string>();
        var totalAffected = 0;
        var hadAffected = false;
        var tracked = SqlChangeParser.ContainsTrackedChanges(script);
        var rollbackParts = new List<string>();
        var databases = new List<string>();

        await using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Message))
            {
                messages.Add(args.Message);
            }
        };

        await connection.OpenAsync(cancellationToken);

        try
        {
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDatabase = await GetDatabaseNameAsync(connection, cancellationToken);
                var batchDatabase = SqlChangeParser.LastUseDatabaseName(batch) ?? currentDatabase;
                if (SqlChangeParser.ContainsTrackedChanges(batch))
                {
                    RememberDatabase(databases, batchDatabase);
                }

                RollbackScriptBuilder.RollbackCapture? capture = null;
                if (tracked)
                {
                    try
                    {
                        capture = await _rollback.SnapshotBatchAsync(
                            connection,
                            batch,
                            batchDatabase,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        messages.Add("Rollback snapshot warning: " + ex.Message);
                    }
                }

                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 60;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        var table = DataTableLoader.Load(reader);
                        table.TableName = $"Result {tables.Count + 1}";
                        tables.Add(table);
                        messages.Add($"({table.Rows.Count} row{(table.Rows.Count == 1 ? "" : "s")} returned)");
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));

                if (reader.RecordsAffected >= 0)
                {
                    totalAffected += reader.RecordsAffected;
                    hadAffected = true;
                    messages.Add($"{reader.RecordsAffected} row(s) affected.");
                }

                await reader.CloseAsync();

                if (capture is not null)
                {
                    try
                    {
                        var part = await _rollback.ComposeAsync(connection, capture, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            rollbackParts.Insert(0, part);
                        }
                    }
                    catch (Exception ex)
                    {
                        messages.Add("Rollback compose warning: " + ex.Message);
                    }
                }
            }

            if (messages.Count == 0)
            {
                messages.Add("Command(s) completed successfully.");
            }

            return new QueryExecutionResult
            {
                Success = true,
                Message = string.Join(Environment.NewLine, messages),
                Tables = tables,
                RowsAffected = hadAffected ? totalAffected : -1,
                IsTrackedChange = tracked,
                RollbackScript = rollbackParts.Count == 0
                    ? null
                    : string.Join(Environment.NewLine + Environment.NewLine, rollbackParts),
                DatabaseName = string.Join(", ", databases)
            };
        }
        catch (SqlException ex)
        {
            messages.Add(FormatSqlError(ex));
            return new QueryExecutionResult
            {
                Success = false,
                Message = string.Join(Environment.NewLine, messages),
                Tables = tables,
                RowsAffected = hadAffected ? totalAffected : -1,
                IsTrackedChange = tracked,
                DatabaseName = string.Join(", ", databases)
            };
        }
        catch (Exception ex)
        {
            messages.Add(ex.Message);
            return new QueryExecutionResult
            {
                Success = false,
                Message = string.Join(Environment.NewLine, messages),
                Tables = tables,
                RowsAffected = hadAffected ? totalAffected : -1,
                IsTrackedChange = tracked,
                DatabaseName = string.Join(", ", databases)
            };
        }
    }

    private static List<string> SplitBatches(string script)
    {
        return GoSplitter
            .Split(script)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0)
            .ToList();
    }

    private static async Task<string?> GetDatabaseNameAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME();";
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static void RememberDatabase(List<string> databases, string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        if (databases.Any(name => string.Equals(name, databaseName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        databases.Add(databaseName);
    }

    private static string FormatSqlError(SqlException exception)
    {
        var builder = new StringBuilder();
        foreach (SqlError error in exception.Errors)
        {
            builder.Append("Msg ");
            builder.Append(error.Number);
            builder.Append(", Level ");
            builder.Append(error.Class);
            builder.Append(", State ");
            builder.Append(error.State);
            if (!string.IsNullOrWhiteSpace(error.Procedure))
            {
                builder.Append(", Procedure ");
                builder.Append(error.Procedure);
            }

            if (error.LineNumber > 0)
            {
                builder.Append(", Line ");
                builder.Append(error.LineNumber);
            }

            builder.AppendLine();
            builder.AppendLine(error.Message);
        }

        return builder.Length == 0 ? exception.Message : builder.ToString().TrimEnd();
    }
}
