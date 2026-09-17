using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public interface IHistoryService
{
    Task AddAsync(
        string serverName,
        string script,
        string rollbackScript,
        int rowsAffected,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScriptHistoryEntry>> GetAsync(string serverName, CancellationToken cancellationToken = default);
}
