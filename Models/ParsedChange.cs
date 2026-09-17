namespace SqlAutoRollback.Models;

public enum ChangeKind
{
    Insert,
    InsertSelect,
    Update,
    Delete,
    Merge,
    Truncate,
    BulkInsert,
    SelectInto
}

public sealed class ParsedChange
{
    public ChangeKind Kind { get; init; }
    public string Schema { get; init; } = "dbo";
    public string Table { get; init; } = string.Empty;
    public string? WhereSql { get; init; }
    public IReadOnlyList<string> InsertColumns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> InsertValueRows { get; init; } = [];
    public string OriginalSql { get; init; } = string.Empty;

    public string QuotedTable =>
        string.IsNullOrWhiteSpace(Schema)
            ? Quote(Table)
            : $"{Quote(Schema)}.{Quote(Table)}";

    public string ObjectIdName =>
        string.IsNullOrWhiteSpace(Schema) ? Table : $"{Schema}.{Table}";

    public bool IsTempTable => Table.StartsWith('#') || Table.StartsWith('@');

    public static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
