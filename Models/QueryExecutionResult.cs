using System.Data;

namespace SqlAutoRollback.Models;

public sealed class QueryExecutionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<DataTable> Tables { get; init; } = [];
    public int RowsAffected { get; init; }
}
