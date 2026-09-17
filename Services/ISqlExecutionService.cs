using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public interface ISqlExecutionService
{
    Task<ServerInfo> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);

    Task<QueryExecutionResult> ExecuteAsync(string connectionString, string script, CancellationToken cancellationToken = default);
}
