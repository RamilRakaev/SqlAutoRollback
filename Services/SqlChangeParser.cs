using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public static class SqlChangeParser
{
    public static bool ContainsTrackedChanges(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var (changes, parsed) = TryParse(sql);
        return parsed ? changes.Count > 0 : MatchesFallback(sql);
    }

    public static IReadOnlyList<ParsedChange> Parse(string sql)
    {
        var (changes, _) = TryParse(sql);
        return changes;
    }

    public static (IReadOnlyList<ParsedChange> Changes, bool ParsedSuccessfully) TryParse(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return ([], true);
        }

        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors is { Count: > 0 } || fragment is not TSqlScript script)
        {
            return ([], false);
        }

        var changes = new List<ParsedChange>();
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                var change = Map(statement);
                if (change is not null)
                {
                    changes.Add(change);
                }
            }
        }

        return (changes, true);
    }

    public static IReadOnlyList<string> ExtractUseDatabaseNames(string sql)
    {
        var names = new List<string>();
        foreach (var name in EnumerateUseDatabaseNames(sql))
        {
            if (names.All(existing => !string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public static string? LastUseDatabaseName(string sql)
    {
        string? last = null;
        foreach (var name in EnumerateUseDatabaseNames(sql))
        {
            last = name;
        }

        return last;
    }

    private static IEnumerable<string> EnumerateUseDatabaseNames(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            yield break;
        }

        var parser = new TSql160Parser(false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out _);
        var found = false;
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                {
                    if (statement is not UseStatement use
                        || string.IsNullOrWhiteSpace(use.DatabaseName?.Value))
                    {
                        continue;
                    }

                    found = true;
                    yield return use.DatabaseName.Value;
                }
            }
        }

        if (found)
        {
            yield break;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     sql,
                     @"\bUSE\s+(?:\[(?<name>[^\]]+)\]|(?<name>[A-Za-z0-9_]+))",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var name = match.Groups["name"].Value;
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static ParsedChange? Map(TSqlStatement statement)
    {
        return statement switch
        {
            InsertStatement insert => MapInsert(insert),
            UpdateStatement update => MapUpdate(update),
            DeleteStatement delete => MapDelete(delete),
            MergeStatement merge => MapMerge(merge),
            TruncateTableStatement truncate => MapTruncate(truncate),
            BulkInsertStatement bulk => MapBulkInsert(bulk),
            SelectStatement select => MapSelectInto(select),
            _ => null
        };
    }

    private static ParsedChange? MapInsert(InsertStatement insert)
    {
        var spec = insert.InsertSpecification;
        if (!TryGetTable(spec.Target, null, out var schema, out var table))
        {
            return null;
        }

        var columns = spec.Columns
            .Select(column => column.MultiPartIdentifier.Identifiers[^1].Value)
            .ToList();

        if (spec.InsertSource is ValuesInsertSource values)
        {
            var rows = values.RowValues
                .Select(row => (IReadOnlyList<string>)row.ColumnValues.Select(GetSql).ToList())
                .ToList();

            return new ParsedChange
            {
                Kind = ChangeKind.Insert,
                Schema = schema,
                Table = table,
                InsertColumns = columns,
                InsertValueRows = rows,
                OriginalSql = GetSql(insert)
            };
        }

        return new ParsedChange
        {
            Kind = ChangeKind.InsertSelect,
            Schema = schema,
            Table = table,
            InsertColumns = columns,
            OriginalSql = GetSql(insert)
        };
    }

    private static ParsedChange? MapUpdate(UpdateStatement update)
    {
        var spec = update.UpdateSpecification;
        if (!TryGetTable(spec.Target, spec.FromClause?.TableReferences, out var schema, out var table))
        {
            return null;
        }

        return new ParsedChange
        {
            Kind = ChangeKind.Update,
            Schema = schema,
            Table = table,
            WhereSql = spec.WhereClause is null ? null : GetSql(spec.WhereClause),
            OriginalSql = GetSql(update)
        };
    }

    private static ParsedChange? MapDelete(DeleteStatement delete)
    {
        var spec = delete.DeleteSpecification;
        if (!TryGetTable(spec.Target, spec.FromClause?.TableReferences, out var schema, out var table))
        {
            return null;
        }

        return new ParsedChange
        {
            Kind = ChangeKind.Delete,
            Schema = schema,
            Table = table,
            WhereSql = spec.WhereClause is null ? null : GetSql(spec.WhereClause),
            OriginalSql = GetSql(delete)
        };
    }

    private static ParsedChange? MapMerge(MergeStatement merge)
    {
        if (!TryGetTable(merge.MergeSpecification.Target, null, out var schema, out var table))
        {
            return null;
        }

        return new ParsedChange
        {
            Kind = ChangeKind.Merge,
            Schema = schema,
            Table = table,
            OriginalSql = GetSql(merge)
        };
    }

    private static ParsedChange? MapTruncate(TruncateTableStatement truncate)
    {
        if (truncate.TableName is null)
        {
            return null;
        }

        var (schema, table) = SplitName(truncate.TableName);
        return new ParsedChange
        {
            Kind = ChangeKind.Truncate,
            Schema = schema,
            Table = table,
            OriginalSql = GetSql(truncate)
        };
    }

    private static ParsedChange? MapBulkInsert(BulkInsertStatement bulk)
    {
        if (bulk.To is null)
        {
            return null;
        }

        var (schema, table) = SplitName(bulk.To);
        return new ParsedChange
        {
            Kind = ChangeKind.BulkInsert,
            Schema = schema,
            Table = table,
            OriginalSql = GetSql(bulk)
        };
    }

    private static ParsedChange? MapSelectInto(SelectStatement select)
    {
        if (select.Into is null)
        {
            return null;
        }

        var (schema, table) = SplitName(select.Into);
        return new ParsedChange
        {
            Kind = ChangeKind.SelectInto,
            Schema = schema,
            Table = table,
            OriginalSql = GetSql(select)
        };
    }

    private static bool TryGetTable(
        TableReference? target,
        IList<TableReference>? fromTables,
        out string schema,
        out string table)
    {
        schema = "dbo";
        table = string.Empty;

        if (target is NamedTableReference named)
        {
            if (named.SchemaObject is not null)
            {
                (schema, table) = SplitName(named.SchemaObject);
                return !string.IsNullOrWhiteSpace(table);
            }

            var alias = named.Alias?.Value;
            if (!string.IsNullOrWhiteSpace(alias) && fromTables is not null)
            {
                foreach (var reference in fromTables)
                {
                    if (TryResolveAlias(reference, alias, out schema, out table))
                    {
                        return true;
                    }
                }
            }
        }

        if (fromTables is { Count: > 0 })
        {
            return TryGetTable(fromTables[0], null, out schema, out table);
        }

        return false;
    }

    private static bool TryResolveAlias(TableReference reference, string alias, out string schema, out string table)
    {
        schema = "dbo";
        table = string.Empty;
        switch (reference)
        {
            case NamedTableReference named
                when string.Equals(named.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase)
                     && named.SchemaObject is not null:
                (schema, table) = SplitName(named.SchemaObject);
                return true;
            case QualifiedJoin join:
                return TryResolveAlias(join.FirstTableReference, alias, out schema, out table)
                       || TryResolveAlias(join.SecondTableReference, alias, out schema, out table);
            default:
                return false;
        }
    }

    private static (string Schema, string Table) SplitName(SchemaObjectName name)
    {
        var table = name.BaseIdentifier?.Value ?? string.Empty;
        var schema = name.SchemaIdentifier?.Value;
        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = table.StartsWith('#') || table.StartsWith('@') ? string.Empty : "dbo";
        }

        return (schema, table);
    }

    private static string GetSql(TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream is null || fragment.FirstTokenIndex < 0 || fragment.LastTokenIndex < 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            builder.Append(fragment.ScriptTokenStream[i].Text);
        }

        return builder.ToString().Trim();
    }

    private static bool MatchesFallback(string sql)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
                sql,
                @"\b(INSERT|UPDATE|DELETE|MERGE)\b|\bTRUNCATE\s+TABLE\b|\bBULK\s+INSERT\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
                   sql,
                   @"\bSELECT\b[\s\S]*?\bINTO\b",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase)
               && !System.Text.RegularExpressions.Regex.IsMatch(
                   sql,
                   @"\bINSERT\s+INTO\b",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
