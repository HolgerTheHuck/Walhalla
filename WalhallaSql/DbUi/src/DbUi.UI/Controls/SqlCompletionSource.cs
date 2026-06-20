using System.Text.RegularExpressions;
using DbUi.Core.Catalog;
using ICSharpCode.AvalonEdit.CodeCompletion;

namespace DbUi.UI.Controls;

public static class SqlCompletionSource
{
    private static readonly string[] Keywords =
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC",
        "BEGIN", "BETWEEN", "BREAK", "BY",
        "CASCADE", "CASE", "CHECK", "CLOSE", "CLUSTERED", "COALESCE", "COLUMN",
        "COMMIT", "CONSTRAINT", "CONTINUE", "CONVERT", "CREATE", "CROSS",
        "DATABASE", "DECLARE", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "END", "EXCEPT", "EXEC", "EXECUTE", "EXISTS",
        "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION",
        "GO", "GOTO", "GRANT", "GROUP",
        "HAVING",
        "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS",
        "JOIN",
        "KEY", "KILL",
        "LEFT", "LIKE",
        "MERGE",
        "NOCHECK", "NONCLUSTERED", "NOT", "NULL", "NULLIF",
        "OF", "OFF", "ON", "OPEN", "OR", "ORDER", "OUTER", "OVER",
        "PERCENT", "PIVOT", "PRIMARY", "PRINT", "PROC", "PROCEDURE",
        "RAISERROR", "READ", "RECONFIGURE", "REFERENCES", "RESTORE", "RETURN", "REVERT",
        "REVOKE", "RIGHT", "ROLLBACK",
        "SAVE", "SCHEMA", "SELECT", "SESSION_USER", "SET", "SHUTDOWN", "SOME",
        "TABLE", "THEN", "TO", "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE",
        "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "USE", "USER",
        "VALUES", "VIEW",
        "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH",
        // Data types
        "BIGINT", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2", "DECIMAL", "FLOAT",
        "INT", "MONEY", "NCHAR", "NVARCHAR", "NUMERIC", "REAL", "SMALLINT",
        "TINYINT", "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML",
        // Functions
        "AVG", "CAST", "CEILING", "CHARINDEX", "COALESCE", "CONCAT", "COUNT",
        "DATEDIFF", "DATEADD", "DATENAME", "DATEPART", "DAY", "DENSE_RANK",
        "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE", "IIF", "ISNULL", "ISNUMERIC",
        "LAG", "LEAD", "LEN", "LOWER", "LTRIM", "MAX", "MIN", "MONTH",
        "NEWID", "NTILE", "NULLIF", "OBJECT_ID", "PATINDEX", "RANK",
        "REPLACE", "REPLICATE", "REVERSE", "ROUND", "ROW_NUMBER", "RTRIM",
        "SCOPE_IDENTITY", "SPACE", "STUFF", "SUBSTRING", "SUM", "SYSDATETIME",
        "TRIM", "TRY_CAST", "TRY_CONVERT", "UPPER", "YEAR",
    ];

    public static IList<ICompletionData> GetCompletions(
        string textBeforeCaret,
        CatalogSnapshot? snapshot)
    {
        var prefix = ExtractPrefix(textBeforeCaret);
        var context = DetectContext(textBeforeCaret);

        var completions = new List<ICompletionData>();

        // Always include keywords that match the prefix.
        completions.AddRange(Keywords
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .Select(k => new SqlCompletionData(k, "keyword")));

        if (snapshot is null)
            return completions;

        switch (context.Kind)
        {
            case CompletionContextKind.Table:
                completions.AddRange(snapshot.Tables
                    .Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name)
                    .Select(t => new SqlCompletionData(t.Name, "table")));
                break;

            case CompletionContextKind.Column:
                var table = FindTableForAlias(snapshot, context.Alias);
                var columns = table?.Columns ?? snapshot.Tables.SelectMany(t => t.Columns).Distinct();
                completions.AddRange(columns
                    .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c)
                    .Select(c => new SqlCompletionData(c, "column")));
                break;

            case CompletionContextKind.Procedure:
                completions.AddRange(snapshot.Procedures
                    .Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Name)
                    .Select(p => new SqlCompletionData(p.Name, "procedure")));
                break;

            case CompletionContextKind.General:
            default:
                // General context: tables + columns + procedures as extra suggestions.
                completions.AddRange(snapshot.Tables
                    .Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name)
                    .Select(t => new SqlCompletionData(t.Name, "table")));
                completions.AddRange(snapshot.Procedures
                    .Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Name)
                    .Select(p => new SqlCompletionData(p.Name, "procedure")));
                break;
        }

        return completions;
    }

    internal static string ExtractPrefix(string textBeforeCaret)
    {
        // Walk back from end until we hit a non-word character.
        var i = textBeforeCaret.Length - 1;
        while (i >= 0 && IsWordChar(textBeforeCaret[i]))
            i--;
        return textBeforeCaret.Substring(i + 1);
    }

    private static CompletionContext DetectContext(string textBeforeCaret)
    {
        var upper = textBeforeCaret.ToUpperInvariant();

        // EXEC / EXECUTE
        if (Regex.IsMatch(textBeforeCaret, @"(?i)\bEXEC(UTE)?\s+[\w\[.]*$"))
            return new CompletionContext(CompletionContextKind.Procedure);

        // alias.column
        var aliasMatch = Regex.Match(textBeforeCaret, @"(?i)(\b[a-zA-Z_][\w@#$]*)\.$");
        if (aliasMatch.Success)
            return new CompletionContext(CompletionContextKind.Column, aliasMatch.Groups[1].Value);

        // FROM / JOIN / INTO / UPDATE / DELETE FROM
        if (Regex.IsMatch(textBeforeCaret, @"(?i)\b(FROM|JOIN|INTO|UPDATE|DELETE\s+FROM)\s+[\w\[.]*$"))
            return new CompletionContext(CompletionContextKind.Table);

        // SELECT / WHERE / ORDER BY / GROUP BY / HAVING
        if (Regex.IsMatch(textBeforeCaret, @"(?i)\b(SELECT|WHERE|ORDER\s+BY|GROUP\s+BY|HAVING|SET)\s+[^,]*$"))
            return new CompletionContext(CompletionContextKind.Column);

        return new CompletionContext(CompletionContextKind.General);
    }

    private static CatalogTable? FindTableForAlias(CatalogSnapshot snapshot, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return null;

        // Naive alias resolution: if the alias matches a table name exactly, use it.
        // TODO: parse FROM clause aliases for accurate mapping.
        return snapshot.Tables.FirstOrDefault(t =>
            string.Equals(t.Name, alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';

    private readonly record struct CompletionContext(CompletionContextKind Kind, string? Alias = null);

    private enum CompletionContextKind
    {
        General,
        Table,
        Column,
        Procedure,
    }
}
