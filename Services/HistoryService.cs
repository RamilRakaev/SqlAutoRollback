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

    public async Task AddAsync(string serverName, string script, int rowsAffected, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        var executedAt = DateTime.Now;

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
                    INSERT INTO ScriptHistory (ServerName, Script, ExecutedAt, RowsAffected)
                    VALUES ($server, $script, $executedAt, $rows);
                    """;
                command.Parameters.AddWithValue("$server", serverName);
                command.Parameters.AddWithValue("$script", script);
                command.Parameters.AddWithValue("$executedAt", executedAt.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$rows", rowsAffected);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        await AppendHistoryFileAsync(serverName, script, executedAt, cancellationToken);
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
                SELECT Id, ServerName, Script, ExecutedAt, RowsAffected
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
                entries.Add(new ScriptHistoryEntry
                {
                    Id = reader.GetInt64(0),
                    ServerName = reader.GetString(1),
                    Script = reader.GetString(2),
                    ExecutedAt = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    RowsAffected = reader.GetInt32(4)
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
                ExecutedAt TEXT NOT NULL,
                RowsAffected INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ScriptHistory_ServerName ON ScriptHistory(ServerName);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendHistoryFileAsync(
        string serverName,
        string script,
        DateTime executedAt,
        CancellationToken cancellationToken)
    {
        var path = AppPaths.GetHistoryFilePath(serverName);
        var block =
            $"-- {executedAt:dd.MM.yyyy HH:mm:ss}{Environment.NewLine}" +
            $"{script.Trim()}{Environment.NewLine}{Environment.NewLine}" +
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

            if (string.IsNullOrWhiteSpace(script))
            {
                continue;
            }

            entries.Add(new ScriptHistoryEntry
            {
                Id = ++id,
                ServerName = serverName,
                Script = script,
                ExecutedAt = executedAt,
                RowsAffected = -1
            });
        }

        entries.Reverse();
        return entries.Count <= 200 ? entries : entries.Take(200).ToList();
    }
}
