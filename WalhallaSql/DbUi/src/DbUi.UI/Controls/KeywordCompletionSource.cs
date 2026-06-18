using ICSharpCode.AvalonEdit.CodeCompletion;

namespace DbUi.UI.Controls;

public static class KeywordCompletionSource
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

    public static IList<ICompletionData> GetCompletions(string prefix)
    {
        var upperPrefix = prefix.ToUpperInvariant();
        return Keywords
            .Where(k => k.StartsWith(upperPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k)
            .Select(k => (ICompletionData)new SqlCompletionData(k))
            .ToList();
    }
}
