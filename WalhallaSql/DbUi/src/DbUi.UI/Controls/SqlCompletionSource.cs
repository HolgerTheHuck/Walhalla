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

        if (snapshot is null)
        {
            completions.AddRange(Keywords
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k)
                .Select(k => new SqlCompletionData(k, "keyword")));
            return completions;
        }

        switch (context.Kind)
        {
            case CompletionContextKind.Table:
                completions.AddRange(snapshot.Tables
                    .Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name)
                    .Select(t => new SqlCompletionData(t.Name, "table")));
                break;

            case CompletionContextKind.Column:
                var table = ResolveTableForAlias(snapshot, context.Alias, textBeforeCaret);
                var columns = table?.Columns
                    ?? snapshot.Tables.SelectMany(t => t.Columns);

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
                completions.AddRange(Keywords
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k)
                    .Select(k => new SqlCompletionData(k, "keyword")));

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
        var i = textBeforeCaret.Length - 1;
        while (i >= 0 && IsWordChar(textBeforeCaret[i]))
            i--;

        var prefixStart = i + 1;
        var lastDot = textBeforeCaret.LastIndexOf('.', textBeforeCaret.Length - 1);
        if (lastDot > prefixStart - 1)
            prefixStart = lastDot + 1;

        return textBeforeCaret.Substring(prefixStart);
    }

    private static CompletionContext DetectContext(string textBeforeCaret)
    {
        if (Regex.IsMatch(textBeforeCaret, @"(?i)\bEXEC(UTE)?\s+[\w\[.]*$"))
            return new CompletionContext(CompletionContextKind.Procedure);

        var aliasMatch = Regex.Match(textBeforeCaret, @"(?i)(\b[a-zA-Z_][\w@#$]*)\.$");
        if (aliasMatch.Success)
            return new CompletionContext(CompletionContextKind.Column, aliasMatch.Groups[1].Value);

        if (Regex.IsMatch(textBeforeCaret, @"(?i)\b(FROM|JOIN|INTO|UPDATE|DELETE\s+FROM)\s+[\w\[.]*$"))
            return new CompletionContext(CompletionContextKind.Table);

        if (Regex.IsMatch(textBeforeCaret, @"(?i)\b(SELECT|WHERE|ORDER\s+BY|GROUP\s+BY|HAVING|SET)\s+[^,]*$"))
            return new CompletionContext(CompletionContextKind.Column);

        return new CompletionContext(CompletionContextKind.General);
    }

    private static CatalogTable? ResolveTableForAlias(CatalogSnapshot snapshot, string? alias, string textBeforeCaret)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return null;

        var direct = snapshot.Tables.FirstOrDefault(t =>
            string.Equals(t.Name, alias, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        var aliases = ExtractAliases(textBeforeCaret);
        if (aliases.TryGetValue(alias, out var tableName))
        {
            return snapshot.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Extrahiert aus dem SQL-Text bis zum Caret eine Abbildung alias -> tabellenname
    /// fuer alle im aktuellen FROM/JOIN-Block vorkommenden Tabellenreferenzen.
    /// </summary>
    private static Dictionary<string, string> ExtractAliases(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var fromIndex = FindLastFrom(text);
        if (fromIndex < 0)
            return result;

        var fromPart = ExtractFromClause(text, fromIndex);
        if (string.IsNullOrWhiteSpace(fromPart))
            return result;

        foreach (var reference in SplitTableReferences(fromPart))
        {
            var parsed = ParseTableReference(reference);
            if (parsed.TableName is null)
                continue;

            if (!string.IsNullOrWhiteSpace(parsed.Alias))
                result[parsed.Alias] = parsed.TableName;

            result[parsed.TableName] = parsed.TableName;
        }

        return result;
    }

    private static int FindLastFrom(string text)
    {
        var upper = text.ToUpperInvariant();
        var index = upper.Length;
        while ((index = upper.LastIndexOf("FROM", index - 1, StringComparison.Ordinal)) >= 0)
        {
            var before = index - 1;
            var after = index + 4;

            var boundaryBefore = before < 0 || char.IsWhiteSpace(text[before]) || text[before] == '(';
            var boundaryAfter = after >= text.Length || char.IsWhiteSpace(text[after]);

            if (boundaryBefore && boundaryAfter)
                return index;
        }

        return -1;
    }

    private static string ExtractFromClause(string text, int fromIndex)
    {
        var end = text.Length;
        var keywords = new[] { "WHERE", "GROUP", "HAVING", "ORDER" };
        var upper = text.ToUpperInvariant();

        foreach (var kw in keywords)
        {
            var idx = upper.IndexOf(" " + kw + " ", fromIndex + 4, StringComparison.Ordinal);
            if (idx < 0)
                idx = upper.IndexOf("\t" + kw + " ", fromIndex + 4, StringComparison.Ordinal);
            if (idx < 0)
                idx = upper.IndexOf(" " + kw + "\t", fromIndex + 4, StringComparison.Ordinal);
            if (idx < 0)
                idx = upper.IndexOf("\t" + kw + "\t", fromIndex + 4, StringComparison.Ordinal);

            if (idx >= 0 && idx < end)
                end = idx;
        }

        return end <= fromIndex + 4 ? string.Empty : text.Substring(fromIndex + 4, end - fromIndex - 4).Trim();
    }

    private static string[] SplitTableReferences(string fromClause)
    {
        var parts = new List<string>();
        var start = 0;

        for (var i = 0; i < fromClause.Length; i++)
        {
            var c = fromClause[i];

            if (c == '[')
            {
                var end = fromClause.IndexOf(']', i);
                if (end < 0)
                {
                    i = fromClause.Length;
                    continue;
                }
                i = end;
                continue;
            }

            if (c == '\'' || c == '"')
            {
                var end = fromClause.IndexOf(c, i + 1);
                if (end < 0)
                {
                    i = fromClause.Length;
                    continue;
                }
                i = end;
                continue;
            }

            if (c == ',')
            {
                AddPart(parts, fromClause, start, i);
                start = i + 1;
                continue;
            }

            if (IsJoinAt(fromClause, i, out var joinLength))
            {
                AddPart(parts, fromClause, start, i);
                start = i + joinLength;
                i = start - 1;
            }
        }

        AddPart(parts, fromClause, start, fromClause.Length);
        return parts.ToArray();
    }

    private static bool IsJoinAt(string text, int index, out int length)
    {
        length = 0;
        var upper = text.ToUpperInvariant();

        var starts = new[] { "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN" };
        foreach (var s in starts)
        {
            if (index + s.Length <= text.Length &&
                upper.Substring(index, s.Length) == s &&
                (index + s.Length == text.Length || char.IsWhiteSpace(text[index + s.Length])))
            {
                length = s.Length;
                return true;
            }
        }

        if (index + 4 <= text.Length &&
            upper.Substring(index, 4) == "JOIN" &&
            (index == 0 || char.IsWhiteSpace(text[index - 1])) &&
            (index + 4 == text.Length || char.IsWhiteSpace(text[index + 4])))
        {
            length = 4;
            return true;
        }

        return false;
    }

    private static void AddPart(List<string> parts, string text, int start, int end)
    {
        if (end <= start) return;
        var part = text.Substring(start, end - start).Trim();
        if (part.Length > 0) parts.Add(part);
    }

    private static (string? TableName, string? Alias) ParseTableReference(string text)
    {
        var onIndex = text.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        if (onIndex >= 0)
            text = text.Substring(0, onIndex).Trim();

        var upper = text.ToUpperInvariant();
        var asIndex = upper.IndexOf(" AS ", StringComparison.Ordinal);

        string? alias = null;
        string tablePart;

        if (asIndex >= 0)
        {
            tablePart = text.Substring(0, asIndex).Trim();
            alias = text.Substring(asIndex + 4).Trim();
        }
        else
        {
            var tokens = Tokenize(text);
            if (tokens.Count >= 2)
            {
                var last = tokens[^1];
                var first = tokens[0];

                if (IsPotentialAlias(last, first))
                {
                    tablePart = string.Join(" ", tokens.Take(tokens.Count - 1));
                    alias = last;
                }
                else
                {
                    tablePart = text;
                }
            }
            else
            {
                tablePart = text;
            }
        }

        var tableName = Unquote(tablePart);
        alias = alias is not null ? Unquote(alias) : null;

        return (tableName, alias);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[')
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                var end = text.IndexOf(']', i);
                if (end < 0) { tokens.Add(text.Substring(i)); return tokens; }
                tokens.Add(text.Substring(i, end - i + 1));
                i = end;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool IsPotentialAlias(string candidate, string tableName)
    {
        if (candidate.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            return false;

        return !ReservedWords.Contains(candidate.ToUpperInvariant()) &&
               !candidate.StartsWith("(", StringComparison.Ordinal);
    }

    private static string Unquote(string name)
    {
        name = name.Trim();
        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
            return name.Substring(1, name.Length - 2).Trim();
        return name;
    }

    private static readonly HashSet<string> ReservedWords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER", "OUTER", "LEFT", "RIGHT", "FULL",
        "CROSS", "ON", "AND", "OR", "NOT", "NULL", "IS", "AS", "ORDER", "GROUP", "BY",
        "HAVING", "TOP", "DISTINCT", "UNION", "ALL", "INSERT", "UPDATE", "DELETE",
        "VALUES", "SET", "CREATE", "ALTER", "DROP", "TABLE", "INDEX", "PROCEDURE", "PROC",
        "TRIGGER", "VIEW", "FUNCTION", "DATABASE", "WITH", "CASE", "WHEN", "THEN", "ELSE",
        "END", "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "GO", "INTO", "EXEC", "EXECUTE",
        "LIKE", "IN", "BETWEEN", "EXISTS"
    ];

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
