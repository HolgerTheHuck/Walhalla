using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WalhallaSql.Parsing;

namespace WalhallaSql.Execution;

/// <summary>
/// Rewrites correlated subquery patterns to equivalent JOIN-based queries for efficient set-based execution.
/// <list type="bullet">
///   <item>Scalar subquery projections in the SELECT list → LEFT JOIN</item>
///   <item>WHERE EXISTS (SELECT 1 FROM ...) predicates → INNER JOIN</item>
/// </list>
/// Rewrites are only attempted for single-table FROM clauses that have no existing JOINs.
/// Queries that cannot be safely identified as one of the supported patterns are returned unchanged.
/// </summary>
internal static class CorrelatedSubqueryRewriter
{
    // Keywords that cannot serve as table aliases after FROM.
    private static readonly string[] ForbiddenAliasKeywords =
    [
        "WHERE", "ORDER", "GROUP", "HAVING", "FETCH", "LIMIT", "OFFSET",
        "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "FULL", "ON", "AS",
        "SELECT", "UNION", "EXCEPT", "INTERSECT", "SET", "WITH",
    ];

    // Keywords that start a trailing clause after the WHERE predicate list.
    private static readonly string[] TrailingClauseKeywords =
        ["ORDER", "GROUP", "HAVING", "FETCH", "LIMIT", "OFFSET"];

    /// <summary>
    /// Tries to rewrite correlated subqueries to equivalent JOIN-based queries.
    /// Returns the (possibly rewritten) SQL.
    /// </summary>
    public static string Rewrite(string sql)
    {
        TryRewrite(sql, out var rewritten);
        return rewritten;
    }

    private static bool TryRewrite(string sql, out string rewritten)
    {
        var current = sql.Trim();
        var changed = false;

        if (TryRewriteScalarSubqueriesToLeftJoins(current, out var afterScalar))
        {
            current = afterScalar;
            changed = true;
        }

        if (TryRewriteExistsToInnerJoins(current, out var afterExists))
        {
            current = afterExists;
            changed = true;
        }

        rewritten = current;
        return changed;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Scalar subquery projection  →  LEFT JOIN
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryRewriteScalarSubqueriesToLeftJoins(string sql, out string rewritten)
    {
        rewritten = sql;

        if (!SqlSyntaxText.StartsWithKeyword(sql, "SELECT"))
            return false;

        var fromIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "FROM", 6);
        if (fromIdx < 0)
            return false;

        var projectionRaw = sql[6..fromIdx].Trim();
        var fromAndBeyond = sql[fromIdx..];

        if (!TryParseFromClause(fromAndBeyond,
                out var outerTable, out var outerAlias,
                out var existingJoins,
                out var whereClause, out var tailClause))
            return false;

        var projectionParts = SqlSyntaxText.SplitTopLevel(projectionRaw, ',');
        if (projectionParts.Length == 0)
            return false;

        var normalParts = new List<string>();
        var subqueryParts = new List<ParsedScalarSubquery>();
        var joinAliasCounter = 0;

        foreach (var part in projectionParts)
        {
            var trimmed = part.Trim();
            if (TryParseScalarSubqueryProjection(trimmed, out var parsed))
            {
                parsed = parsed with { JoinAlias = $"__j{joinAliasCounter++}", OriginalText = trimmed };
                subqueryParts.Add(parsed);
            }
            else
            {
                normalParts.Add(trimmed);
            }
        }

        if (subqueryParts.Count == 0)
            return false;

        var allProjections = new List<string>(normalParts);
        var joinSb = new StringBuilder();
        var hasRewrite = false;

        foreach (var sq in subqueryParts)
        {
            if (sq.Aggregate != null)
            {
                if (TryBuildAggregateJoinClause(sq, joinSb, out var aggProjExpr))
                {
                    allProjections.Add(aggProjExpr);
                    hasRewrite = true;
                }
                else
                {
                    // Cannot rewrite this aggregate — keep as original correlated subquery.
                    allProjections.Add(sq.OriginalText);
                }
            }
            else
            {
                // Rename the inner alias in the column expression so it references the JOIN alias.
                var renamedCol = ReplaceAlias(sq.InnerColumn, sq.InnerAlias, sq.JoinAlias);
                var proj = string.IsNullOrEmpty(sq.ProjectionAlias)
                    ? renamedCol
                    : $"{renamedCol} AS {sq.ProjectionAlias}";
                allProjections.Add(proj);
                var onConditions = ReplaceAlias(sq.InnerWhereRaw, sq.InnerAlias, sq.JoinAlias);
                joinSb.Append(" LEFT JOIN ").Append(sq.InnerTable).Append(' ').Append(sq.JoinAlias);
                joinSb.Append(" ON ").Append(onConditions);
                hasRewrite = true;
            }
        }

        if (!hasRewrite)
            return false;

        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", allProjections));
        sb.Append(" FROM ").Append(outerTable);
        if (!string.IsNullOrEmpty(outerAlias))
            sb.Append(' ').Append(outerAlias);
        if (!string.IsNullOrEmpty(existingJoins))
            sb.Append(' ').Append(existingJoins);
        sb.Append(joinSb);

        if (!string.IsNullOrWhiteSpace(whereClause))
            sb.Append(" WHERE ").Append(whereClause);

        if (!string.IsNullOrWhiteSpace(tailClause))
            sb.Append(' ').Append(tailClause);

        rewritten = sb.ToString();
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WHERE EXISTS (...)  →  INNER JOIN
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryRewriteExistsToInnerJoins(string sql, out string rewritten)
    {
        rewritten = sql;

        if (!SqlSyntaxText.StartsWithKeyword(sql, "SELECT"))
            return false;

        var fromIdx = SqlSyntaxText.FindTopLevelKeyword(sql, "FROM", 6);
        if (fromIdx < 0)
            return false;

        var projectionRaw = sql[6..fromIdx].Trim();
        var fromAndBeyond = sql[fromIdx..];

        if (!TryParseFromClause(fromAndBeyond,
                out var outerTable, out var outerAlias,
                out var existingJoins,
                out var whereClause, out var tailClause))
            return false;

        if (string.IsNullOrWhiteSpace(whereClause))
            return false;

        var andParts = SplitByTopLevelAnd(whereClause);
        var existsParts = new List<ParsedExists>();
        var residualParts = new List<string>();
        var joinAliasCounter = 0;

        foreach (var part in andParts)
        {
            var trimmed = part.Trim();
            if (TryParseExistsExpression(trimmed, out var parsed) && !parsed.Negated)
            {
                parsed = parsed with { JoinAlias = $"__e{joinAliasCounter++}" };
                // Skip EXISTS-to-JOIN rewrite when the inner WHERE references aliases from an
                // outer-outer scope (i.e. not the local outer table alias or the inner alias).
                // Such references are unresolvable as ON predicates within the current scope.
                if (ExistsHasOuterScopeAliasReferences(parsed.InnerWhereRaw, outerAlias, parsed.InnerAlias))
                    residualParts.Add(trimmed);
                else
                    existsParts.Add(parsed);
            }
            else
            {
                residualParts.Add(trimmed);
            }
        }

        if (existsParts.Count == 0)
            return false;

        var sb = new StringBuilder("SELECT ").Append(projectionRaw);
        sb.Append(" FROM ").Append(outerTable);
        if (!string.IsNullOrEmpty(outerAlias))
            sb.Append(' ').Append(outerAlias);
        if (!string.IsNullOrEmpty(existingJoins))
            sb.Append(' ').Append(existingJoins);

        foreach (var ex in existsParts)
        {
            var onConditions = ReplaceAlias(ex.InnerWhereRaw, ex.InnerAlias, ex.JoinAlias);
            sb.Append(" INNER JOIN ").Append(ex.InnerTable).Append(' ').Append(ex.JoinAlias);
            sb.Append(" ON ").Append(onConditions);
        }

        if (residualParts.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", residualParts));

        if (!string.IsNullOrWhiteSpace(tailClause))
            sb.Append(' ').Append(tailClause);

        rewritten = sb.ToString();
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FROM clause parsing
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a FROM clause, extracting the first table identifier and any existing JOIN clauses.
    /// Returns <see langword="false"/> only when the FROM source is not a simple identifier.
    /// Existing JOIN clauses are returned verbatim in <paramref name="existingJoins"/> so the
    /// caller can preserve them in the rewritten SQL.
    /// </summary>
    private static bool TryParseFromClause(
        string fromAndBeyond,
        out string outerTable,
        out string outerAlias,
        out string existingJoins,
        out string whereClause,
        out string tailClause)
    {
        outerTable = string.Empty;
        outerAlias = string.Empty;
        existingJoins = string.Empty;
        whereClause = string.Empty;
        tailClause = string.Empty;

        // Match: FROM <table> [AS] [<alias>]
        // The alias group uses a negative lookahead to reject SQL reserved keywords.
        const string ForbiddenAliasLookahead =
            "WHERE|ORDER|GROUP|HAVING|FETCH|LIMIT|OFFSET|JOIN|INNER|LEFT|RIGHT|CROSS|FULL|ON|AS|SELECT|UNION|EXCEPT|INTERSECT|SET|WITH";

        var pattern =
            @"^FROM\s+" +
            @"(?<table>[\w\.\[\]""]+)" +
            @"(?:\s+(?:AS\s+)?(?<alias>(?!(?:" + ForbiddenAliasLookahead + @")\b)\w+))?" +
            @"\s*";

        var match = Regex.Match(fromAndBeyond, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return false;

        outerTable = match.Groups["table"].Value.Trim();
        outerAlias = match.Groups["alias"].Success ? match.Groups["alias"].Value.Trim() : string.Empty;

        var remainder = fromAndBeyond[match.Length..].Trim();

        // Extract any existing JOIN clauses verbatim so they can be preserved in the rewritten SQL.
        if (Regex.IsMatch(remainder, @"^(?:INNER|LEFT|RIGHT|CROSS|FULL)?\s*(?:OUTER\s+)?JOIN\b",
                RegexOptions.IgnoreCase))
        {
            var joinBlockEnd = FindJoinBlockEnd(remainder);
            if (joinBlockEnd >= 0)
            {
                existingJoins = remainder[..joinBlockEnd].TrimEnd();
                remainder = remainder[joinBlockEnd..].Trim();
            }
            else
            {
                existingJoins = remainder.Trim();
                remainder = string.Empty;
            }
        }

        // Split into WHERE clause and trailing ORDER BY / FETCH FIRST / etc.
        var whereKwIdx = SqlSyntaxText.FindTopLevelKeyword(remainder, "WHERE", 0);
        if (whereKwIdx >= 0)
        {
            var afterWhere = remainder[(whereKwIdx + 5)..].Trim();
            var tailIdx = FindTrailingClauseIndex(afterWhere);
            if (tailIdx >= 0)
            {
                whereClause = afterWhere[..tailIdx].Trim();
                tailClause = afterWhere[tailIdx..].Trim();
            }
            else
            {
                whereClause = afterWhere;
                tailClause = string.Empty;
            }
        }
        else
        {
            whereClause = string.Empty;
            var tailIdx = FindTrailingClauseIndex(remainder);
            tailClause = tailIdx >= 0 ? remainder[tailIdx..].Trim() : string.Empty;
        }

        return true;
    }

    private static int FindTrailingClauseIndex(string sql)
    {
        var minIdx = -1;
        foreach (var keyword in TrailingClauseKeywords)
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, keyword, 0);
            if (idx >= 0 && (minIdx < 0 || idx < minIdx))
                minIdx = idx;
        }
        return minIdx;
    }

    /// <summary>
    /// Returns the index of the first top-level keyword that ends a JOIN block
    /// (WHERE, ORDER, GROUP, HAVING, FETCH, LIMIT, OFFSET), or -1 if none found.
    /// </summary>
    private static int FindJoinBlockEnd(string sql)
    {
        var minIdx = -1;
        foreach (var keyword in new[] { "WHERE", "ORDER", "GROUP", "HAVING", "FETCH", "LIMIT", "OFFSET" })
        {
            var idx = SqlSyntaxText.FindTopLevelKeyword(sql, keyword, 0);
            if (idx >= 0 && (minIdx < 0 || idx < minIdx))
                minIdx = idx;
        }
        return minIdx;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Scalar subquery projection parsing
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryParseScalarSubqueryProjection(string part, out ParsedScalarSubquery result)
    {
        result = default!;

        if (part.Length < 2 || part[0] != '(')
            return false;

        var closeIdx = SqlSyntaxText.FindMatchingParen(part, 0);
        if (closeIdx <= 0)
            return false;

        var innerSql = part[1..closeIdx].Trim();
        if (!SqlSyntaxText.StartsWithKeyword(innerSql, "SELECT"))
            return false;

        // Parse optional  ) AS alias  or  ) alias  after the closing paren.
        var afterClose = part[(closeIdx + 1)..].Trim();
        string? projAlias = null;

        if (SqlSyntaxText.StartsWithKeyword(afterClose, "AS"))
        {
            var rest = afterClose[2..].Trim();
            var idMatch = Regex.Match(rest, @"^\w+");
            if (idMatch.Success)
                projAlias = idMatch.Value;
        }
        else if (!string.IsNullOrEmpty(afterClose))
        {
            var idMatch = Regex.Match(afterClose, @"^\w+$");
            if (idMatch.Success)
                projAlias = idMatch.Value;
            else
                return false; // Unexpected suffix — do not rewrite.
        }

        if (!TryParseInnerSelect(innerSql,
                out var innerCol, out var innerTable,
                out var innerAlias, out var innerWhere))
            return false;

        // Handle SELECT TOP N prefix in the inner column expression.
        // TOP 1 without ORDER BY is semantically equivalent to a LEFT JOIN (at most one match).
        // TOP 1 with ORDER BY is order-dependent → do not rewrite.
        // TOP N where N > 1 can return multiple rows → do not rewrite.
        if (Regex.IsMatch(innerCol, @"^TOP\s+\d", RegexOptions.IgnoreCase))
        {
            var topMatch = Regex.Match(innerCol, @"^TOP\s+(\d+)\s+(.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!topMatch.Success)
                return false;

            if (!int.TryParse(topMatch.Groups[1].Value, out var topN) || topN != 1)
                return false;

            // ORDER BY inside the inner query makes the result order-dependent — skip rewrite.
            if (SqlSyntaxText.FindTopLevelKeyword(innerSql, "ORDER", 0) >= 0)
                return false;

            innerCol = topMatch.Groups[2].Value.Trim();
        }

        // Detect aggregate functions: MAX, MIN, COUNT, SUM, AVG.
        AggregateSpec? aggregate = null;
        var aggMatch = Regex.Match(innerCol,
            @"^(MAX|MIN|COUNT|SUM|AVG)\s*\(\s*(\*|[\w\.]+)\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (aggMatch.Success && Enum.TryParse<AggregateKind>(aggMatch.Groups[1].Value, true, out var aggKind))
            aggregate = new AggregateSpec(aggKind, aggMatch.Groups[2].Value.Trim());

        // Require an explicit alias so that ON-clause column references are unambiguous after the JOIN.
        if (string.IsNullOrEmpty(innerAlias))
            return false;

        result = new ParsedScalarSubquery(innerCol, innerTable, innerAlias, innerWhere, projAlias, string.Empty, aggregate);
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // EXISTS expression parsing
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryParseExistsExpression(string part, out ParsedExists result)
    {
        result = default!;
        var trimmed = part.Trim();

        bool negated;
        if (SqlSyntaxText.StartsWithKeyword(trimmed, "NOT EXISTS"))
            negated = true;
        else if (SqlSyntaxText.StartsWithKeyword(trimmed, "EXISTS"))
            negated = false;
        else
            return false;

        var openParenIdx = trimmed.IndexOf('(');
        if (openParenIdx < 0)
            return false;

        var closeParenIdx = SqlSyntaxText.FindMatchingParen(trimmed, openParenIdx);
        if (closeParenIdx < 0)
            return false;

        // Nothing may follow the closing paren of the EXISTS argument.
        if (!string.IsNullOrWhiteSpace(trimmed[(closeParenIdx + 1)..]))
            return false;

        var innerSql = trimmed[(openParenIdx + 1)..closeParenIdx].Trim();
        if (!SqlSyntaxText.StartsWithKeyword(innerSql, "SELECT"))
            return false;

        if (!TryParseInnerSelect(innerSql,
                out _, out var innerTable,
                out var innerAlias, out var innerWhere))
            return false;

        if (string.IsNullOrEmpty(innerAlias))
            return false;

        result = new ParsedExists(innerTable, innerAlias, innerWhere, negated, string.Empty);
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Inner SELECT parsing  (shared by scalar and EXISTS)
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryParseInnerSelect(
        string innerSql,
        out string innerColumn,
        out string innerTable,
        out string innerAlias,
        out string innerWhere)
    {
        innerColumn = string.Empty;
        innerTable = string.Empty;
        innerAlias = string.Empty;
        innerWhere = string.Empty;

        // Find top-level FROM inside the subquery.
        var fromIdx = SqlSyntaxText.FindTopLevelKeyword(innerSql, "FROM", 6);
        if (fromIdx < 0)
            return false;

        innerColumn = innerSql[6..fromIdx].Trim();
        var afterFrom = innerSql[(fromIdx + 4)..].Trim();

        // Split into table clause and WHERE clause.
        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(afterFrom, "WHERE", 0);
        string tableClause;
        if (whereIdx >= 0)
        {
            tableClause = afterFrom[..whereIdx].Trim();
            innerWhere = afterFrom[(whereIdx + 5)..].Trim();
        }
        else
        {
            tableClause = afterFrom.Trim();
            innerWhere = string.Empty;
        }

        // Strip trailing FETCH FIRST / LIMIT from the inner WHERE (defensive).
        var tailIdx = FindTrailingClauseIndex(innerWhere);
        if (tailIdx >= 0)
            innerWhere = innerWhere[..tailIdx].Trim();

        // Parse inner table and optional alias.
        var tableMatch = Regex.Match(tableClause,
            @"^(?<table>[\w\.\[\]""]+)(?:\s+(?:AS\s+)?(?<alias>\w+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!tableMatch.Success)
            return false;

        innerTable = tableMatch.Groups["table"].Value.Trim();
        innerAlias = tableMatch.Groups["alias"].Success
            ? tableMatch.Groups["alias"].Value.Trim()
            : string.Empty;

        if (!string.IsNullOrEmpty(innerAlias) && IsForbiddenAlias(innerAlias))
            innerAlias = string.Empty;

        return !string.IsNullOrEmpty(innerTable);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Utilities
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces all occurrences of <c>oldAlias.</c> with <c>newAlias.</c> in <paramref name="text"/>
    /// using word-boundary matching so that only genuine alias-qualified references are affected.
    /// </summary>
    private static string ReplaceAlias(string text, string oldAlias, string newAlias)
    {
        if (string.IsNullOrEmpty(oldAlias))
            return text;

        return Regex.Replace(text, $@"\b{Regex.Escape(oldAlias)}\.", newAlias + ".",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Splits <paramref name="text"/> on top-level AND keywords, correctly skipping
    /// BETWEEN...AND constructs and AND operators inside nested parentheses or string literals.
    /// </summary>
    private static string[] SplitByTopLevelAnd(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var inString = false;
        var depth = 0;
        var betweenPending = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\'' && (i == 0 || text[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '(') { depth++; continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); continue; }

            if (depth != 0)
                continue;

            if (SqlSyntaxText.MatchesKeywordAt(text, i, "BETWEEN"))
            {
                betweenPending = true;
                continue;
            }

            if (SqlSyntaxText.MatchesKeywordAt(text, i, "AND"))
            {
                if (betweenPending)
                {
                    // This is the AND inside BETWEEN ... AND ...; skip it.
                    betweenPending = false;
                    i += 2; // Loop increment will advance one more, skipping all 3 chars.
                    continue;
                }

                var part = text[start..i].Trim();
                if (!string.IsNullOrEmpty(part))
                    parts.Add(part);

                start = i + 3;
                i = start - 1;
            }
        }

        var last = text[start..].Trim();
        if (!string.IsNullOrEmpty(last))
            parts.Add(last);

        return parts.ToArray();
    }

    private static bool IsForbiddenAlias(string word)
        => Array.Exists(ForbiddenAliasKeywords, k => k.Equals(word, StringComparison.OrdinalIgnoreCase));

    // ──────────────────────────────────────────────────────────────────────────
    // Aggregate JOIN helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the index of the first top-level standalone <c>=</c> operator in <paramref name="text"/>.
    /// Skips <c>!=</c>, <c>&lt;=</c>, <c>&gt;=</c>, and characters inside nested parens or string literals.
    /// Returns -1 if not found.
    /// </summary>
    private static int FindTopLevelEquals(string text)
    {
        var depth = 0;
        var inStr = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'' && (i == 0 || text[i - 1] != '\\')) { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0) continue;
            if (c == '=' && (i == 0 || (text[i - 1] != '!' && text[i - 1] != '<' && text[i - 1] != '>'))
                         && (i + 1 >= text.Length || text[i + 1] != '='))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Splits <paramref name="whereRaw"/> on top-level AND predicates and divides them into
    /// <paramref name="correlationPredicates"/> (those referencing both the inner and an outer
    /// table alias) and <paramref name="residualPredicates"/> (everything else).
    /// </summary>
    private static void TrySplitWherePredicates(
        string whereRaw,
        string innerAlias,
        out string[] correlationPredicates,
        out string[] residualPredicates)
    {
        var parts = SplitByTopLevelAnd(whereRaw);
        var corr = new List<string>();
        var resid = new List<string>();
        foreach (var p in parts)
        {
            var trimmed = p.Trim();
            if (FindTopLevelEquals(trimmed) >= 0 && ReferencesOtherAlias(trimmed, innerAlias))
                corr.Add(trimmed);
            else
                resid.Add(trimmed);
        }
        correlationPredicates = corr.ToArray();
        residualPredicates = resid.ToArray();
    }

    /// <summary>
    /// Returns true when <paramref name="predicate"/> contains a qualified reference to the
    /// inner alias and also a qualified reference to at least one other alias.
    /// </summary>
    private static bool ReferencesOtherAlias(string predicate, string innerAlias)
    {
        bool hasInner = false;
        bool hasOther = false;
        int i = 0;
        while (i < predicate.Length)
        {
            if (predicate[i] == '\'')
            {
                i++;
                while (i < predicate.Length && predicate[i] != '\'') i++;
                if (i < predicate.Length) i++;
                continue;
            }
            if (char.IsLetter(predicate[i]) || predicate[i] == '_')
            {
                int start = i;
                i++;
                while (i < predicate.Length && (char.IsLetterOrDigit(predicate[i]) || predicate[i] == '_')) i++;
                var name = predicate[start..i];
                if (i < predicate.Length && predicate[i] == '.')
                {
                    if (string.Equals(name, innerAlias, StringComparison.OrdinalIgnoreCase))
                        hasInner = true;
                    else
                        hasOther = true;
                    i++; // consume dot
                    continue;
                }
            }
            i++;
        }
        return hasInner && hasOther;
    }

    /// <summary>
    /// Builds the derived-table LEFT JOIN clause for an aggregate scalar subquery and returns
    /// the projection expression (e.g. <c>COALESCE(__j0.cnt, 0) AS OrderCount</c>).
    /// Returns <see langword="false"/> if no correlation predicate can be identified.
    /// </summary>
    private static bool TryBuildAggregateJoinClause(
        ParsedScalarSubquery sq,
        StringBuilder joinSb,
        out string projExpr)
    {
        projExpr = string.Empty;
        if (sq.Aggregate == null) return false;

        TrySplitWherePredicates(sq.InnerWhereRaw, sq.InnerAlias, out var corrPreds, out var residPreds);

        // We need at least one correlation predicate to build the ON clause.
        if (corrPreds.Length == 0) return false;

        var agg = sq.Aggregate;

        // Column alias inside the derived table for the aggregate result.
        var innerAggAlias = agg.Kind == AggregateKind.Count ? "cnt" : "val";
        // Keep the inner alias unchanged inside the derived table.
        var argCol = agg.ArgColumn;

        var aggExpr = $"{agg.Kind.ToString().ToUpperInvariant()}({argCol})";

        // Build inner WHERE (residual predicates only; correlation becomes ON clause).
        var innerWhere = residPreds.Length > 0
            ? " WHERE " + string.Join(" AND ", residPreds)
            : string.Empty;

        // Build the ON clause from correlation predicates, renaming the inner alias.
        var onClause = string.Join(" AND ",
            corrPreds.Select(p => ReplaceAlias(p, sq.InnerAlias, sq.JoinAlias)));

        // Derived table: (SELECT outer_key, AGG(col) AS alias FROM t WHERE ... GROUP BY outer_key)
        // We extract the outer-side column from the first correlation predicate.
        var firstCorr = corrPreds[0];
        var eqIdx = FindTopLevelEquals(firstCorr);
        var lhs = firstCorr[..eqIdx].Trim();
        var rhs = firstCorr[(eqIdx + 1)..].Trim();

        // Determine which side belongs to the inner table.
        var innerKeyExpr = lhs.StartsWith(sq.InnerAlias + ".", StringComparison.OrdinalIgnoreCase) ? lhs
                         : rhs.StartsWith(sq.InnerAlias + ".", StringComparison.OrdinalIgnoreCase) ? rhs
                         : lhs; // fallback

        // Build GROUP BY from all inner-table key columns in correlation predicates.
        var groupByKeys = corrPreds
            .Select(p =>
            {
                var eq = FindTopLevelEquals(p);
                var l = p[..eq].Trim();
                var r = p[(eq + 1)..].Trim();
                return l.StartsWith(sq.InnerAlias + ".", StringComparison.OrdinalIgnoreCase) ? l : r;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupByExpr = string.Join(", ", groupByKeys);

        var innerTableRef = string.IsNullOrEmpty(sq.InnerAlias)
            ? sq.InnerTable
            : $"{sq.InnerTable} {sq.InnerAlias}";

        var derivedTable =
            $"(SELECT {string.Join(", ", groupByKeys)}, {aggExpr} AS {innerAggAlias}" +
            $" FROM {innerTableRef}{innerWhere}" +
            $" GROUP BY {groupByExpr})";

        joinSb.Append(" LEFT JOIN ").Append(derivedTable).Append(' ').Append(sq.JoinAlias);
        joinSb.Append(" ON ").Append(onClause);

        // Projection expression: COUNT uses CASE WHEN to 0 (COALESCE not supported by engine);
        // others stay NULL on no-match.
        var rawProj = agg.Kind == AggregateKind.Count
            ? $"CASE WHEN {sq.JoinAlias}.{innerAggAlias} IS NULL THEN 0 ELSE {sq.JoinAlias}.{innerAggAlias} END"
            : $"{sq.JoinAlias}.{innerAggAlias}";

        projExpr = string.IsNullOrEmpty(sq.ProjectionAlias)
            ? rawProj
            : $"{rawProj} AS {sq.ProjectionAlias}";

        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal data records
    // ──────────────────────────────────────────────────────────────────────────

    private enum AggregateKind { Max, Min, Count, Sum, Avg }

    private sealed record AggregateSpec(AggregateKind Kind, string ArgColumn);

    private sealed record ParsedScalarSubquery(
        string InnerColumn,
        string InnerTable,
        string InnerAlias,
        string InnerWhereRaw,
        string? ProjectionAlias,
        string JoinAlias,
        AggregateSpec? Aggregate = null,
        string OriginalText = "");

    private sealed record ParsedExists(
        string InnerTable,
        string InnerAlias,
        string InnerWhereRaw,
        bool Negated,
        string JoinAlias);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="innerWhere"/> references any
    /// alias that is neither the local outer-table alias (<paramref name="outerAlias"/>)
    /// nor the EXISTS inner alias (<paramref name="innerAlias"/>).  Such a reference
    /// belongs to an enclosing scope and cannot be expressed as a JOIN ON predicate in
    /// the current query; the EXISTS must be kept as a WHERE clause instead.
    /// </summary>
    private static bool ExistsHasOuterScopeAliasReferences(
        string innerWhere, string outerAlias, string innerAlias)
    {
        foreach (Match match in Regex.Matches(innerWhere, @"\b(?<alias>\w+)\s*\."))
        {
            var alias = match.Groups["alias"].Value;
            if (!alias.Equals(outerAlias, StringComparison.OrdinalIgnoreCase)
                && !alias.Equals(innerAlias, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
