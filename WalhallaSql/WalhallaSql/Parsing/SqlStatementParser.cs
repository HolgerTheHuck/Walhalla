using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WalhallaSql.Sql;

namespace WalhallaSql.Parsing;

internal static class SqlStatementParser
{
    private static readonly HashSet<string> ValidIsolationLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "READ UNCOMMITTED", "READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE"
    };

    public static SqlStatement Parse(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL must not be empty.", nameof(sql));

        var normalized = SqlSyntaxText.RemoveTrailingSemicolon(sql).Trim();

        if (SqlSyntaxText.StartsWithKeyword(normalized, "EXPLAIN"))
            return ParseExplain(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "SAVEPOINT"))
            return new SqlSavepointStatement(normalized[9..].Trim());

        if (SqlSyntaxText.StartsWithKeyword(normalized, "ROLLBACK TO"))
            return new SqlRollbackToStatement(normalized[11..].Trim());

        if (SqlSyntaxText.StartsWithKeyword(normalized, "RELEASE SAVEPOINT"))
            return new SqlReleaseSavepointStatement(normalized[17..].Trim());

        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "BEGIN TRANSACTION"))
            return new SqlBeginTransactionStatement();
        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "BEGIN")
            && (normalized.Length == 5 || normalized[5..].Trim().Length == 0))
            return new SqlBeginTransactionStatement();

        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "COMMIT"))
            return new SqlCommitStatement();

        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "ROLLBACK")
            && !SqlSyntaxText.StartsWithKeyword(normalized, "ROLLBACK TO"))
            return new SqlRollbackStatement();

        if (SqlSyntaxText.StartsWithKeyword(normalized, "WITH"))
        {
            var isRecursive = IsWithRecursive(normalized);
            return ParseWithStatement(normalized, isRecursive);
        }

        if (SqlSyntaxText.StartsWithKeyword(normalized, "SELECT"))
            return ParseSelectStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "INSERT"))
            return ParseInsert(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "CREATE TABLE"))
            return ParseCreateTable(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DROP TABLE"))
            return ParseDropTable(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "CREATE INDEX")
            || SqlSyntaxText.StartsWithKeyword(normalized, "CREATE UNIQUE INDEX"))
            return ParseCreateIndex(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DROP INDEX"))
            return ParseDropIndex(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "UPDATE"))
            return ParseUpdate(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DELETE"))
            return ParseDelete(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "ALTER TABLE"))
            return ParseAlterTable(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "CREATE VIEW"))
            return ParseCreateView(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DROP VIEW"))
            return ParseDropView(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "CREATE PROCEDURE")
            || SqlSyntaxText.StartsWithKeyword(normalized, "CREATE OR REPLACE PROCEDURE"))
            return ParseCreateProcedureStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DROP PROCEDURE"))
            return ParseDropProcedureStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "EXEC")
            || SqlSyntaxText.StartsWithKeyword(normalized, "EXECUTE"))
            return ParseExecStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "CREATE TRIGGER")
            || SqlSyntaxText.StartsWithKeyword(normalized, "CREATE OR REPLACE TRIGGER"))
            return ParseCreateTriggerStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DROP TRIGGER"))
            return ParseDropTriggerStatement(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "TRUNCATE TABLE"))
            return ParseTruncateTable(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "SET WALHALLA.TRANSACTION_MODE"))
            return ParseSetTransactionMode(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "SET TRANSACTION"))
            return ParseSetTransaction(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "DESCRIBE"))
            return ParseDescribe(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "MERGE"))
            return ParseMerge(normalized);

        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "VACUUM"))
            return ParseVacuum(normalized);

        if (SqlSyntaxText.MatchesKeywordAt(normalized, 0, "ANALYZE"))
            return ParseAnalyze(normalized);

        if (SqlSyntaxText.StartsWithKeyword(normalized, "COPY"))
            return ParseCopy(normalized);

        throw new NotSupportedException($"SQL statement type not supported: '{normalized[..Math.Min(normalized.Length, 80)]}'.");
    }

    private static SqlStatement ParseWithStatement(string sql, bool isRecursive)
    {
        // WITH [RECURSIVE] cte1 AS (SELECT ...), cte2 AS (SELECT ...) SELECT ...
        var afterWith = "WITH".Length;
        while (afterWith < sql.Length && char.IsWhiteSpace(sql[afterWith])) afterWith++;
        if (isRecursive)
            afterWith += "RECURSIVE".Length;

        var selectIdx = FindTopLevelKeywordSkipCtes(sql, afterWith);

        if (selectIdx < 0)
            throw new NotSupportedException("WITH clause requires a following SELECT statement.");

        var cteText = sql[afterWith..selectIdx].Trim();
        var mainSql = sql[selectIdx..];

        // Parse CTE definitions: name AS (SELECT ...) [, name AS (SELECT ...)]
        var ctes = new List<SqlCteDefinition>();
        var pos = 0;
        while (pos < cteText.Length)
        {
            while (pos < cteText.Length && char.IsWhiteSpace(cteText[pos])) pos++;
            if (pos >= cteText.Length) break;

            // Parse CTE name
            var nameEnd = cteText.IndexOf(' ', pos);
            if (nameEnd < 0) break;
            var cteName = SqlSyntaxText.NormalizeIdentifier(cteText[pos..nameEnd]);
            pos = nameEnd + 1;

            // Skip "AS"
            while (pos < cteText.Length && char.IsWhiteSpace(cteText[pos])) pos++;
            if (pos + 2 <= cteText.Length &&
                string.Compare(cteText, pos, "AS", 0, 2, StringComparison.OrdinalIgnoreCase) == 0)
                pos += 2;

            // Find opening paren
            while (pos < cteText.Length && char.IsWhiteSpace(cteText[pos])) pos++;
            if (pos >= cteText.Length || cteText[pos] != '(') break;

            var subEnd = SqlSyntaxText.FindMatchingParen(cteText, pos);
            if (subEnd < 0) break;

            var subSql = cteText[(pos + 1)..subEnd];
            SqlStatement body;
            if (isRecursive)
            {
                body = ParseSelectStatement(subSql);
                if (body is not SqlCompoundSelectStatement compound ||
                    (compound.Operator != SqlSetOperator.Union && compound.Operator != SqlSetOperator.UnionAll))
                    throw new NotSupportedException("Recursive CTE body must be a UNION or UNION ALL of two SELECT statements.");
            }
            else
            {
                body = ParseSelect(subSql);
            }
            ctes.Add(new SqlCteDefinition(cteName, body));

            pos = subEnd + 1;
            // Skip comma between CTEs
            while (pos < cteText.Length && char.IsWhiteSpace(cteText[pos])) pos++;
            if (pos < cteText.Length && cteText[pos] == ',') pos++;
        }

        var mainStmt = ParseSelectStatement(mainSql);
        return new SqlWithStatement(ctes, mainStmt, isRecursive);
    }

    private static bool IsWithRecursive(string sql)
    {
        var pos = "WITH".Length;
        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
        return pos + "RECURSIVE".Length <= sql.Length &&
               string.Compare(sql, pos, "RECURSIVE", 0, "RECURSIVE".Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static int FindTopLevelKeywordSkipCtes(string s, int start)
    {
        // Skip past CTE definitions (parenthesized SELECTs) to find the main SELECT
        var depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '(') { depth++; continue; }
            if (s[i] == ')') { depth--; continue; }
            if (s[i] == '\'') { i++; while (i < s.Length && s[i] != '\'') i++; continue; }

            if (depth == 0 && IsKeywordAt(s, i, "SELECT"))
                return i;
        }
        return -1;
    }

    private static SqlStatement ParseSelectStatement(string sql)
    {
        // Check for top-level set operators (UNION, EXCEPT, INTERSECT)
        var (setOpIdx, setOp, setOpLen) = FindTopLevelSetOperator(sql);
        if (setOpIdx >= 0)
        {
            var leftSql = sql[..setOpIdx].Trim();
            var rightSql = sql[(setOpIdx + setOpLen)..].TrimStart();

            var left = ParseSingleSelect(leftSql);
            // Right side may itself be compound, so recurse
            var rightStmt = ParseSelectStatement(rightSql);
            if (rightStmt is not SqlSelectStatement right)
                throw new NotSupportedException("Right side of set operator must be a SELECT.");

            return new SqlCompoundSelectStatement(left, setOp, right);
        }

        return ParseSingleSelect(sql);
    }

    private static (int Index, SqlSetOperator Op, int Length) FindTopLevelSetOperator(string sql)
    {
        var depth = 0;
        for (int i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '(') { depth++; continue; }
            if (sql[i] == ')') { depth--; continue; }
            if (sql[i] == '\'') { i++; while (i < sql.Length && sql[i] != '\'') i++; continue; }

            if (depth == 0)
            {
                if (IsKeywordAt(sql, i, "UNION"))
                {
                    // Check for UNION ALL
                    var afterKw = i + 5;
                    if (afterKw + 3 < sql.Length &&
                        IsKeywordAt(sql, SkipWhitespace2(sql, afterKw), "ALL"))
                        return (i, SqlSetOperator.UnionAll, SkipWhitespace2(sql, afterKw) + 3 - i);
                    return (i, SqlSetOperator.Union, 5);
                }
                if (IsKeywordAt(sql, i, "EXCEPT"))
                    return (i, SqlSetOperator.Except, 6);
                if (IsKeywordAt(sql, i, "INTERSECT"))
                    return (i, SqlSetOperator.Intersect, 9);
            }
        }
        return (-1, default, 0);
    }

    private static bool IsKeywordAt(string s, int pos, string keyword)
    {
        if (pos + keyword.Length > s.Length) return false;
        if (string.Compare(s, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var end = pos + keyword.Length;
        return end >= s.Length || !char.IsLetterOrDigit(s[end]);
    }

    private static int SkipWhitespace2(string s, int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        return pos;
    }

    /// <summary>Parses a single non-compound SELECT.</summary>
    private static SqlSelectStatement ParseSingleSelect(string sql)
    {
        return ParseSelect(sql);
    }

    private static SqlSelectStatement ParseSelect(string sql)
    {
        sql = sql.TrimStart();
        var afterSelect = "SELECT".Length;
        var isDistinct = false;

        // Check for DISTINCT after SELECT
        if (sql.Length > afterSelect + 9 && sql.AsSpan(afterSelect).TrimStart().StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            var distinctIdx = sql.IndexOf("DISTINCT", afterSelect, StringComparison.OrdinalIgnoreCase);
            if (distinctIdx >= 0)
            {
                isDistinct = true;
                afterSelect = distinctIdx + "DISTINCT".Length;
            }
        }

        // Check for TOP (N) or TOP N after SELECT / DISTINCT
        if (sql.Length > afterSelect + 4 && sql.AsSpan(afterSelect).TrimStart().StartsWith("TOP", StringComparison.OrdinalIgnoreCase))
        {
            var topIdx = sql.IndexOf("TOP", afterSelect, StringComparison.OrdinalIgnoreCase);
            if (topIdx >= 0)
            {
                afterSelect = topIdx + "TOP".Length;
                // Skip whitespace
                while (afterSelect < sql.Length && char.IsWhiteSpace(sql[afterSelect])) afterSelect++;
                // Skip optional parenthesized number: (1000)
                if (afterSelect < sql.Length && sql[afterSelect] == '(')
                {
                    var parenClose = SqlSyntaxText.FindMatchingParen(sql, afterSelect);
                    if (parenClose > 0)
                        afterSelect = parenClose + 1;
                }
                else
                {
                    // Skip plain number: 1000
                    while (afterSelect < sql.Length && char.IsDigit(sql[afterSelect])) afterSelect++;
                }
            }
        }

        var fromIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "FROM", afterSelect);
        if (fromIdx < 0)
            throw new NotSupportedException("SELECT requires a FROM clause.");

        // Parse table name between FROM and WHERE/end
        var afterFrom = fromIdx + "FROM".Length;

        // Parse optional WINDOW clause (named window definitions) up front so columns can resolve OVER w.
        var namedWindows = ParseWindowClause(sql, afterFrom);

        // Parse columns between SELECT and FROM
        var projectionText = sql[afterSelect..fromIdx].Trim();
        var columns = ParseSelectColumns(projectionText, namedWindows);

        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "WHERE", afterFrom);

        // Also look for JOIN between FROM and WHERE/end
        var baseTableEnd = whereIdx >= 0 ? whereIdx : sql.Length;

        // Also stop at GROUP BY, HAVING, WINDOW, ORDER BY, LIMIT, OFFSET, FETCH when extracting table name.
        foreach (var kw in new[] { "GROUP BY", "HAVING", "WINDOW", "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, afterFrom);
            if (idx >= 0) baseTableEnd = Math.Min(baseTableEnd, idx);
        }

        var firstJoinIdx = FindJoinKeyword(sql, afterFrom, baseTableEnd);

        var tableSegmentEnd = firstJoinIdx >= 0 ? firstJoinIdx : baseTableEnd;
        var tableSegment = sql[afterFrom..tableSegmentEnd].Trim();

        SqlSelectStatement? derivedTable = null;
        string tableName;
        string? alias = null;

        // Check for derived table: FROM (SELECT ...) AS alias
        if (tableSegment.StartsWith("("))
        {
            var subEnd = SqlSyntaxText.FindMatchingParen(tableSegment, 0);
            if (subEnd < 0)
                throw new NotSupportedException("Unclosed derived table parentheses.");

            var subquerySql = tableSegment[1..subEnd];
            derivedTable = ParseSelect(subquerySql);

            // Parse alias after closing paren: "AS alias" or just "alias"
            var afterParen = tableSegment[(subEnd + 1)..].TrimStart();
            if (afterParen.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                afterParen = afterParen[2..].TrimStart();
            var aliasEnd = afterParen.IndexOf(' ');
            alias = aliasEnd > 0 ? afterParen[..aliasEnd] : afterParen;
            if (string.IsNullOrWhiteSpace(alias))
                throw new NotSupportedException("Derived table requires an alias.");

            tableName = alias; // Use alias as the table name for the outer query
        }
        else
        {
            if (!SqlSyntaxText.TryParseSingleTableSource(tableSegment, out tableName, out alias))
                throw new NotSupportedException($"Cannot parse table name from '{tableSegment}'.");
        }

        // Parse optional JOINs (they come BEFORE WHERE in the SQL text)
        var searchStart = firstJoinIdx >= 0 ? firstJoinIdx : (whereIdx >= 0 ? whereIdx : sql.Length);
        var endBeforeWhere = whereIdx >= 0 ? whereIdx : sql.Length;
        foreach (var kw in new[] { "GROUP BY", "HAVING", "WINDOW", "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, afterFrom);
            if (idx >= 0) endBeforeWhere = Math.Min(endBeforeWhere, idx);
        }
        var joins = ParseJoinClauses(sql, searchStart, endBeforeWhere);

        // Parse optional WHERE clause (after all JOINs)
        SqlWhereExpression? where = null;
        IReadOnlyList<string>? parameters = null;
        var afterWhere = whereIdx >= 0 ? whereIdx + "WHERE".Length : endBeforeWhere;

        if (whereIdx >= 0)
        {
            var whereEnd = sql.Length;
            foreach (var kw in new[] { "GROUP BY", "HAVING", "WINDOW", "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
            {
                var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, whereIdx + "WHERE".Length);
                if (idx >= 0) whereEnd = Math.Min(whereEnd, idx);
            }

            var whereText = sql[(whereIdx + "WHERE".Length)..whereEnd].Trim();
            var (expr, @params) = SqlWhereParser.ParseWithParameters(whereText);
            where = expr;
            if (@params.Count > 0) parameters = @params;
            afterWhere = whereEnd;
        }

        // Parse optional GROUP BY
        IReadOnlyList<string>? groupByColumns = null;
        var groupByIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "GROUP BY", afterWhere);
        if (groupByIdx2 >= 0)
        {
            var afterGroupBy = groupByIdx2 + "GROUP BY".Length;
            var groupEnd = sql.Length;
            foreach (var kw in new[] { "HAVING", "WINDOW", "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
            {
                var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, afterGroupBy);
                if (idx >= 0) groupEnd = Math.Min(groupEnd, idx);
            }
            var groupText = sql[afterGroupBy..groupEnd].Trim();
            groupByColumns = groupText.Split(',').Select(c => SqlSyntaxText.NormalizeIdentifier(c.Trim())).ToList();
            afterWhere = groupEnd;
        }

        // Parse optional HAVING
        SqlWhereExpression? having = null;
        var havingIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "HAVING", afterWhere);
        if (havingIdx2 >= 0)
        {
            var havingEnd = sql.Length;
            foreach (var kw in new[] { "WINDOW", "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
            {
                var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, havingIdx2 + "HAVING".Length);
                if (idx >= 0) havingEnd = Math.Min(havingEnd, idx);
            }
            var havingText = sql[(havingIdx2 + "HAVING".Length)..havingEnd].Trim();
            havingText = NormalizeAggregatesForHaving(havingText);
            var (hExpr, _) = SqlWhereParser.ParseWithParameters(havingText);
            having = hExpr;
            afterWhere = havingIdx2 + "HAVING".Length + havingText.Length;
        }

        // Parse optional ORDER BY
        IReadOnlyList<SqlOrderByColumn>? orderBy = null;
        var orderByIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ORDER BY", afterWhere);
        if (orderByIdx >= 0)
        {
            var afterOrderBy = orderByIdx + "ORDER BY".Length;
            orderBy = ParseOrderByClause(sql, afterOrderBy);
            afterWhere = orderByIdx + "ORDER BY".Length;
            // Advance past all order-by columns
            foreach (var col in orderBy)
                afterWhere = FindOrderByEnd(sql, afterWhere, col);
        }

        // Parse optional paging
        int? limit = null, offset = null;
        ParsePaging(sql, afterWhere, out limit, out offset);

        return new SqlSelectStatement(tableName, alias, columns, where, parameters, joins, groupByColumns, having,
            orderBy, limit, offset, isDistinct, derivedTable);
    }

    private static IReadOnlyList<SqlOrderByColumn> ParseOrderByClause(string sql, int start)
    {
        // Find end: LIMIT, OFFSET, FETCH, or end of string
        var end = sql.Length;
        foreach (var kw in new[] { "LIMIT", "OFFSET", "FETCH" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, start);
            if (idx >= 0) end = Math.Min(end, idx);
        }

        var text = sql[start..end].Trim();
        var parts = SqlSyntaxText.SplitTopLevel(text, ',');
        var columns = new List<SqlOrderByColumn>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var descending = false;
            if (trimmed.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase))
            {
                descending = true;
                trimmed = trimmed[..^5].Trim();
            }
            else if (trimmed.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^4].Trim();
            }
            columns.Add(new SqlOrderByColumn(SqlSyntaxText.NormalizeColumnIdentifier(trimmed), descending));
        }
        return columns;
    }

    private static int FindOrderByEnd(string sql, int afterKeyword, SqlOrderByColumn col)
    {
        // Find where this order-by column specification ends (including ASC/DESC suffix)
        var colName = col.ColumnName;
        var idx = sql.IndexOf(colName, afterKeyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return afterKeyword;
        var end = idx + colName.Length;
        // Skip ASC/DESC
        var remainder = sql[end..].TrimStart();
        if (remainder.StartsWith("ASC", StringComparison.OrdinalIgnoreCase))
            end += (end - afterKeyword) + 3; // rough
        else if (remainder.StartsWith("DESC", StringComparison.OrdinalIgnoreCase))
            end += 4;
        // Skip comma
        while (end < sql.Length && (char.IsWhiteSpace(sql[end]) || sql[end] == ',')) end++;
        return end;
    }

    private static void ParsePaging(string sql, int start, out int? limit, out int? offset)
    {
        limit = null;
        offset = null;

        // Canonical: OFFSET n ROWS FETCH NEXT m ROWS ONLY
        var offsetIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "OFFSET", start);
        if (offsetIdx >= 0)
        {
            var afterOffset = offsetIdx + "OFFSET".Length;
            var rowsIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ROWS", afterOffset);
            if (rowsIdx >= 0)
            {
                var offsetText = sql[afterOffset..rowsIdx].Trim();
                if (int.TryParse(offsetText, out var off)) offset = off;

                var fetchIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "FETCH", rowsIdx + "ROWS".Length);
                if (fetchIdx >= 0)
                {
                    var afterFetch = fetchIdx + "FETCH".Length;
                    var nextIdx = sql.IndexOf("NEXT", afterFetch, StringComparison.OrdinalIgnoreCase);
                    if (nextIdx < 0) nextIdx = sql.IndexOf("FIRST", afterFetch, StringComparison.OrdinalIgnoreCase);
                    if (nextIdx >= 0)
                    {
                        afterFetch = nextIdx + (sql.AsSpan(nextIdx).StartsWith("NEXT", StringComparison.OrdinalIgnoreCase) ? "NEXT".Length : "FIRST".Length);
                        var rowsIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "ROWS", afterFetch);
                        if (rowsIdx2 >= 0)
                        {
                            var limitText = sql[afterFetch..rowsIdx2].Trim();
                            if (int.TryParse(limitText, out var lim)) limit = lim;
                        }
                    }
                }
                return;
            }
        }

        // FETCH FIRST n ROWS ONLY (no OFFSET)
        var fetchIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "FETCH", start);
        if (fetchIdx2 >= 0)
        {
            var afterFetch = fetchIdx2 + "FETCH".Length;
            var firstIdx = sql.IndexOf("FIRST", afterFetch, StringComparison.OrdinalIgnoreCase);
            if (firstIdx >= 0)
            {
                afterFetch = firstIdx + "FIRST".Length;
                var rowsIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "ROWS", afterFetch);
                if (rowsIdx2 >= 0)
                {
                    var limitText = sql[afterFetch..rowsIdx2].Trim();
                    if (int.TryParse(limitText, out var lim)) limit = lim;
                }
            }
            return;
        }

        // Compatibility: LIMIT n OFFSET m or LIMIT n
        var limitIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "LIMIT", start);
        if (limitIdx >= 0)
        {
            var afterLimit = limitIdx + "LIMIT".Length;
            var offIdx2 = SqlSyntaxText.FindTopLevelKeyword(sql, "OFFSET", afterLimit);
            if (offIdx2 >= 0)
            {
                var limitText = sql[afterLimit..offIdx2].Trim();
                if (int.TryParse(limitText, out var lim)) limit = lim;
                var offsetText = sql[(offIdx2 + "OFFSET".Length)..].Trim();
                if (int.TryParse(offsetText, out var off)) offset = off;
            }
            else
            {
                var limitText = sql[afterLimit..].Trim();
                if (int.TryParse(limitText, out var lim)) limit = lim;
            }
        }
    }

    private static readonly string[] _aggregateNames = { "COUNT", "SUM", "AVG", "MIN", "MAX" };

    private static string NormalizeAggregatesForHaving(string havingText)
    {
        foreach (var agg in _aggregateNames)
        {
            int i = 0;
            while (i < havingText.Length)
            {
                var idx = havingText.IndexOf(agg, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                // Must be followed by '('
                var parenIdx = idx + agg.Length;
                if (parenIdx >= havingText.Length || havingText[parenIdx] != '(')
                {
                    i = idx + agg.Length;
                    continue;
                }

                // Find matching ')'
                var depth = 1;
                var end = parenIdx + 1;
                while (end < havingText.Length && depth > 0)
                {
                    if (havingText[end] == '(') depth++;
                    else if (havingText[end] == ')') depth--;
                    end++;
                }
                if (depth != 0) break;

                // Extract aggregate text: "SUM(Amount)" → wrap in brackets
                var aggregateText = havingText[idx..end];
                havingText = havingText[..idx] + "[" + aggregateText + "]" + havingText[end..];
                i = idx + aggregateText.Length + 2; // +2 for brackets
            }
        }
        return havingText;
    }

    /// <summary>
    /// Parses an optional WINDOW clause (e.g. <c>WINDOW w AS (PARTITION BY a ORDER BY b)</c>)
    /// into a map of window name to its OVER-body text. Returns null when no clause is present.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ParseWindowClause(string sql, int searchFrom)
    {
        var windowIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "WINDOW", searchFrom);
        if (windowIdx < 0) return null;

        var afterWindow = windowIdx + "WINDOW".Length;
        var end = sql.Length;
        foreach (var kw in new[] { "ORDER BY", "LIMIT", "OFFSET", "FETCH" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, kw, afterWindow);
            if (idx >= 0) end = Math.Min(end, idx);
        }

        var text = sql[afterWindow..end].Trim();
        var defs = SqlSyntaxText.SplitTopLevel(text, ',');
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs)
        {
            var d = def.Trim();
            var asIdx = SqlSyntaxText.FindTopLevelKeyword(d, "AS", 0);
            if (asIdx < 0) continue;
            var name = d[..asIdx].Trim();
            var rest = d[(asIdx + "AS".Length)..].TrimStart();
            if (!rest.StartsWith("(")) continue;
            var close = SqlSyntaxText.FindMatchingParen(rest, 0);
            if (close < 0) continue;
            dict[name] = rest[1..close].Trim();
        }
        return dict.Count > 0 ? dict : null;
    }

    private static IReadOnlyList<SqlSelectColumn> ParseSelectColumns(string projectionText,
        IReadOnlyDictionary<string, string>? namedWindows = null)
    {
        if (string.IsNullOrWhiteSpace(projectionText))
            return Array.Empty<SqlSelectColumn>();        if (projectionText == "*")
            return new SqlSelectColumn[] { new("*", null) };

        var parts = SqlSyntaxText.SplitTopLevel(projectionText, ',');
        var columns = new List<SqlSelectColumn>(parts.Length);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Equals("*", StringComparison.Ordinal))
            {
                columns.Add(new SqlSelectColumn("*", null));
                continue;
            }

            // Check for "expression AS alias" or "expression alias"
            SqlAggregateCall? aggregate = null;
            string? alias = null;
            var asIdx = SqlSyntaxText.FindTopLevelKeyword(trimmed, "AS", 0);
            string exprText;
            if (asIdx >= 0)
            {
                exprText = trimmed[..asIdx].Trim();
                alias = SqlSyntaxText.NormalizeIdentifier(trimmed[(asIdx + 2)..].Trim());
            }
            else
            {
                // Might be "expression alias" (space-separated)
                var lastSpace = trimmed.LastIndexOf(' ');
                if (lastSpace > 0 && !trimmed.Contains('('))
                {
                    exprText = trimmed[..lastSpace].Trim();
                    alias = SqlSyntaxText.NormalizeIdentifier(trimmed[(lastSpace + 1)..].Trim());
                }
                else
                {
                    exprText = trimmed;
                }
            }

            // Detect aggregate or window function calls. A window call (anything followed by OVER)
            // takes precedence so that aggregate-over-window — e.g. SUM(x) OVER (...) — is not
            // mistaken for a plain GROUP BY aggregate.
            var windowFunc = TryParseWindowCall(exprText, namedWindows);
            aggregate = windowFunc == null ? TryParseAggregateCall(exprText) : null;

            columns.Add(new SqlSelectColumn(exprText, alias, aggregate, windowFunc));
        }

        return columns;
    }

    private static SqlAggregateCall? TryParseAggregateCall(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return null;

        var parenOpen = expr.IndexOf('(');
        if (parenOpen < 0) return null;

        var funcName = expr[..parenOpen].Trim().ToUpperInvariant();
        if (!funcName.StartsWith("COUNT") && !funcName.StartsWith("SUM")
            && !funcName.StartsWith("AVG") && !funcName.StartsWith("MIN")
            && !funcName.StartsWith("MAX"))
            return null;

        // Find matching closing paren.
        var parenClose = -1;
        var depth = 0;
        for (int i = parenOpen; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') { depth--; if (depth == 0) { parenClose = i; break; } }
        }
        if (parenClose < 0) return null;

        var arg = expr[(parenOpen + 1)..parenClose].Trim();

        SqlAggregateFunction func;
        if (funcName == "COUNT") func = SqlAggregateFunction.Count;
        else if (funcName == "SUM") func = SqlAggregateFunction.Sum;
        else if (funcName == "AVG") func = SqlAggregateFunction.Avg;
        else if (funcName == "MIN") func = SqlAggregateFunction.Min;
        else if (funcName == "MAX") func = SqlAggregateFunction.Max;
        else return null;

        if (arg == "*" && func == SqlAggregateFunction.Count)
            return new SqlAggregateCall(func, null);
        if (string.IsNullOrEmpty(arg)) return null;

        return new SqlAggregateCall(func, SqlSyntaxText.NormalizeIdentifier(arg));
    }

    private static SqlWindowCall? TryParseWindowCall(string expr, IReadOnlyDictionary<string, string>? namedWindows)
    {
        if (string.IsNullOrEmpty(expr)) return null;

        var parenOpen = expr.IndexOf('(');
        if (parenOpen < 0) return null;

        var funcName = expr[..parenOpen].Trim().ToUpperInvariant();
        SqlWindowFunctionType funcType;
        SqlAggregateFunction? aggFunc = null;
        if (funcName == "ROW_NUMBER") funcType = SqlWindowFunctionType.RowNumber;
        else if (funcName == "RANK") funcType = SqlWindowFunctionType.Rank;
        else if (funcName == "DENSE_RANK") funcType = SqlWindowFunctionType.DenseRank;
        else if (funcName == "NTILE") funcType = SqlWindowFunctionType.NTile;
        else if (funcName == "PERCENT_RANK") funcType = SqlWindowFunctionType.PercentRank;
        else if (funcName == "CUME_DIST") funcType = SqlWindowFunctionType.CumeDist;
        else if (funcName == "COUNT") { funcType = SqlWindowFunctionType.Aggregate; aggFunc = SqlAggregateFunction.Count; }
        else if (funcName == "SUM") { funcType = SqlWindowFunctionType.Aggregate; aggFunc = SqlAggregateFunction.Sum; }
        else if (funcName == "AVG") { funcType = SqlWindowFunctionType.Aggregate; aggFunc = SqlAggregateFunction.Avg; }
        else if (funcName == "MIN") { funcType = SqlWindowFunctionType.Aggregate; aggFunc = SqlAggregateFunction.Min; }
        else if (funcName == "MAX") { funcType = SqlWindowFunctionType.Aggregate; aggFunc = SqlAggregateFunction.Max; }
        else if (funcName == "LAG") funcType = SqlWindowFunctionType.Lag;
        else if (funcName == "LEAD") funcType = SqlWindowFunctionType.Lead;
        else if (funcName == "FIRST_VALUE") funcType = SqlWindowFunctionType.FirstValue;
        else if (funcName == "LAST_VALUE") funcType = SqlWindowFunctionType.LastValue;
        else if (funcName == "NTH_VALUE") funcType = SqlWindowFunctionType.NthValue;
        else return null;

        // Find function closing paren
        var depth = 0;
        var funcClose = -1;
        for (int i = parenOpen; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') { depth--; if (depth == 0) { funcClose = i; break; } }
        }
        if (funcClose < 0) return null;

        // NTILE(n) carries a bucket-count argument inside the function parentheses.
        int? ntileBuckets = null;
        if (funcType == SqlWindowFunctionType.NTile)
        {
            var argText = expr[(parenOpen + 1)..funcClose].Trim();
            if (!int.TryParse(argText, out var buckets) || buckets <= 0)
                throw new NotSupportedException("NTILE requires a positive integer bucket count.");
            ntileBuckets = buckets;
        }

        // Aggregate windows carry the aggregated column (or "*" for COUNT) inside the parentheses.
        string? aggArg = null;
        if (funcType == SqlWindowFunctionType.Aggregate)
        {
            var argText = expr[(parenOpen + 1)..funcClose].Trim();
            if (argText == "*")
            {
                if (aggFunc != SqlAggregateFunction.Count)
                    throw new NotSupportedException("'*' is only valid as an argument to COUNT.");
                aggArg = null; // COUNT(*)
            }
            else if (argText.Length == 0)
            {
                return null;
            }
            else
            {
                aggArg = SqlSyntaxText.NormalizeIdentifier(argText);
            }
        }

        // Offset/value functions carry their value expression and optional offset/default arguments.
        string? offsetColumn = null;
        int? offsetAmount = null;
        string? offsetDefault = null;
        if (funcType is SqlWindowFunctionType.Lag or SqlWindowFunctionType.Lead
            or SqlWindowFunctionType.FirstValue or SqlWindowFunctionType.LastValue
            or SqlWindowFunctionType.NthValue)
        {
            var argText = expr[(parenOpen + 1)..funcClose].Trim();
            if (argText.Length == 0)
                throw new NotSupportedException($"{funcName} requires a value expression argument.");

            var args = SqlSyntaxText.SplitTopLevel(argText, ',')
                .Select(a => a.Trim())
                .ToList();

            offsetColumn = SqlSyntaxText.NormalizeIdentifier(args[0]);

            switch (funcType)
            {
                case SqlWindowFunctionType.Lag:
                case SqlWindowFunctionType.Lead:
                    if (args.Count >= 2)
                    {
                        if (!int.TryParse(args[1], out var amount) || amount < 0)
                            throw new NotSupportedException($"{funcName} offset must be a non-negative integer.");
                        offsetAmount = amount;
                    }
                    else
                    {
                        offsetAmount = 1;
                    }
                    if (args.Count >= 3)
                        offsetDefault = args[2];
                    if (args.Count > 3)
                        throw new NotSupportedException($"{funcName} accepts at most three arguments.");
                    break;
                case SqlWindowFunctionType.NthValue:
                    if (args.Count != 2)
                        throw new NotSupportedException("NTH_VALUE requires exactly two arguments (expr, n).");
                    if (!int.TryParse(args[1], out var nth) || nth <= 0)
                        throw new NotSupportedException("NTH_VALUE position must be a positive integer.");
                    offsetAmount = nth;
                    break;
                default: // FIRST_VALUE / LAST_VALUE
                    if (args.Count != 1)
                        throw new NotSupportedException($"{funcName} accepts exactly one argument.");
                    break;
            }
        }

        // Optional NULL treatment (IGNORE NULLS / RESPECT NULLS) between the function and OVER.
        var ignoreNulls = false;
        var afterClose = expr[(funcClose + 1)..].TrimStart();
        if (afterClose.StartsWith("IGNORE", StringComparison.OrdinalIgnoreCase) &&
            afterClose[6..].TrimStart().StartsWith("NULLS", StringComparison.OrdinalIgnoreCase))
        {
            ignoreNulls = true;
            var nullsIdx = afterClose.IndexOf("NULLS", StringComparison.OrdinalIgnoreCase);
            afterClose = afterClose[(nullsIdx + "NULLS".Length)..].TrimStart();
        }
        else if (afterClose.StartsWith("RESPECT", StringComparison.OrdinalIgnoreCase) &&
                 afterClose[7..].TrimStart().StartsWith("NULLS", StringComparison.OrdinalIgnoreCase))
        {
            var nullsIdx = afterClose.IndexOf("NULLS", StringComparison.OrdinalIgnoreCase);
            afterClose = afterClose[(nullsIdx + "NULLS".Length)..].TrimStart();
        }

        // Look for OVER after function close (and optional NULL treatment)
        var afterFunc = afterClose;
        if (!afterFunc.StartsWith("OVER", StringComparison.OrdinalIgnoreCase))
            return null;

        // Distinguish inline "OVER (...)" from a named window reference "OVER w".
        var afterOver = afterFunc[4..].TrimStart();
        string overBody;
        if (afterOver.StartsWith("("))
        {
            var overClose = SqlSyntaxText.FindMatchingParen(afterOver, 0);
            if (overClose < 0) return null;
            overBody = afterOver[1..overClose].Trim();
        }
        else
        {
            // Named window: OVER w  → resolve from WINDOW clause definitions.
            var nameEnd = 0;
            while (nameEnd < afterOver.Length && (char.IsLetterOrDigit(afterOver[nameEnd]) || afterOver[nameEnd] == '_'))
                nameEnd++;
            var windowName = afterOver[..nameEnd].Trim();
            if (windowName.Length == 0)
                return null;
            if (namedWindows == null || !namedWindows.TryGetValue(windowName, out var named))
                throw new NotSupportedException($"Window '{windowName}' is not defined in a WINDOW clause.");
            overBody = named.Trim();
        }

        // Parse PARTITION BY
        IReadOnlyList<string>? partitionBy = null;
        var partIdx = SqlSyntaxText.FindTopLevelKeyword(overBody, "PARTITION BY", 0);
        var orderByIdx = SqlSyntaxText.FindTopLevelKeyword(overBody, "ORDER BY", 0);

        // Locate optional frame clause (ROWS | RANGE | GROUPS) at top level.
        var frameIdx = FindFrameKeyword(overBody);

        if (partIdx >= 0)
        {
            var partStart = partIdx + "PARTITION BY".Length;
            var partEnd = orderByIdx >= 0 ? orderByIdx
                : frameIdx >= 0 ? frameIdx
                : overBody.Length;
            var partText = overBody[partStart..partEnd].Trim();
            partitionBy = partText.Split(',')
                .Select(p => SqlSyntaxText.NormalizeIdentifier(p.Trim()))
                .ToList();
        }

        // Parse ORDER BY
        IReadOnlyList<SqlOrderByColumn>? orderBy = null;
        if (orderByIdx >= 0)
        {
            var orderStart = orderByIdx + "ORDER BY".Length;
            var orderEnd = frameIdx >= 0 ? frameIdx : overBody.Length;
            orderBy = ParseOrderByColumns(overBody[orderStart..orderEnd]);
        }

        // Parse optional frame specification.
        SqlWindowFrame? frame = null;
        if (frameIdx >= 0)
            frame = ParseWindowFrame(overBody[frameIdx..].Trim());

        return new SqlWindowCall(funcType, partitionBy, orderBy, frame, ntileBuckets, aggFunc, aggArg,
            offsetColumn, offsetAmount, offsetDefault, ignoreNulls);
    }

    /// <summary>
    /// Finds the start index of a top-level frame keyword (ROWS, RANGE, GROUPS) in an OVER body,
    /// or -1 if none is present.
    /// </summary>
    private static int FindFrameKeyword(string overBody)
    {
        foreach (var kw in new[] { "ROWS", "RANGE", "GROUPS" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(overBody, kw, 0);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    /// <summary>
    /// Parses a frame specification such as
    /// <c>ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW</c> or the abbreviated single-bound
    /// form <c>ROWS 5 PRECEDING</c> (equivalent to BETWEEN that bound AND CURRENT ROW).
    /// </summary>
    private static SqlWindowFrame ParseWindowFrame(string text)
    {
        var trimmed = text.Trim();
        SqlWindowFrameMode mode;
        if (trimmed.StartsWith("ROWS", StringComparison.OrdinalIgnoreCase))
        { mode = SqlWindowFrameMode.Rows; trimmed = trimmed[4..].TrimStart(); }
        else if (trimmed.StartsWith("RANGE", StringComparison.OrdinalIgnoreCase))
        { mode = SqlWindowFrameMode.Range; trimmed = trimmed[5..].TrimStart(); }
        else if (trimmed.StartsWith("GROUPS", StringComparison.OrdinalIgnoreCase))
        { mode = SqlWindowFrameMode.Groups; trimmed = trimmed[6..].TrimStart(); }
        else
            throw new NotSupportedException($"Unsupported window frame: '{text}'.");

        if (trimmed.StartsWith("BETWEEN", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].TrimStart();
            var andIdx = SqlSyntaxText.FindTopLevelKeyword(trimmed, "AND", 0);
            if (andIdx < 0)
                throw new NotSupportedException($"Window frame BETWEEN requires AND: '{text}'.");
            var startText = trimmed[..andIdx].Trim();
            var endText = trimmed[(andIdx + "AND".Length)..].Trim();
            return new SqlWindowFrame(mode, ParseFrameBound(startText), ParseFrameBound(endText));
        }

        // Single-bound form: <bound> means BETWEEN <bound> AND CURRENT ROW.
        var start = ParseFrameBound(trimmed);
        return new SqlWindowFrame(mode, start,
            new SqlWindowFrameBound(SqlWindowFrameBoundType.CurrentRow));
    }

    private static SqlWindowFrameBound ParseFrameBound(string text)
    {
        var t = text.Trim();
        if (t.Equals("UNBOUNDED PRECEDING", StringComparison.OrdinalIgnoreCase))
            return new SqlWindowFrameBound(SqlWindowFrameBoundType.UnboundedPreceding);
        if (t.Equals("UNBOUNDED FOLLOWING", StringComparison.OrdinalIgnoreCase))
            return new SqlWindowFrameBound(SqlWindowFrameBoundType.UnboundedFollowing);
        if (t.Equals("CURRENT ROW", StringComparison.OrdinalIgnoreCase))
            return new SqlWindowFrameBound(SqlWindowFrameBoundType.CurrentRow);

        if (t.EndsWith("PRECEDING", StringComparison.OrdinalIgnoreCase))
        {
            var num = t[..^"PRECEDING".Length].Trim();
            if (!int.TryParse(num, out var offset) || offset < 0)
                throw new NotSupportedException($"Invalid frame offset: '{text}'.");
            return new SqlWindowFrameBound(SqlWindowFrameBoundType.Preceding, offset);
        }
        if (t.EndsWith("FOLLOWING", StringComparison.OrdinalIgnoreCase))
        {
            var num = t[..^"FOLLOWING".Length].Trim();
            if (!int.TryParse(num, out var offset) || offset < 0)
                throw new NotSupportedException($"Invalid frame offset: '{text}'.");
            return new SqlWindowFrameBound(SqlWindowFrameBoundType.Following, offset);
        }

        throw new NotSupportedException($"Unsupported frame bound: '{text}'.");
    }

    private static IReadOnlyList<SqlOrderByColumn> ParseOrderByColumns(string text)
    {
        var parts = SqlSyntaxText.SplitTopLevel(text.Trim(), ',');
        var columns = new List<SqlOrderByColumn>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var descending = false;
            string? collation = null;

            // Detect COLLATE before ASC/DESC
            var collateIdx = trimmed.IndexOf("COLLATE", StringComparison.OrdinalIgnoreCase);
            if (collateIdx >= 0)
            {
                var afterCollate = trimmed[(collateIdx + "COLLATE".Length)..].TrimStart();
                if (afterCollate.Length > 0 && afterCollate[0] == '"')
                {
                    var endQuote = afterCollate.IndexOf('"', 1);
                    if (endQuote >= 0)
                        collation = afterCollate[1..endQuote];
                }
                else
                {
                    var spaceIdx = afterCollate.IndexOfAny(new[] { ' ', '\t' });
                    collation = spaceIdx >= 0 ? afterCollate[..spaceIdx] : afterCollate;
                }
                trimmed = trimmed[..collateIdx].TrimEnd();
            }

            if (trimmed.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase))
            { descending = true; trimmed = trimmed[..^5].Trim(); }
            else if (trimmed.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase))
            { trimmed = trimmed[..^4].Trim(); }
            columns.Add(new SqlOrderByColumn(SqlSyntaxText.NormalizeColumnIdentifier(trimmed), descending, collation));
        }
        return columns;
    }

    private static SqlStatement ParseInsert(string sql)
    {
        // Support: INSERT INTO table (cols) VALUES (v1, v2), (v3, v4), ...
        var intoIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "INTO", "INSERT".Length);
        if (intoIdx < 0)
            throw new NotSupportedException("INSERT requires INTO keyword.");

        var afterInto = intoIdx + "INTO".Length;
        var parenOpen = -1;
        for (var i = afterInto; i < sql.Length; i++)
        {
            if (sql[i] == '(') { parenOpen = i; break; }
            if (char.IsLetter(sql[i])) continue; // still scanning table name
        }

        if (parenOpen < 0)
            throw new NotSupportedException("INSERT requires column list in parentheses.");

        var tableSegment = sql[afterInto..parenOpen].Trim();
        var tableName = SqlSyntaxText.NormalizeIdentifier(tableSegment);

        // Find the columns closing paren
        var colClose = SqlSyntaxText.FindMatchingParen(sql, parenOpen);
        if (colClose < 0)
            throw new NotSupportedException("Unclosed column list in INSERT.");

        var colsText = sql[(parenOpen + 1)..colClose].Trim();
        var columns = SqlSyntaxText.SplitTopLevel(colsText, ',')
            .Select(SqlSyntaxText.NormalizeIdentifier)
            .ToList();

        if (columns.Count == 0)
            throw new NotSupportedException("INSERT requires at least one column.");

        var afterCols = sql[(colClose + 1)..].TrimStart();

        // INSERT ... SELECT [ON CONFLICT ...]
        if (afterCols.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var (selectStmt, selConflict) = ParseSelectWithOptionalOnConflict(afterCols);
            return new SqlInsertSelectStatement(tableName, columns, selectStmt, selConflict);
        }

        // INSERT ... VALUES [ON CONFLICT ...]
        var valuesIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "VALUES", colClose + 1);
        if (valuesIdx < 0)
            throw new NotSupportedException("INSERT requires VALUES or SELECT clause.");

        // Parse value rows: (v1, v2), (v3, v4), ...; stop at ON CONFLICT
        var remaining = sql[(valuesIdx + "VALUES".Length)..].Trim();
        var valueRows = new List<IReadOnlyList<string>>();

        var pos = 0;
        while (pos < remaining.Length)
        {
            while (pos < remaining.Length && char.IsWhiteSpace(remaining[pos])) pos++;
            if (pos >= remaining.Length) break;

            // Stop if we hit ON CONFLICT clause
            if (remaining[pos..].StartsWith("ON CONFLICT", StringComparison.OrdinalIgnoreCase))
                break;

            if (remaining[pos] != '(')
                throw new NotSupportedException($"Expected '(' for value row near '{remaining[pos..]}'.");

            var valClose = SqlSyntaxText.FindMatchingParen(remaining, pos);
            if (valClose < 0)
                throw new NotSupportedException("Unclosed value list in INSERT.");

            var valsText = remaining[(pos + 1)..valClose].Trim();
            var values = SqlSyntaxText.SplitTopLevel(valsText, ',')
                .Select(v => v.Trim())
                .ToList();

            if (values.Count != columns.Count)
                throw new NotSupportedException($"INSERT column/value mismatch: {columns.Count} columns but {values.Count} values.");

            valueRows.Add(values);
            pos = valClose + 1;

            // Skip comma separator between value groups
            while (pos < remaining.Length && char.IsWhiteSpace(remaining[pos])) pos++;
            if (pos < remaining.Length && remaining[pos] == ',') pos++;
        }

        if (valueRows.Count == 0)
            throw new NotSupportedException("INSERT requires at least one value row.");

        var onConflict = TryParseOnConflict(remaining, pos);
        return new SqlInsertStatement(tableName, columns, valueRows, onConflict);
    }

    private static (SqlSelectStatement Select, SqlOnConflictClause? OnConflict) ParseSelectWithOptionalOnConflict(string sql)
    {
        // Find ON CONFLICT that belongs to INSERT (after SELECT ends)
        var onConflictIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ON CONFLICT", 0);
        if (onConflictIdx < 0)
            return (ParseSelect(sql), null);

        var selectSql = sql[..onConflictIdx].TrimEnd();
        var conflictSql = sql[onConflictIdx..].Trim();
        return (ParseSelect(selectSql), ParseOnConflict(conflictSql));
    }

    private static SqlOnConflictClause? TryParseOnConflict(string remaining, int pos)
    {
        while (pos < remaining.Length && char.IsWhiteSpace(remaining[pos])) pos++;
        if (pos < remaining.Length && remaining[pos..].StartsWith("ON CONFLICT", StringComparison.OrdinalIgnoreCase))
            return ParseOnConflict(remaining[pos..].Trim());
        return null;
    }

    private static SqlInsertSelectStatement ParseInsertSelect(
        string sql, string tableName, IReadOnlyList<string> columns, int colClose)
    {
        // Extract and parse the SELECT sub-statement
        var selectSql = sql[(colClose + 1)..].TrimStart();
        var selectStmt = ParseSelect(selectSql);

        return new SqlInsertSelectStatement(tableName, columns, selectStmt);
    }

    private static SqlOnConflictClause ParseOnConflict(string sql)
    {
        var remaining = sql["ON CONFLICT".Length..].TrimStart();

        // Parse optional conflict target
        SqlConflictTarget? target = null;
        if (remaining.StartsWith("("))
        {
            var closeParen = SqlSyntaxText.FindMatchingParen(remaining, 0);
            if (closeParen < 0)
                throw new NotSupportedException("Unclosed conflict target column list.");
            var colsText = remaining[1..closeParen];
            var columns = SqlSyntaxText.SplitTopLevel(colsText, ',')
                .Select(SqlSyntaxText.NormalizeIdentifier)
                .ToList();
            target = new SqlConflictTarget(columns, null);
            remaining = remaining[(closeParen + 1)..].TrimStart();
        }
        else if (remaining.StartsWith("ON CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining["ON CONSTRAINT".Length..].TrimStart();
            // Constraint name: identifier until whitespace or end
            var end = remaining.Length;
            var spaceIdx = remaining.IndexOf(' ');
            if (spaceIdx < 0) spaceIdx = remaining.IndexOf('\t');
            var name = spaceIdx >= 0 ? remaining[..spaceIdx] : remaining;
            target = new SqlConflictTarget(null, SqlSyntaxText.NormalizeIdentifier(name));
            remaining = remaining[name.Length..].TrimStart();
        }

        if (remaining.StartsWith("DO NOTHING", StringComparison.OrdinalIgnoreCase))
            return new SqlOnConflictClause(target, SqlConflictAction.DoNothing, null, null);

        if (!remaining.StartsWith("DO UPDATE", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Expected DO NOTHING or DO UPDATE in ON CONFLICT.");

        remaining = remaining["DO UPDATE".Length..].TrimStart();
        if (!remaining.StartsWith("SET", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Expected SET after DO UPDATE in ON CONFLICT.");

        remaining = remaining["SET".Length..].TrimStart();

        // Find optional WHERE clause
        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(remaining, "WHERE", 0);
        SqlWhereExpression? where = null;
        var setClause = remaining;
        if (whereIdx >= 0)
        {
            setClause = remaining[..whereIdx].Trim();
            var whereText = remaining[(whereIdx + "WHERE".Length)..].Trim();
            where = SqlWhereParser.Parse(whereText);
        }

        var assignments = ParseAssignments(setClause.Trim());
        return new SqlOnConflictClause(target, SqlConflictAction.DoUpdate, assignments, where);
    }

    private static SqlCreateTableStatement ParseCreateTable(string sql)
    {
        // CREATE TABLE name (col1 TYPE, col2 TYPE, ...)
        var prefixLen = "CREATE TABLE".Length;
        var openIdx = -1;
        for (var i = prefixLen; i < sql.Length; i++)
        {
            if (sql[i] == '(') { openIdx = i; break; }
        }

        if (openIdx < 0)
            throw new NotSupportedException("CREATE TABLE requires column definitions in parentheses.");

        var closeIdx = SqlSyntaxText.FindMatchingParen(sql, openIdx);
        if (closeIdx < 0)
            throw new NotSupportedException("Unclosed column definitions in CREATE TABLE.");

        var tableSegment = sql[prefixLen..openIdx].Trim();
        var tableName = SqlSyntaxText.NormalizeIdentifier(tableSegment);

        var bodyText = sql[(openIdx + 1)..closeIdx].Trim();
        var definitions = SqlSyntaxText.SplitTopLevel(bodyText, ',');
        var columns = new List<SqlColumnDefinition>();
        var foreignKeys = new List<SqlForeignKeyDefinition>();
        var checks = new List<SqlCheckConstraint>();
        var autoCheckIndex = 0;

        foreach (var definition in definitions)
        {
            var trimmed = definition.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
                continue;

            if (trimmed.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            {
                foreignKeys.Add(ParseForeignKeyDefinition(trimmed));
                continue;
            }

            // Table-level CHECK (...) — "CHECK" must be followed by whitespace or '(' so a column
            // named e.g. "check_flag" is not misinterpreted.
            if (IsTableLevelCheck(trimmed))
            {
                var expr = ExtractCheckExpression(trimmed);
                checks.Add(new SqlCheckConstraint($"{tableName}_check{++autoCheckIndex}", expr));
                continue;
            }

            if (trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                var constraintBody = trimmed["CONSTRAINT".Length..].TrimStart();
                // CONSTRAINT fk_name FOREIGN KEY ... — extract name, parse FK
                var fkIdx = constraintBody.IndexOf("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);
                if (fkIdx >= 0)
                {
                    var fkDef = ParseForeignKeyDefinition(constraintBody[fkIdx..]);
                    // Use constraint name if provided
                    if (fkIdx > 0)
                    {
                        var constraintName = constraintBody[..fkIdx].Trim();
                        fkDef = fkDef with { ConstraintName = constraintName };
                    }
                    foreignKeys.Add(fkDef);
                    continue;
                }

                // CONSTRAINT chk_name CHECK (...)
                var checkIdx = FindCheckKeyword(constraintBody);
                if (checkIdx >= 0)
                {
                    var name = checkIdx > 0
                        ? constraintBody[..checkIdx].Trim()
                        : $"{tableName}_check{++autoCheckIndex}";
                    var expr = ExtractCheckExpression(constraintBody[checkIdx..]);
                    checks.Add(new SqlCheckConstraint(name, expr));
                }
                continue;
            }

            // Column definition — may carry an inline column-level CHECK (...).
            if (TryExtractInlineCheck(trimmed, out var remainder, out var inlineExpr))
            {
                var col = ParseColumnDefinition(remainder);
                columns.Add(col);
                checks.Add(new SqlCheckConstraint($"{tableName}_{col.Name}_check", inlineExpr));
            }
            else
            {
                columns.Add(ParseColumnDefinition(trimmed));
            }
        }

        if (columns.Count == 0)
            throw new NotSupportedException("CREATE TABLE requires at least one column.");

        var tableDef = new SqlTableDefinition(
            tableName, columns, Array.Empty<SqlIndexDefinition>(), foreignKeys,
            null, checks.Count > 0 ? checks : null);
        return new SqlCreateTableStatement(tableDef);
    }

    /// <summary>True if <paramref name="text"/> is a table-level CHECK clause (CHECK followed by whitespace or '(').</summary>
    private static bool IsTableLevelCheck(string text)
    {
        if (!text.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
            return false;
        if (text.Length == 5) return false;
        var next = text[5];
        return char.IsWhiteSpace(next) || next == '(';
    }

    /// <summary>Finds the position of the CHECK keyword (word boundary, followed by whitespace or '(') at top level.</summary>
    private static int FindCheckKeyword(string text)
    {
        var inQuote = false;
        for (var i = 0; i + 5 <= text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) continue;
            if (string.Compare(text, i, "CHECK", 0, 5, StringComparison.OrdinalIgnoreCase) != 0) continue;
            var j = i + 5;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j < text.Length && text[j] == '(')
                return i;
        }
        return -1;
    }

    /// <summary>Extracts the inner predicate text from a "CHECK (expr)" clause.</summary>
    private static string ExtractCheckExpression(string checkClause)
    {
        var afterCheck = checkClause["CHECK".Length..].TrimStart();
        if (afterCheck.Length == 0 || afterCheck[0] != '(')
            throw new NotSupportedException($"CHECK constraint requires a parenthesized expression: '{checkClause}'.");
        var closeIdx = SqlSyntaxText.FindMatchingParen(afterCheck, 0);
        if (closeIdx < 0)
            throw new NotSupportedException($"Unclosed CHECK expression: '{checkClause}'.");
        var expr = afterCheck[1..closeIdx].Trim();
        if (expr.Length == 0)
            throw new NotSupportedException($"Empty CHECK expression: '{checkClause}'.");
        return expr;
    }

    /// <summary>Detects and strips an inline column-level CHECK (...) from a column definition.</summary>
    private static bool TryExtractInlineCheck(string columnDef, out string remainder, out string expression)
    {
        remainder = columnDef;
        expression = string.Empty;
        var checkIdx = FindCheckKeyword(columnDef);
        if (checkIdx < 0) return false;

        var afterCheck = columnDef[checkIdx..];
        expression = ExtractCheckExpression(afterCheck);

        // Locate the matching close paren in the original string to compute the remainder.
        var openRel = columnDef.IndexOf('(', checkIdx);
        var closeIdx = SqlSyntaxText.FindMatchingParen(columnDef, openRel);
        remainder = (columnDef[..checkIdx] + " " + columnDef[(closeIdx + 1)..]).Trim();
        return true;
    }

    private static SqlColumnDefinition ParseColumnDefinition(string raw)
    {
        // "name TYPE [NOT NULL] [PRIMARY KEY] [UNIQUE] [COLLATE name]"
        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new NotSupportedException($"Invalid column definition '{raw}'.");

        var name = SqlSyntaxText.NormalizeIdentifier(parts[0]);
        var type = ParseSqlType(parts[1]);

        var upper = raw.ToUpperInvariant();
        var isPrimary = upper.Contains("PRIMARY KEY");
        var isUnique = upper.Contains("UNIQUE") || isPrimary;
        var isNotNull = upper.Contains("NOT NULL") || isPrimary;

        string? collation = null;
        var collateIdx = raw.IndexOf("COLLATE", StringComparison.OrdinalIgnoreCase);
        if (collateIdx >= 0)
        {
            var afterCollate = raw[(collateIdx + "COLLATE".Length)..].TrimStart();
            if (afterCollate.Length > 0 && afterCollate[0] == '"')
            {
                var endQuote = afterCollate.IndexOf('"', 1);
                if (endQuote >= 0)
                    collation = afterCollate[1..endQuote];
            }
            else
            {
                var spaceIdx = afterCollate.IndexOfAny(new[] { ' ', '\t' });
                collation = spaceIdx >= 0 ? afterCollate[..spaceIdx] : afterCollate;
            }
        }

        return new SqlColumnDefinition(name, type, !isNotNull, isPrimary, isUnique, collation);
    }

    private static SqlForeignKeyDefinition ParseForeignKeyDefinition(string raw)
    {
        // FOREIGN KEY (col1, col2) REFERENCES other_table(col1, col2) [ON DELETE action] [ON UPDATE action]
        var afterFk = raw["FOREIGN KEY".Length..].TrimStart();
        if (afterFk.Length == 0 || afterFk[0] != '(')
            throw new NotSupportedException($"Invalid FOREIGN KEY definition: '{raw}'.");

        var closeIdx = SqlSyntaxText.FindMatchingParen(afterFk, 0);
        var fkColumns = SqlSyntaxText.SplitTopLevel(afterFk[1..closeIdx], ',')
            .Select(c => SqlSyntaxText.NormalizeIdentifier(c.Trim()))
            .ToArray();

        var afterColumns = afterFk[(closeIdx + 1)..].TrimStart();
        if (!afterColumns.StartsWith("REFERENCES", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"FOREIGN KEY must specify REFERENCES: '{raw}'.");

        var afterRef = afterColumns["REFERENCES".Length..].TrimStart();
        var refOpenIdx = afterRef.IndexOf('(');
        if (refOpenIdx < 0)
            throw new NotSupportedException($"REFERENCES requires column list: '{raw}'.");

        var refCloseIdx = SqlSyntaxText.FindMatchingParen(afterRef, refOpenIdx);
        var refTable = SqlSyntaxText.NormalizeIdentifier(afterRef[..refOpenIdx].Trim());
        var refColumns = SqlSyntaxText.SplitTopLevel(afterRef[(refOpenIdx + 1)..refCloseIdx], ',')
            .Select(c => SqlSyntaxText.NormalizeIdentifier(c.Trim()))
            .ToArray();

        var onDelete = SqlForeignKeyAction.Restrict;
        var onUpdate = SqlForeignKeyAction.Restrict;

        var remaining = afterRef[(refCloseIdx + 1)..].Trim();
        while (remaining.Length > 0)
        {
            if (remaining.StartsWith("ON DELETE", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining["ON DELETE".Length..].TrimStart();
                onDelete = ParseFkAction(ref remaining);
                continue;
            }
            if (remaining.StartsWith("ON UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining["ON UPDATE".Length..].TrimStart();
                onUpdate = ParseFkAction(ref remaining);
                continue;
            }
            break;
        }

        return new SqlForeignKeyDefinition(
            string.Empty, fkColumns, refTable, refColumns, onDelete, onUpdate);
    }

    private static SqlForeignKeyAction ParseFkAction(ref string remaining)
    {
        var spaceIdx = remaining.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        var word = spaceIdx >= 0 ? remaining[..spaceIdx] : remaining;
        remaining = spaceIdx >= 0 ? remaining[spaceIdx..].TrimStart() : string.Empty;

        return word.ToUpperInvariant() switch
        {
            "RESTRICT" => SqlForeignKeyAction.Restrict,
            "CASCADE" => SqlForeignKeyAction.Cascade,
            "SET" => ParseSetNull(ref remaining),
            _ => SqlForeignKeyAction.Restrict
        };
    }

    private static SqlForeignKeyAction ParseSetNull(ref string remaining)
    {
        // Consume "NULL" after "SET"
        var spaceIdx = remaining.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        var word = spaceIdx >= 0 ? remaining[..spaceIdx] : remaining;
        remaining = spaceIdx >= 0 ? remaining[spaceIdx..].TrimStart() : string.Empty;
        return word.ToUpperInvariant() == "NULL" ? SqlForeignKeyAction.SetNull : SqlForeignKeyAction.Restrict;
    }

    private static SqlScalarType ParseSqlType(string typeName)
    {
        // Strip size qualifiers: VARCHAR(500) → VARCHAR
        var baseType = typeName;
        var parenIdx = typeName.IndexOf('(');
        if (parenIdx >= 0) baseType = typeName[..parenIdx];

        return baseType.ToUpperInvariant() switch
        {
            "INT" or "INT32" or "INTEGER" => SqlScalarType.Int32,
            "BIGINT" or "INT64" or "LONG" => SqlScalarType.Int64,
            "SMALLINT" or "INT16" => SqlScalarType.Int16,
            "FLOAT" or "REAL" or "DOUBLE" => SqlScalarType.Double,
            "DECIMAL" or "NUMERIC" or "MONEY" => SqlScalarType.Decimal,
            "VARCHAR" or "NVARCHAR" or "TEXT" or "STRING" or "CHAR" or "NCHAR" => SqlScalarType.String,
            "BIT" or "BOOL" or "BOOLEAN" => SqlScalarType.Boolean,
            "DATETIME" or "DATETIME2" or "TIMESTAMP" => SqlScalarType.DateTime,
            "DATE" => SqlScalarType.Date,
            "TIME" => SqlScalarType.Time,
            "UNIQUEIDENTIFIER" or "UUID" or "GUID" => SqlScalarType.Guid,
            "VARBINARY" or "BINARY" or "BLOB" => SqlScalarType.Binary,
            "JSON" => SqlScalarType.Json,
            _ => SqlScalarType.String
        };
    }

    private static SqlDropTableStatement ParseDropTable(string sql)
    {
        // DROP TABLE [IF EXISTS] name
        var afterPrefix = sql["DROP TABLE".Length..].TrimStart();

        if (afterPrefix.StartsWith("IF EXISTS", StringComparison.OrdinalIgnoreCase))
            afterPrefix = afterPrefix["IF EXISTS".Length..].TrimStart();

        var tableName = SqlSyntaxText.NormalizeIdentifier(afterPrefix);
        return new SqlDropTableStatement(tableName);
    }

    private static SqlCreateIndexStatement ParseCreateIndex(string sql)
    {
        // CREATE [UNIQUE] INDEX index_name ON table_name [USING {BTREE|GIN}] (col1, col2, ...)
        var isUnique = false;
        var remaining = sql;
        var indexType = SqlIndexType.BTree;

        if (remaining.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase))
        {
            isUnique = true;
            remaining = remaining["CREATE UNIQUE INDEX".Length..].TrimStart();
        }
        else
        {
            remaining = remaining["CREATE INDEX".Length..].TrimStart();
        }

        // Next token is index name
        var onIdx = SqlSyntaxText.FindTopLevelKeyword(remaining, "ON", 0);
        if (onIdx < 0)
            throw new NotSupportedException("CREATE INDEX requires ON clause.");

        var indexName = SqlSyntaxText.NormalizeIdentifier(remaining[..onIdx].Trim());
        remaining = remaining[(onIdx + "ON".Length)..].TrimStart();

        // Next is table name, optionally followed by USING clause, then (col1, col2, ...)
        var parenOpen = remaining.IndexOf('(');
        if (parenOpen < 0)
            throw new NotSupportedException("CREATE INDEX requires column list in parentheses.");

        // Check for USING clause before the opening paren.
        var tablePart = remaining[..parenOpen].Trim();
        var usingIdx = SqlSyntaxText.FindTopLevelKeyword(tablePart, "USING", 0);
        string tableName;
        if (usingIdx >= 0)
        {
            tableName = SqlSyntaxText.NormalizeIdentifier(tablePart[..usingIdx].Trim());
            var usingRest = tablePart[(usingIdx + "USING".Length)..].Trim();
            indexType = usingRest.Equals("GIN", StringComparison.OrdinalIgnoreCase) ? SqlIndexType.Gin
                : usingRest.Equals("BTREE", StringComparison.OrdinalIgnoreCase) ? SqlIndexType.BTree
                : throw new NotSupportedException($"Unsupported index type: '{usingRest}'. Expected GIN or BTREE.");
        }
        else
        {
            tableName = SqlSyntaxText.NormalizeIdentifier(tablePart);
        }

        remaining = remaining[parenOpen..].Trim();

        // Parse column list
        if (!remaining.StartsWith("("))
            throw new NotSupportedException("Expected '(' for index column list.");

        // Find matching closing paren.
        int depth = 0;
        int parenClose = -1;
        for (int i = 0; i < remaining.Length; i++)
        {
            if (remaining[i] == '(') depth++;
            else if (remaining[i] == ')') { depth--; if (depth == 0) { parenClose = i; break; } }
        }
        if (parenClose < 0)
            throw new NotSupportedException("Unmatched '(' in index column list.");

        var colListText = remaining[1..parenClose].Trim();
        var colParts = SqlSyntaxText.SplitTopLevel(colListText, ',');
        var colNames = new List<string>(colParts.Length);
        foreach (var part in colParts)
        {
            var trimmed = part.Trim();
            // Support JSON path index: col->'$.path' or col->>'$.path'
            var arrowIdx = trimmed.IndexOf("->");
            if (arrowIdx >= 0)
            {
                var colName = SqlSyntaxText.NormalizeIdentifier(trimmed[..arrowIdx].Trim());
                var rest = trimmed[(arrowIdx + 2)..].TrimStart();
                if (rest.StartsWith(">")) rest = rest[1..].TrimStart(); // ->>
                var jsonPath = rest.StartsWith("'") ? rest.Trim('\'') : rest;
                colNames.Add($"{colName}:{jsonPath}");
            }
            else
            {
                colNames.Add(SqlSyntaxText.NormalizeIdentifier(trimmed));
            }
        }

        return new SqlCreateIndexStatement(indexName, tableName, colNames, isUnique, indexType);
    }

    private static SqlDropIndexStatement ParseDropIndex(string sql)
    {
        // DROP INDEX index_name ON table_name
        var remaining = sql["DROP INDEX".Length..].TrimStart();

        var onIdx = SqlSyntaxText.FindTopLevelKeyword(remaining, "ON", 0);
        if (onIdx < 0)
            throw new NotSupportedException("DROP INDEX requires ON clause.");

        var indexName = SqlSyntaxText.NormalizeIdentifier(remaining[..onIdx].Trim());
        var tableName = SqlSyntaxText.NormalizeIdentifier(remaining[(onIdx + "ON".Length)..].Trim());

        return new SqlDropIndexStatement(indexName, tableName);
    }

    private static SqlUpdateStatement ParseUpdate(string sql)
    {
        // UPDATE table SET col = value, ... WHERE predicate
        var setIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "SET", "UPDATE".Length);
        if (setIdx < 0)
            throw new NotSupportedException("UPDATE requires SET clause.");

        var tableSegment = sql["UPDATE".Length..setIdx].Trim();
        var tableName = SqlSyntaxText.NormalizeIdentifier(tableSegment);

        var afterSet = setIdx + "SET".Length;
        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "WHERE", afterSet);

        var assignmentsEnd = whereIdx >= 0 ? whereIdx : sql.Length;
        var assignmentsText = sql[afterSet..assignmentsEnd].Trim();
        var assignments = ParseAssignments(assignmentsText);

        SqlWhereExpression? where = null;
        IReadOnlyList<string>? parameters = null;
        if (whereIdx >= 0)
        {
            var whereText = sql[(whereIdx + "WHERE".Length)..].Trim();
            var (expr, @params) = SqlWhereParser.ParseWithParameters(whereText);
            where = expr;
            if (@params.Count > 0) parameters = @params;
        }

        return new SqlUpdateStatement(tableName, assignments, where, parameters);
    }

    private static IReadOnlyDictionary<string, string> ParseAssignments(string text)
    {
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SqlSyntaxText.SplitTopLevel(text.AsSpan(), ',', part =>
        {
            var eqIdx = FindAssignmentSeparator(part);
            if (eqIdx < 0)
                throw new NotSupportedException($"Invalid assignment '{part.ToString()}'.");

            var col = SqlSyntaxText.NormalizeIdentifier(part.Slice(0, eqIdx).Trim());
            var val = part.Slice(eqIdx + 1).Trim().ToString();
            assignments[col] = val;
        });
        return assignments;
    }

    private static int FindAssignmentSeparator(ReadOnlySpan<char> assignment)
    {
        var inString = false;
        var depth = 0;
        for (var i = 0; i < assignment.Length; i++)
        {
            var c = assignment[i];
            if (c == '\'' && (i == 0 || assignment[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c == '=') return i;
        }
        return -1;
    }

    private static SqlDeleteStatement ParseDelete(string sql)
    {
        // DELETE FROM table WHERE predicate
        var fromIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "FROM", "DELETE".Length);
        if (fromIdx < 0)
            throw new NotSupportedException("DELETE requires FROM clause.");

        var afterFrom = fromIdx + "FROM".Length;
        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "WHERE", afterFrom);

        var tableSegmentEnd = whereIdx >= 0 ? whereIdx : sql.Length;
        var tableName = SqlSyntaxText.NormalizeIdentifier(sql[afterFrom..tableSegmentEnd].Trim());

        SqlWhereExpression? where = null;
        IReadOnlyList<string>? parameters = null;
        if (whereIdx >= 0)
        {
            var whereText = sql[(whereIdx + "WHERE".Length)..].Trim();
            var (expr, @params) = SqlWhereParser.ParseWithParameters(whereText);
            where = expr;
            if (@params.Count > 0) parameters = @params;
        }

        return new SqlDeleteStatement(tableName, where, parameters);
    }

    // ── JOIN parsing ──────────────────────────────────────────────────────────

    private static int FindJoinKeyword(string sql, int startIndex, int endIndex)
    {
        var searchRange = sql[startIndex..endIndex];
        var idx = -1;
        foreach (var keyword in new[] { "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "CROSS JOIN", "JOIN" })
        {
            var pos = SqlSyntaxText.FindTopLevelKeyword(searchRange, keyword, 0);
            if (pos >= 0 && (idx < 0 || pos < idx))
                idx = pos;
        }
        return idx >= 0 ? startIndex + idx : -1;
    }

    private static IReadOnlyList<SqlJoinClause>? ParseJoinClauses(string sql, int startIndex, int endBeforeWhere)
    {
        if (startIndex >= endBeforeWhere) return null;

        var joins = new List<SqlJoinClause>();
        var pos = startIndex;

        while (pos < endBeforeWhere)
        {
            // Skip whitespace
            while (pos < endBeforeWhere && char.IsWhiteSpace(sql[pos])) pos++;
            if (pos >= endBeforeWhere) break;

            // Determine join kind
            var kind = SqlJoinKind.Inner;
            var remaining = sql[pos..endBeforeWhere];
            if (remaining.StartsWith("INNER JOIN", StringComparison.OrdinalIgnoreCase))
            { kind = SqlJoinKind.Inner; pos += "INNER JOIN".Length; }
            else if (remaining.StartsWith("LEFT JOIN", StringComparison.OrdinalIgnoreCase))
            { kind = SqlJoinKind.Left; pos += "LEFT JOIN".Length; }
            else if (remaining.StartsWith("RIGHT JOIN", StringComparison.OrdinalIgnoreCase))
            { kind = SqlJoinKind.Right; pos += "RIGHT JOIN".Length; }
            else if (remaining.StartsWith("CROSS JOIN", StringComparison.OrdinalIgnoreCase))
            { kind = SqlJoinKind.Cross; pos += "CROSS JOIN".Length; }
            else if (remaining.StartsWith("JOIN", StringComparison.OrdinalIgnoreCase))
            { kind = SqlJoinKind.Inner; pos += "JOIN".Length; }
            else
                break; // No more JOIN clauses

            // Parse table name and alias
            while (pos < endBeforeWhere && char.IsWhiteSpace(sql[pos])) pos++;
            var onIdx = SqlSyntaxText.FindTopLevelKeyword(sql, " ON ", pos);
            if (onIdx < 0)
                onIdx = FindOnKeyword(sql, pos, endBeforeWhere);
            var usingIdx = FindUsingKeyword(sql, pos, endBeforeWhere);

            // Determine end of table segment (the earlier of ON, USING, or next JOIN)
            var tableEnd = endBeforeWhere;
            if (onIdx >= 0) tableEnd = Math.Min(tableEnd, onIdx);
            if (usingIdx >= 0) tableEnd = Math.Min(tableEnd, usingIdx);
            // For CROSS JOIN without ON, also stop at next JOIN keyword
            if (kind == SqlJoinKind.Cross)
            {
                var nextJoinIdx = FindJoinKeyword(sql, pos, endBeforeWhere);
                if (nextJoinIdx >= 0) tableEnd = Math.Min(tableEnd, nextJoinIdx);
            }

            var tableSeg = sql[pos..tableEnd].Trim();

            SqlSelectStatement? joinDerivedTable = null;
            string tableName;
            string? alias = null;

            if (tableSeg.StartsWith("("))
            {
                var subEnd = SqlSyntaxText.FindMatchingParen(tableSeg, 0);
                if (subEnd < 0)
                    throw new NotSupportedException("Unclosed derived table parentheses in JOIN.");

                var subquerySql = tableSeg[1..subEnd];
                joinDerivedTable = ParseSelect(subquerySql);

                var afterParen = tableSeg[(subEnd + 1)..].TrimStart();
                if (afterParen.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                    afterParen = afterParen[2..].TrimStart();
                var aliasEnd = afterParen.IndexOf(' ');
                alias = aliasEnd > 0 ? afterParen[..aliasEnd] : afterParen;
                if (string.IsNullOrWhiteSpace(alias))
                    throw new NotSupportedException("Derived table in JOIN requires an alias.");

                tableName = alias;
            }
            else
            {
                if (!SqlSyntaxText.TryParseSingleTableSource(tableSeg, out tableName, out alias))
                    throw new NotSupportedException($"Cannot parse table name from '{tableSeg}'.");
            }

            SqlWhereExpression? onPredicate;
            int predicateEnd;

            if (usingIdx >= 0)
            {
                // USING (col1, col2, ...) — expand to ON left.col1 = right.col1 AND ...
                var afterUsing = usingIdx + "USING".Length;
                while (afterUsing < endBeforeWhere && char.IsWhiteSpace(sql[afterUsing])) afterUsing++;
                if (afterUsing >= endBeforeWhere || sql[afterUsing] != '(')
                    throw new NotSupportedException("USING requires a parenthesized column list.");

                var closeParen = afterUsing + 1;
                var parenDepth = 1;
                while (closeParen < endBeforeWhere && parenDepth > 0)
                {
                    if (sql[closeParen] == '(') parenDepth++;
                    else if (sql[closeParen] == ')') parenDepth--;
                    closeParen++;
                }
                if (parenDepth != 0)
                    throw new NotSupportedException("Unterminated USING clause.");

                var usingColsText = sql[(afterUsing + 1)..(closeParen - 1)];
                var usingCols = usingColsText.Split(',');

                // Build ON predicate: left.col1 = right.col1 AND left.col2 = right.col2
                onPredicate = null;
                foreach (var col in usingCols)
                {
                    var colName = col.Trim();
                    var eqExpr = new SqlWhereComparisonExpression(
                        new SqlWhereColumnExpression(colName, colName),
                        SqlWhereComparisonOperator.Equal,
                        new SqlWhereColumnExpression(colName, colName));

                    onPredicate = onPredicate == null
                        ? eqExpr
                        : new SqlWhereAndExpression(new[] { onPredicate, eqExpr });
                }

                var nextJoin = FindJoinKeyword(sql, closeParen, endBeforeWhere);
                predicateEnd = nextJoin >= 0 ? nextJoin : endBeforeWhere;
                pos = closeParen;
            }
            else if (kind == SqlJoinKind.Cross)
            {
                // CROSS JOIN has no ON/USING — just the table.
                onPredicate = null; // unused for cross joins
                var nextJoin = FindJoinKeyword(sql, pos, endBeforeWhere);
                pos = nextJoin >= 0 ? nextJoin : endBeforeWhere;
            }
            else if (onIdx >= 0)
            {
                // Parse ON predicate: from after ON to next JOIN keyword or end
                var afterOn = onIdx + 2; // "ON".Length
                while (afterOn < endBeforeWhere && char.IsWhiteSpace(sql[afterOn])) afterOn++;

                var nextJoin = FindJoinKeyword(sql, afterOn, endBeforeWhere);
                predicateEnd = nextJoin >= 0 ? nextJoin : endBeforeWhere;

                var onText = sql[afterOn..predicateEnd].Trim();
                var (parsed, _) = SqlWhereParser.ParseWithParameters(onText);
                onPredicate = parsed;
                pos = predicateEnd;
            }
            else
            {
                throw new NotSupportedException("JOIN requires ON or USING clause.");
            }

            joins.Add(new SqlJoinClause(kind, tableName, alias, onPredicate, joinDerivedTable));
        }

        return joins.Count > 0 ? joins : null;
    }

    private static int FindOnKeyword(string sql, int start, int end)
    {
        var inString = false;
        var depth = 0;
        for (var i = start; i < end - 1; i++)
        {
            var c = sql[i];
            if (c == '\'' && (i == 0 || sql[i - 1] != '\\')) { inString = !inString; continue; }
            if (inString) continue;
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0) continue;

            if ((c == 'O' || c == 'o') && (sql[i + 1] == 'N' || sql[i + 1] == 'n'))
            {
                // Ensure ON is a standalone word
                var beforeOk = i == start || char.IsWhiteSpace(sql[i - 1]);
                var afterOk = i + 2 >= end || char.IsWhiteSpace(sql[i + 2]);
                if (beforeOk && afterOk) return i;
            }
        }
        return -1;
    }

    private static int FindUsingKeyword(string sql, int start, int end)
    {
        var inString = false;
        var depth = 0;
        for (var i = start; i < end - 5; i++)
        {
            var c = sql[i];
            if (c == '\'' && (i == 0 || sql[i - 1] != '\\')) { inString = !inString; continue; }
            if (inString) continue;
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0) continue;

            if ((c == 'U' || c == 'u') && i + 5 < end
                && (sql[i + 1] == 'S' || sql[i + 1] == 's')
                && (sql[i + 2] == 'I' || sql[i + 2] == 'i')
                && (sql[i + 3] == 'N' || sql[i + 3] == 'n')
                && (sql[i + 4] == 'G' || sql[i + 4] == 'g'))
            {
                var beforeOk = i == start || char.IsWhiteSpace(sql[i - 1]);
                var afterOk = i + 5 >= end || char.IsWhiteSpace(sql[i + 5]) || sql[i + 5] == '(';
                if (beforeOk && afterOk) return i;
            }
        }
        return -1;
    }

    // ── DDL: ALTER TABLE / VIEWS ────────────────────────────────────────────

    private static SqlAlterTableStatement ParseAlterTable(string sql)
    {
        var afterAlter = "ALTER TABLE".Length;
        // Find the table name (before the action keyword)
        var actionStart = -1;
        SqlAlterActionType action = default;

        // Check for ADD COLUMN
        var addIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ADD COLUMN", afterAlter);
        if (addIdx >= 0) { actionStart = addIdx; action = SqlAlterActionType.AddColumn; }

        if (actionStart < 0)
        {
            var dropIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "DROP COLUMN", afterAlter);
            if (dropIdx >= 0) { actionStart = dropIdx; action = SqlAlterActionType.DropColumn; }
        }

        if (actionStart < 0)
        {
            var altIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ALTER COLUMN", afterAlter);
            if (altIdx >= 0) { actionStart = altIdx; action = SqlAlterActionType.AlterColumn; }
        }

        if (actionStart < 0)
        {
            var renColIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "RENAME COLUMN", afterAlter);
            if (renColIdx >= 0) { actionStart = renColIdx; action = SqlAlterActionType.RenameColumn; }
        }

        if (actionStart < 0)
        {
            var renTblIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "RENAME TO", afterAlter);
            if (renTblIdx >= 0) { actionStart = renTblIdx; action = SqlAlterActionType.RenameTable; }
        }

        if (actionStart < 0)
        {
            var addConIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ADD CONSTRAINT", afterAlter);
            if (addConIdx >= 0) { actionStart = addConIdx; action = SqlAlterActionType.AddConstraint; }
        }

        if (actionStart < 0)
        {
            var addChkIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ADD CHECK", afterAlter);
            if (addChkIdx >= 0) { actionStart = addChkIdx; action = SqlAlterActionType.AddConstraint; }
        }

        if (actionStart < 0)
        {
            var addFkIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "ADD FOREIGN KEY", afterAlter);
            if (addFkIdx >= 0) { actionStart = addFkIdx; action = SqlAlterActionType.AddConstraint; }
        }

        if (actionStart < 0)
        {
            var dropConIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "DROP CONSTRAINT", afterAlter);
            if (dropConIdx >= 0) { actionStart = dropConIdx; action = SqlAlterActionType.DropConstraint; }
        }

        if (actionStart < 0)
            throw new NotSupportedException("ALTER TABLE requires ADD COLUMN, DROP COLUMN, ALTER COLUMN, RENAME COLUMN, RENAME TO, ADD [CONSTRAINT ...] CHECK, ADD FOREIGN KEY, or DROP CONSTRAINT.");

        var tableName = SqlSyntaxText.NormalizeIdentifier(sql[afterAlter..actionStart].Trim());

        switch (action)
        {
            case SqlAlterActionType.AddColumn:
            {
                var afterAdd = actionStart + "ADD COLUMN".Length;
                var remaining = sql[afterAdd..].Trim();
                // columnName TYPE [DEFAULT value] [NOT NULL] [COLLATE name]
                var parts = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    throw new NotSupportedException("ADD COLUMN requires column name and type.");
                var colName = SqlSyntaxText.NormalizeIdentifier(parts[0]);
                var type = ParseScalarType(parts[1]);
                object? defaultVal = null;
                bool? notNull = null;
                string? collation = null;
                for (int i = 2; i < parts.Length; i++)
                {
                    if (parts[i].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                        defaultVal = parts[++i];
                    else if (parts[i].Equals("NOT", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length && parts[i + 1].Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    { notNull = true; i++; }
                    else if (parts[i].Equals("COLLATE", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                        collation = NormalizeCollationName(parts[++i]);
                }
                return new SqlAlterTableStatement(tableName, action, ColumnName: colName, NewType: type, DefaultValue: defaultVal, NotNull: notNull, Collation: collation);
            }

            case SqlAlterActionType.DropColumn:
            {
                var afterDrop = actionStart + "DROP COLUMN".Length;
                var colName = SqlSyntaxText.NormalizeIdentifier(sql[afterDrop..].Trim());
                return new SqlAlterTableStatement(tableName, action, ColumnName: colName);
            }

            case SqlAlterActionType.AlterColumn:
            {
                var afterAlter2 = actionStart + "ALTER COLUMN".Length;
                var remaining = sql[afterAlter2..].Trim();
                var parts = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !parts[1].Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("ALTER COLUMN requires: ALTER COLUMN name TYPE newType [NOT NULL|NULL]");
                var colName = SqlSyntaxText.NormalizeIdentifier(parts[0]);
                var type = ParseScalarType(parts[2]);
                bool? notNull = null;
                if (parts.Length > 3 && parts[3].Equals("NOT", StringComparison.OrdinalIgnoreCase)) notNull = true;
                else if (parts.Length > 3 && parts[3].Equals("NULL", StringComparison.OrdinalIgnoreCase)) notNull = false;
                return new SqlAlterTableStatement(tableName, action, ColumnName: colName, NewType: type, NotNull: notNull);
            }

            case SqlAlterActionType.RenameColumn:
            {
                var afterRename = actionStart + "RENAME COLUMN".Length;
                var remaining = sql[afterRename..].Trim();
                var toIdx = remaining.IndexOf(" TO ", StringComparison.OrdinalIgnoreCase);
                if (toIdx < 0) throw new NotSupportedException("RENAME COLUMN requires TO keyword.");
                var oldName = SqlSyntaxText.NormalizeIdentifier(remaining[..toIdx].Trim());
                var newName = SqlSyntaxText.NormalizeIdentifier(remaining[(toIdx + 4)..].Trim());
                return new SqlAlterTableStatement(tableName, action, ColumnName: oldName, NewColumnName: newName);
            }

            case SqlAlterActionType.RenameTable:
            {
                var afterRename = actionStart + "RENAME TO".Length;
                var newTableName = SqlSyntaxText.NormalizeIdentifier(sql[afterRename..].Trim());
                return new SqlAlterTableStatement(tableName, action, NewTableName: newTableName);
            }

            case SqlAlterActionType.AddConstraint:
            {
                // Four forms:
                //   ADD CONSTRAINT name CHECK (expr)
                //   ADD CHECK (expr)                           (auto-named CHECK)
                //   ADD CONSTRAINT name FOREIGN KEY (col) REFERENCES other(col) [ON DELETE ...] [ON UPDATE ...]
                //   ADD FOREIGN KEY (col) REFERENCES other(col) [ON DELETE ...] [ON UPDATE ...]
                string? constraintName = null;

                if (sql.IndexOf("ADD CONSTRAINT", actionStart, StringComparison.OrdinalIgnoreCase) == actionStart)
                {
                    var afterCon = actionStart + "ADD CONSTRAINT".Length;
                    var tail = sql[afterCon..];

                    // Check for FOREIGN KEY anywhere in the tail (ADD CONSTRAINT name FOREIGN KEY ...)
                    var fkIdx = tail.IndexOf("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);
                    if (fkIdx >= 0)
                    {
                        // Parse the FOREIGN KEY part
                        var fkDef = ParseForeignKeyDefinition(tail[fkIdx..]);
                        // Extract constraint name from before FOREIGN KEY
                        constraintName = SqlSyntaxText.NormalizeIdentifier(tail[..fkIdx].Trim());
                        fkDef = fkDef with { ConstraintName = constraintName ?? string.Empty };
                        return new SqlAlterTableStatement(
                            tableName, action, ConstraintName: constraintName, ForeignKey: fkDef);
                    }

                    // Fall back to CHECK constraint
                    var chkRel = FindCheckKeyword(tail);
                    if (chkRel < 0)
                        throw new NotSupportedException("ADD CONSTRAINT only supports CHECK and FOREIGN KEY constraints.");
                    constraintName = SqlSyntaxText.NormalizeIdentifier(tail[..chkRel].Trim());
                    var checkClause = tail[chkRel..];
                    var expr = ExtractCheckExpression(checkClause);
                    if (string.IsNullOrEmpty(constraintName))
                        constraintName = $"{tableName}_check";
                    return new SqlAlterTableStatement(
                        tableName, action, ConstraintName: constraintName, CheckExpression: expr);
                }
                else if (sql.IndexOf("ADD FOREIGN KEY", actionStart, StringComparison.OrdinalIgnoreCase) == actionStart)
                {
                    // ADD FOREIGN KEY (col) REFERENCES other(col) ...
                    var afterFk = actionStart + "ADD ".Length;
                    var fkRaw = sql[afterFk..];
                    var fkDef = ParseForeignKeyDefinition(fkRaw);
                    constraintName = fkDef.ConstraintName;
                    if (string.IsNullOrEmpty(constraintName))
                        constraintName = $"FK_{tableName}_{string.Join("_", fkDef.ColumnNames)}";
                    fkDef = fkDef with { ConstraintName = constraintName };
                    return new SqlAlterTableStatement(
                        tableName, action, ConstraintName: constraintName, ForeignKey: fkDef);
                }
                else
                {
                    // ADD CHECK (...) — clause starts at the CHECK keyword.
                    var checkClause = sql[(actionStart + "ADD ".Length)..];
                    var expr = ExtractCheckExpression(checkClause);
                    if (string.IsNullOrEmpty(constraintName))
                        constraintName = $"{tableName}_check";
                    return new SqlAlterTableStatement(
                        tableName, action, ConstraintName: constraintName, CheckExpression: expr);
                }
            }

            case SqlAlterActionType.DropConstraint:
            {
                var afterDrop = actionStart + "DROP CONSTRAINT".Length;
                var name = SqlSyntaxText.NormalizeIdentifier(sql[afterDrop..].Trim());
                return new SqlAlterTableStatement(tableName, action, ConstraintName: name);
            }

            default:
                throw new NotSupportedException($"ALTER TABLE action '{action}' is not supported.");
        }
    }

    private static SqlScalarType ParseScalarType(string typeName)
    {
        return typeName.ToUpperInvariant() switch
        {
            "INT" or "INT32" or "INTEGER" => SqlScalarType.Int32,
            "LONG" or "INT64" or "BIGINT" => SqlScalarType.Int64,
            "SMALLINT" or "INT16" => SqlScalarType.Int16,
            "STRING" or "TEXT" or "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" => SqlScalarType.String,
            "DOUBLE" or "FLOAT" or "REAL" => SqlScalarType.Double,
            "DECIMAL" or "NUMERIC" or "MONEY" => SqlScalarType.Decimal,
            "BOOL" or "BOOLEAN" or "BIT" => SqlScalarType.Boolean,
            "DATETIME" or "DATETIME2" or "TIMESTAMP" => SqlScalarType.DateTime,
            "DATE" => SqlScalarType.Date,
            "TIME" => SqlScalarType.Time,
            "GUID" or "UUID" or "UNIQUEIDENTIFIER" => SqlScalarType.Guid,
            "BINARY" or "BLOB" or "BYTEA" or "VARBINARY" => SqlScalarType.Binary,
            "JSON" => SqlScalarType.Json,
            _ => throw new NotSupportedException($"Unknown type '{typeName}'.")
        };
    }

    private static SqlCreateViewStatement ParseCreateView(string sql)
    {
        var afterCreate = "CREATE VIEW".Length;
        var asIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "AS", afterCreate);
        if (asIdx < 0)
            throw new NotSupportedException("CREATE VIEW requires AS keyword.");
        var viewName = SqlSyntaxText.NormalizeIdentifier(sql[afterCreate..asIdx].Trim());
        var selectSql = sql[(asIdx + 2)..].Trim();
        var selectStmt = ParseSelect(selectSql);
        return new SqlCreateViewStatement(viewName, selectStmt);
    }

    private static SqlDropViewStatement ParseDropView(string sql)
    {
        var viewName = SqlSyntaxText.NormalizeIdentifier(sql["DROP VIEW".Length..].Trim());
        return new SqlDropViewStatement(viewName);
    }

    // ── Stored Procedure / Trigger Parsing ──────────────────────────────────────

    private static SqlCreateProcedureStatement ParseCreateProcedureStatement(string sql)
    {
        var orReplace = false;
        var remaining = sql;
        if (remaining.StartsWith("CREATE OR REPLACE PROCEDURE", StringComparison.OrdinalIgnoreCase))
        {
            orReplace = true;
            remaining = remaining["CREATE OR REPLACE PROCEDURE".Length..].TrimStart();
        }
        else
        {
            remaining = remaining["CREATE PROCEDURE".Length..].TrimStart();
        }

        // Parse procedure name — first whitespace-delimited token (or up to '(')
        var nameEnd = remaining.Length;
        var spaceIdx = remaining.IndexOf(' ');
        var tabIdx = remaining.IndexOf('\t');
        var nlIdx = remaining.IndexOf('\n');
        var parenIdx = remaining.IndexOf('(');
        if (spaceIdx >= 0) nameEnd = Math.Min(nameEnd, spaceIdx);
        if (tabIdx >= 0) nameEnd = Math.Min(nameEnd, tabIdx);
        if (nlIdx >= 0) nameEnd = Math.Min(nameEnd, nlIdx);
        if (parenIdx >= 0) nameEnd = Math.Min(nameEnd, parenIdx);
        var procName = SqlSyntaxText.NormalizeIdentifier(remaining[..nameEnd].Trim());
        remaining = remaining[nameEnd..].TrimStart();

        // Parse parameters (two forms: (...) or bare @param declarations before AS)
        var parameters = new List<SqlProcedureParameter>();
        if (remaining.Length > 0 && remaining[0] == '(')
        {
            var closeIdx = SqlSyntaxText.FindMatchingParen(remaining, 0);
            var paramsText = remaining[1..closeIdx].Trim();
            if (!string.IsNullOrWhiteSpace(paramsText))
                parameters.AddRange(ParseProcedureParameters(paramsText));
            remaining = remaining[(closeIdx + 1)..].TrimStart();
        }
        else if (remaining.Length > 0 && remaining[0] == '@')
        {
            // SQL Server style: @param TYPE, @param2 TYPE ... AS
            var asIdx = FindKeywordIndex(remaining, "AS");
            if (asIdx >= 0)
            {
                var paramsText = remaining[..asIdx].Trim();
                if (!string.IsNullOrWhiteSpace(paramsText))
                    parameters.AddRange(ParseProcedureParameters(paramsText));
                remaining = remaining[asIdx..].TrimStart();
            }
        }

        // Expect AS
        if (remaining.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            remaining = remaining[2..].TrimStart();

        // Check for CSHARP language
        var language = "sql";
        if (remaining.StartsWith("CSHARP", StringComparison.OrdinalIgnoreCase))
        {
            language = "csharp";
            remaining = remaining["CSHARP".Length..].TrimStart();
        }

        var body = ExtractBeginEndBody(remaining, language == "csharp");

        // RemoveTrailingSemicolon (line 16) strips the final ; from the outer SQL,
        // but for CSHARP bodies without BEGIN/END that ; is C# syntax, not SQL punctuation.
        if (language == "csharp" && body.Length > 0 && body[^1] != ';' && body[^1] != '}')
            body += ";";

        return new SqlCreateProcedureStatement(procName, parameters, body, orReplace, language);
    }

    private static List<SqlProcedureParameter> ParseProcedureParameters(string paramsText)
    {
        var result = new List<SqlProcedureParameter>();
        var parts = SqlSyntaxText.SplitTopLevel(paramsText, ',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // @name TYPE [= default] [OUTPUT]
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 0) continue;
            var paramName = SqlSyntaxText.NormalizeIdentifier(trimmed[..spaceIdx].Trim());

            var afterName = trimmed[spaceIdx..].TrimStart();
            var typeEndIdx = afterName.IndexOfAny(new[] { ' ', '\t', '=' });
            var typeStr = typeEndIdx >= 0 ? afterName[..typeEndIdx] : afterName;
            var type = ParseSqlType(typeStr.TrimEnd(')', ' ', '\t'));

            var isOutput = trimmed.ToUpperInvariant().Contains("OUTPUT");
            object? defaultValue = null;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx >= 0)
            {
                var valText = trimmed[(eqIdx + 1)..].Trim();
                // Remove OUTPUT if trailing
                var outIdx = valText.LastIndexOf("OUTPUT", StringComparison.OrdinalIgnoreCase);
                if (outIdx >= 0) valText = valText[..outIdx].Trim();
                if (valText.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    defaultValue = null;
                else
                    defaultValue = ParseProcedureDefaultLiteral(valText, type);
            }

            result.Add(new SqlProcedureParameter(paramName, type, isOutput, true, defaultValue));
        }
        return result;
    }

    private static SqlDropProcedureStatement ParseDropProcedureStatement(string sql)
    {
        var remaining = sql["DROP PROCEDURE".Length..].TrimStart();
        var ifExists = false;
        if (remaining.StartsWith("IF EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            ifExists = true;
            remaining = remaining["IF EXISTS".Length..].TrimStart();
        }
        return new SqlDropProcedureStatement(
            SqlSyntaxText.NormalizeIdentifier(remaining.Trim()), ifExists);
    }

    private static SqlExecStatement ParseExecStatement(string sql)
    {
        var trimmed = sql.Trim();
        string prefix;
        if (trimmed.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase))
            prefix = "EXECUTE";
        else
            prefix = "EXEC";

        var afterPrefix = trimmed[prefix.Length..].TrimStart();
        // Find procedure name (before first space or '(' or end)
        var nameEnd = afterPrefix.Length;
        var spaceIdx = afterPrefix.IndexOf(' ');
        var parenIdx = afterPrefix.IndexOf('(');
        var candidates = new[] { spaceIdx, parenIdx }.Where(i => i >= 0).ToArray();
        if (candidates.Length > 0) nameEnd = candidates.Min();

        var procName = SqlSyntaxText.NormalizeIdentifier(afterPrefix[..nameEnd].Trim());
        var argsText = afterPrefix[nameEnd..].Trim().TrimStart('(', ' ').TrimEnd(')');

        var arguments = new List<SqlExecArgument>();
        if (!string.IsNullOrWhiteSpace(argsText))
        {
            var parts = SqlSyntaxText.SplitTopLevel(argsText, ',');
            foreach (var p in parts)
            {
                var part = p.Trim();
                if (string.IsNullOrWhiteSpace(part)) continue;
                var eqIdx = part.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var pName = SqlSyntaxText.NormalizeIdentifier(part[..eqIdx].Trim());
                    var valExpr = part[(eqIdx + 1)..].Trim();
                    arguments.Add(new SqlExecArgument(pName, valExpr));
                }
                else
                {
                    arguments.Add(new SqlExecArgument(null, part));
                }
            }
        }

        return new SqlExecStatement(procName, arguments);
    }

    private static SqlCreateTriggerStatement ParseCreateTriggerStatement(string sql)
    {
        var orReplace = false;
        var remaining = sql;
        if (remaining.StartsWith("CREATE OR REPLACE TRIGGER", StringComparison.OrdinalIgnoreCase))
        {
            orReplace = true;
            remaining = remaining["CREATE OR REPLACE TRIGGER".Length..].TrimStart();
        }
        else
        {
            remaining = remaining["CREATE TRIGGER".Length..].TrimStart();
        }

        // Trigger name then ON keyword
        var onIdx = remaining.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        if (onIdx < 0)
            throw new NotSupportedException("CREATE TRIGGER requires ON <table>.");
        var triggerName = SqlSyntaxText.NormalizeIdentifier(remaining[..onIdx].Trim());
        remaining = remaining[(onIdx + 4)..].TrimStart();

        // Table name, then timing, then event, then AS
        var tableNameEnd = remaining.Length;
        var timingIdx = -1;
        foreach (var kw in new[] { " BEFORE ", " AFTER ", " INSTEAD OF " })
        {
            var idx = remaining.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (timingIdx < 0 || idx < timingIdx))
            {
                timingIdx = idx;
                tableNameEnd = idx;
            }
        }
        if (timingIdx < 0)
            throw new NotSupportedException("CREATE TRIGGER requires BEFORE/AFTER/INSTEAD OF timing.");

        var tableName = SqlSyntaxText.NormalizeIdentifier(remaining[..tableNameEnd].Trim());
        remaining = remaining[timingIdx..].TrimStart();

        // Timing
        var timing = SqlTriggerTiming.After;
        if (remaining.StartsWith("BEFORE", StringComparison.OrdinalIgnoreCase))
        {
            timing = SqlTriggerTiming.Before;
            remaining = remaining["BEFORE".Length..].TrimStart();
        }
        else if (remaining.StartsWith("INSTEAD OF", StringComparison.OrdinalIgnoreCase))
        {
            timing = SqlTriggerTiming.InsteadOf;
            remaining = remaining["INSTEAD OF".Length..].TrimStart();
        }
        else if (remaining.StartsWith("AFTER", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining["AFTER".Length..].TrimStart();
        }

        // Event
        var evt = SqlTriggerEvent.Insert;
        if (remaining.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            evt = SqlTriggerEvent.Insert;
            remaining = remaining["INSERT".Length..].TrimStart();
        }
        else if (remaining.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            evt = SqlTriggerEvent.Update;
            remaining = remaining["UPDATE".Length..].TrimStart();
        }
        else if (remaining.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            evt = SqlTriggerEvent.Delete;
            remaining = remaining["DELETE".Length..].TrimStart();
        }

        // Expect AS
        if (remaining.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            remaining = remaining["AS".Length..].TrimStart();

        var body = ExtractBeginEndBody(remaining, false);
        return new SqlCreateTriggerStatement(triggerName, tableName, evt, timing, body, orReplace);
    }

    private static SqlDropTriggerStatement ParseDropTriggerStatement(string sql)
    {
        var remaining = sql["DROP TRIGGER".Length..].TrimStart();
        var ifExists = false;
        if (remaining.StartsWith("IF EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            ifExists = true;
            remaining = remaining["IF EXISTS".Length..].TrimStart();
        }
        return new SqlDropTriggerStatement(
            SqlSyntaxText.NormalizeIdentifier(remaining.Trim()), ifExists);
    }

    private static SqlTruncateTableStatement ParseTruncateTable(string sql)
    {
        var tableName = sql["TRUNCATE TABLE".Length..].Trim();
        return new SqlTruncateTableStatement(SqlSyntaxText.NormalizeIdentifier(tableName));
    }

    private static SqlDescribeStatement ParseDescribe(string sql)
    {
        var tableName = sql["DESCRIBE".Length..].Trim();
        return new SqlDescribeStatement(SqlSyntaxText.NormalizeIdentifier(tableName));
    }

    private static SqlMergeStatement ParseMerge(string sql)
    {
        var remaining = sql["MERGE".Length..].TrimStart();
        if (!remaining.StartsWith("INTO", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("MERGE must be followed by INTO.");
        remaining = remaining["INTO".Length..].TrimStart();

        var targetEnd = remaining.IndexOf(' ', StringComparison.Ordinal);
        if (targetEnd < 0) throw new NotSupportedException("Expected target table name after MERGE INTO.");
        var targetTable = SqlSyntaxText.NormalizeIdentifier(remaining[..targetEnd]);
        remaining = remaining[targetEnd..].TrimStart();

        if (!remaining.StartsWith("USING", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Expected USING clause in MERGE.");
        remaining = remaining["USING".Length..].TrimStart();

        var sourceEnd = remaining.IndexOf(' ', StringComparison.Ordinal);
        if (sourceEnd < 0) throw new NotSupportedException("Expected source table name after USING.");
        var sourceTable = SqlSyntaxText.NormalizeIdentifier(remaining[..sourceEnd]);
        remaining = remaining[sourceEnd..].TrimStart();

        var sourceAlias = sourceTable;
        if (remaining.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining["AS".Length..].TrimStart();
            var aliasEnd = remaining.IndexOf(' ');
            if (aliasEnd < 0) aliasEnd = remaining.IndexOf('\t');
            if (aliasEnd < 0) aliasEnd = remaining.Length;
            sourceAlias = SqlSyntaxText.NormalizeIdentifier(remaining[..aliasEnd]);
            remaining = remaining[aliasEnd..].TrimStart();
        }

        if (!remaining.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Expected ON clause in MERGE.");
        remaining = remaining["ON".Length..].TrimStart();

        // Parse ON predicate up to WHEN using FindTopLevelKeyword
        var whenIdx = SqlSyntaxText.FindTopLevelKeyword(remaining, "WHEN", 0);
        if (whenIdx < 0) throw new NotSupportedException("Expected WHEN MATCHED clause in MERGE.");
        var predText = remaining[..whenIdx].Trim();
        var onPredicate = SqlWhereParser.Parse(predText);
        remaining = remaining[whenIdx..].TrimStart();

        IReadOnlyDictionary<string, string>? updateAssignments = null;
        IReadOnlyList<string>? insertColumns = null;

        if (remaining.StartsWith("WHEN MATCHED", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining["WHEN MATCHED".Length..].TrimStart();
            if (!remaining.StartsWith("THEN", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Expected THEN after WHEN MATCHED.");
            remaining = remaining["THEN".Length..].TrimStart();
            if (!remaining.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Expected UPDATE after WHEN MATCHED THEN.");
            remaining = remaining["UPDATE".Length..].TrimStart();
            if (!remaining.StartsWith("SET", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Expected SET after UPDATE in MERGE.");
            remaining = remaining["SET".Length..].TrimStart();

            var nextWhenIdx = FindKeywordIndex(remaining, "WHEN");
            var setClause = nextWhenIdx >= 0 ? remaining[..nextWhenIdx].Trim() : remaining.Trim();
            updateAssignments = ParseAssignments(setClause);
            if (nextWhenIdx >= 0) remaining = remaining[nextWhenIdx..].TrimStart();
        }

        if (remaining.StartsWith("WHEN NOT MATCHED", StringComparison.OrdinalIgnoreCase))
        {
            remaining = remaining["WHEN NOT MATCHED".Length..].TrimStart();
            if (!remaining.StartsWith("THEN", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Expected THEN after WHEN NOT MATCHED.");
            remaining = remaining["THEN".Length..].TrimStart();
            if (!remaining.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Expected INSERT after WHEN NOT MATCHED THEN.");
            remaining = remaining["INSERT".Length..].TrimStart();

            var parenOpen = remaining.IndexOf('(');
            var parenClose = remaining.IndexOf(')');
            if (parenOpen < 0 || parenClose < 0)
                throw new NotSupportedException("Expected INSERT column list in MERGE.");
            var colList = remaining[(parenOpen + 1)..parenClose];
            insertColumns = colList.Split(',').Select(c => SqlSyntaxText.NormalizeIdentifier(c.Trim())).ToList();
        }

        return new SqlMergeStatement(targetTable, sourceTable, sourceAlias, onPredicate, updateAssignments, insertColumns);
    }

    private static string ExtractBeginEndBody(string sql, bool preserveSemicolons)
    {
        var trimmed = sql.Trim();
        if (trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            var beginEnd = trimmed["BEGIN".Length..];
            var depth = 1;
            var pos = 0;
            while (pos < beginEnd.Length && depth > 0)
            {
                if (beginEnd[pos] == '\'')
                {
                    pos++;
                    while (pos < beginEnd.Length && beginEnd[pos] != '\'') pos++;
                    pos++;
                    continue;
                }
                if (MatchKeywordAt(beginEnd, pos, "BEGIN"))
                {
                    depth++;
                    pos += 5;
                    continue;
                }
                if (MatchKeywordAt(beginEnd, pos, "END"))
                {
                    depth--;
                    if (depth == 0)
                        return beginEnd[..pos].Trim();
                }
                pos++;
            }
            return beginEnd.Trim();
        }
        return trimmed;
    }

    private static object? ParseProcedureDefaultLiteral(string text, SqlScalarType type)
    {
        text = text.Trim();
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            return text[1..^1].Replace("''", "'");

        return type switch
        {
            SqlScalarType.Int32 => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            SqlScalarType.Int64 => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            SqlScalarType.Double => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            SqlScalarType.Decimal => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            SqlScalarType.Boolean => bool.Parse(text),
            SqlScalarType.String => text,
            _ => text
        };
    }

    private static bool MatchKeywordAt(string s, int pos, string keyword)
    {
        if (pos + keyword.Length > s.Length) return false;
        if (string.Compare(s, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var end = pos + keyword.Length;
        return end >= s.Length || !char.IsLetterOrDigit(s[end]);
    }

    private static int FindKeywordIndex(string s, string keyword, int start = 0)
    {
        var idx = start;
        while (idx < s.Length)
        {
            var next = s.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase);
            if (next < 0) return -1;
            // Check word boundary: previous char must not be letter/digit, next char after keyword must not be
            var beforeOk = next == 0 || !char.IsLetterOrDigit(s[next - 1]);
            var after = next + keyword.Length;
            var afterOk = after >= s.Length || !char.IsLetterOrDigit(s[after]);
            if (beforeOk && afterOk) return next;
            idx = next + keyword.Length;
        }
        return -1;
    }

    private static SqlStatement ParseExplain(string sql)
    {
        // Strip EXPLAIN prefix and parse the inner statement.
        var innerSql = sql[7..].Trim();
        if (innerSql.Length == 0)
            throw new NotSupportedException("EXPLAIN must be followed by a statement.");
        return new SqlExplainStatement(innerSql);
    }

    private static SqlSetTransactionStatement ParseSetTransaction(string sql)
    {
        // "SET TRANSACTION ISOLATION LEVEL <level>"
        var remaining = sql["SET TRANSACTION".Length..].TrimStart();
        var isolationIdx = SqlSyntaxText.FindTopLevelKeyword(remaining, "ISOLATION LEVEL", 0);
        if (isolationIdx < 0)
            throw new NotSupportedException("SET TRANSACTION requires ISOLATION LEVEL clause.");

        var levelName = remaining[(isolationIdx + "ISOLATION LEVEL".Length)..].Trim();
        if (!ValidIsolationLevels.Contains(levelName))
            throw new NotSupportedException(
                $"Unsupported isolation level: '{levelName}'. Supported: READ UNCOMMITTED, READ COMMITTED, REPEATABLE READ, SERIALIZABLE.");

        return new SqlSetTransactionStatement(levelName);
    }

    private static SqlSetTransactionModeStatement ParseSetTransactionMode(string sql)
    {
        // "SET WALHALLA.TRANSACTION_MODE = 'locking'|'mvcc'"
        var remaining = sql["SET WALHALLA.TRANSACTION_MODE".Length..].TrimStart();
        if (remaining.Length >= 1 && remaining[0] == '=')
            remaining = remaining[1..].TrimStart();
        remaining = remaining.Trim('\'', '"').Trim();
        if (remaining.Length == 0)
            throw new NotSupportedException(
                "SET WALHALLA.TRANSACTION_MODE requires a mode name: 'locking' or 'mvcc'.");
        var upper = remaining.ToUpperInvariant();
        if (upper != "LOCKING" && upper != "MVCC")
            throw new NotSupportedException(
                $"Unsupported transaction mode: '{remaining}'. Supported: 'locking', 'mvcc'.");
        return new SqlSetTransactionModeStatement(upper);
    }

    private static SqlVacuumStatement ParseVacuum(string sql)
    {
        var remaining = sql["VACUUM".Length..].Trim();

        if (remaining.Length >= 4
            && SqlSyntaxText.MatchesKeywordAt(remaining, 0, "FULL"))
        {
            throw new NotSupportedException(
                "VACUUM FULL is not yet supported. Use VACUUM or VACUUM <table_name>.");
        }

        if (remaining.Length == 0)
            return new SqlVacuumStatement(null);

        return new SqlVacuumStatement(SqlSyntaxText.NormalizeIdentifier(remaining));
    }

    private static SqlAnalyzeStatement ParseAnalyze(string sql)
    {
        var remaining = sql["ANALYZE".Length..].Trim();

        if (remaining.Length == 0)
            return new SqlAnalyzeStatement(null);

        return new SqlAnalyzeStatement(SqlSyntaxText.NormalizeIdentifier(remaining));
    }

    private static SqlCopyStatement ParseCopy(string sql)
    {
        // COPY table_name [(col1, col2, ...)] FROM STDIN [WITH (options)]
        // COPY table_name [(col1, col2, ...)] TO STDOUT [WITH (options)]
        var pos = "COPY".Length;
        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;

        // Parse table name
        var tableName = SqlSyntaxText.ReadIdentifier(sql, ref pos);
        if (string.IsNullOrEmpty(tableName))
            throw new NotSupportedException("COPY requires a table name.");

        // Optional column list: (col1, col2, ...)
        List<string>? columns = null;
        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
        if (pos < sql.Length && sql[pos] == '(')
        {
            pos++;
            columns = new List<string>();
            while (true)
            {
                while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
                var col = SqlSyntaxText.ReadIdentifier(sql, ref pos);
                if (!string.IsNullOrEmpty(col))
                    columns.Add(col);
                while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
                if (pos < sql.Length && sql[pos] == ',')
                { pos++; continue; }
                if (pos < sql.Length && sql[pos] == ')')
                { pos++; break; }
                throw new NotSupportedException("Invalid column list in COPY statement.");
            }
        }

        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;

        // Direction: FROM or TO
        SqlCopyDirection direction;
        if (SqlSyntaxText.MatchesKeywordAt(sql, pos, "FROM"))
        {
            pos += "FROM".Length;
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            if (!SqlSyntaxText.MatchesKeywordAt(sql, pos, "STDIN"))
                throw new NotSupportedException("COPY FROM only supports STDIN.");
            pos += "STDIN".Length;
            direction = SqlCopyDirection.FromStdin;
        }
        else if (SqlSyntaxText.MatchesKeywordAt(sql, pos, "TO"))
        {
            pos += "TO".Length;
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            if (!SqlSyntaxText.MatchesKeywordAt(sql, pos, "STDOUT"))
                throw new NotSupportedException("COPY TO only supports STDOUT.");
            pos += "STDOUT".Length;
            direction = SqlCopyDirection.ToStdout;
        }
        else
        {
            throw new NotSupportedException("COPY requires FROM or TO clause.");
        }

        // Parse WITH options
        var options = new SqlCopyOptions();
        while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
        if (SqlSyntaxText.MatchesKeywordAt(sql, pos, "WITH"))
        {
            pos += "WITH".Length;
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            if (pos < sql.Length && sql[pos] == '(')
            {
                pos++;
                var opts = ParseCopyOptions(sql, ref pos);
                options = opts;
            }
        }

        return new SqlCopyStatement(tableName, direction, options, columns?.AsReadOnly());
    }

    private static SqlCopyOptions ParseCopyOptions(string sql, ref int pos)
    {
        var format = SqlCopyFormat.Text;
        string? delimiter = null;
        string? nullMarker = null;
        bool header = false;
        string? quote = null;
        string? escape = null;

        while (true)
        {
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            var key = SqlSyntaxText.ReadIdentifier(sql, ref pos);
            if (string.IsNullOrEmpty(key))
                break;

            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            if (pos < sql.Length && sql[pos] == '=')
                pos++;
            else if (pos < sql.Length && sql[pos] == ',')
            { pos++; continue; } // boolean flag without value (e.g., HEADER)
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;

            string? value = null;
            if (pos < sql.Length && sql[pos] == '\'')
            {
                pos++;
                var start = pos;
                while (pos < sql.Length && sql[pos] != '\'')
                    pos++;
                value = sql[start..pos];
                if (pos < sql.Length && sql[pos] == '\'')
                    pos++;
            }
            else
            {
                var start = pos;
                while (pos < sql.Length && sql[pos] != ',' && sql[pos] != ')')
                    pos++;
                value = sql[start..pos].Trim();
            }

            switch (key.ToUpperInvariant())
            {
                case "FORMAT":
                    format = value?.ToUpperInvariant() switch
                    {
                        "TEXT" => SqlCopyFormat.Text,
                        "CSV" => SqlCopyFormat.Csv,
                        "BINARY" => SqlCopyFormat.Binary,
                        _ => throw new NotSupportedException($"COPY format '{value}' not supported.")
                    };
                    break;
                case "DELIMITER":
                    delimiter = value;
                    break;
                case "NULL":
                    nullMarker = value;
                    break;
                case "HEADER":
                    header = true;
                    if (!string.IsNullOrEmpty(value) && bool.TryParse(value, out var h))
                        header = h;
                    break;
                case "QUOTE":
                    quote = value;
                    break;
                case "ESCAPE":
                    escape = value;
                    break;
            }

            while (pos < sql.Length && char.IsWhiteSpace(sql[pos])) pos++;
            if (pos < sql.Length && sql[pos] == ',')
            { pos++; continue; }
            if (pos < sql.Length && sql[pos] == ')')
            { pos++; break; }
        }

        return new SqlCopyOptions(format, delimiter, nullMarker, header, quote, escape);
    }

    private static string NormalizeCollationName(string raw)
    {
        // Strip quotes: "de-DE-x-icu" → de-DE-x-icu
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw[1..^1];
        return raw;
    }
}
