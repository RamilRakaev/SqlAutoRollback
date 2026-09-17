namespace SqlAutoRollback.Models;

public sealed class ScriptHistoryEntry
{
    public long Id { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public string Script { get; init; } = string.Empty;
    public string RollbackScript { get; init; } = string.Empty;
    public DateTime ExecutedAt { get; init; }
    public int RowsAffected { get; init; }
    public string ExecutedAtDisplay =>
        ExecutedAt == default ? string.Empty : ExecutedAt.ToString("dd.MM.yyyy HH:mm:ss");
    public string RowsAffectedDisplay => RowsAffected < 0 ? "—" : $"{RowsAffected} row(s) affected";
}
