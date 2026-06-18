using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using WalhallaSql.Collation;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal static class WhereCompiler
{
    public static Func<object?[], object?[], bool>? Compile(
        SqlWhereExpression? expression,
        SqlTableDefinition table,
        int parameterCount,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery = null)
    {
        if (expression == null)
            return null;

        var rowParam = Expression.Parameter(typeof(object?[]), "row");
        var paramsParam = Expression.Parameter(typeof(object?[]), "params");

        var body = BuildExpression(expression, table, rowParam, paramsParam, resolveSubquery);
        if (body == null)
            return null;

        // Guard: the lambda return type is bool, but some branches (e.g. bool? columns)
        // may produce Nullable<bool> or object. Coerce to bool safely.
        if (body.Type != typeof(bool))
        {
            if (body.Type == typeof(bool?))
            {
                body = Expression.Equal(body, Expression.Constant(true, typeof(bool?)));
            }
            else
            {
                // Runtime-safe conversion via Convert.ToBoolean(object) so that
                // boxed bool? / int / etc. do not throw InvalidCastException.
                var asObject = body.Type == typeof(object) ? body : Expression.Convert(body, typeof(object));
                var converted = Expression.Call(
                    typeof(Convert).GetMethod(nameof(Convert.ToBoolean), new[] { typeof(object) })!,
                    asObject);
                body = Expression.Equal(converted, Expression.Constant(true));
            }
        }

        var lambda = Expression.Lambda<Func<object?[], object?[], bool>>(body, rowParam, paramsParam);
        return lambda.Compile();
    }

    public static Func<object?[], object?>? CompileValue(
        SqlWhereValueExpression value,
        SqlTableDefinition table)
    {
        var rowParam = Expression.Parameter(typeof(object?[]), "row");
        var nullParams = Expression.Parameter(typeof(object?[]), "nullParams");
        var body = BuildValue(value, table, rowParam, nullParams, null);
        if (body == null)
            return null;
        var lambda = Expression.Lambda<Func<object?[], object?>>(body, rowParam);
        return lambda.Compile();
    }

    private static Expression? BuildExpression(
        SqlWhereExpression expr,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        switch (expr)
        {
            case SqlWhereAndExpression and:
            {
                Expression? acc = null;
                foreach (var child in and.Children)
                {
                    var childExpr = BuildExpression(child, table, rowParam, paramsParam, resolveSubquery);
                    if (childExpr == null) return null;
                    acc = acc == null ? childExpr : Expression.AndAlso(acc, childExpr);
                }
                return acc ?? Expression.Constant(true);
            }

            case SqlWhereOrExpression or:
            {
                Expression? acc = null;
                foreach (var child in or.Children)
                {
                    var childExpr = BuildExpression(child, table, rowParam, paramsParam, resolveSubquery);
                    if (childExpr == null) return null;
                    acc = acc == null ? childExpr : Expression.OrElse(acc, childExpr);
                }
                return acc ?? Expression.Constant(true);
            }

            case SqlWhereNotExpression not:
            {
                var inner = BuildExpression(not.Inner, table, rowParam, paramsParam, resolveSubquery);
                return inner == null ? null : Expression.Not(inner);
            }

            case SqlWhereComparisonExpression cmp:
            {
                var left = BuildValue(cmp.Left, table, rowParam, paramsParam, resolveSubquery);
                var right = BuildValue(cmp.Right, table, rowParam, paramsParam, resolveSubquery);
                if (left == null || right == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(CompareValues),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    left, right, Expression.Constant(cmp.Operator),
                    Expression.Constant(null, typeof(CompareInfo)));
            }

            case SqlWhereBetweenExpression between:
            {
                var value = BuildValue(between.Value, table, rowParam, paramsParam, resolveSubquery);
                var lower = BuildValue(between.Lower, table, rowParam, paramsParam, resolveSubquery);
                var upper = BuildValue(between.Upper, table, rowParam, paramsParam, resolveSubquery);
                if (value == null || lower == null || upper == null) return null;

                var geExpr = Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(CompareValues),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    value, lower, Expression.Constant(SqlWhereComparisonOperator.GreaterThanOrEqual),
                    Expression.Constant(null, typeof(CompareInfo)));

                var leExpr = Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(CompareValues),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    value, upper, Expression.Constant(SqlWhereComparisonOperator.LessThanOrEqual),
                    Expression.Constant(null, typeof(CompareInfo)));

                var andExpr = Expression.AndAlso(geExpr, leExpr);
                return between.Negated ? Expression.Not(andExpr) : andExpr;
            }

            case SqlWhereNullCheckExpression nullCheck:
            {
                var value = BuildValue(nullCheck.Value, table, rowParam, paramsParam, resolveSubquery);
                if (value == null) return null;
                var nullConst = Expression.Constant(null, typeof(object));
                return nullCheck.Negated
                    ? Expression.NotEqual(value, nullConst)
                    : Expression.Equal(value, nullConst);
            }

            case SqlWhereDistinctFromExpression distinct:
            {
                var left = BuildValue(distinct.Left, table, rowParam, paramsParam, resolveSubquery);
                var right = BuildValue(distinct.Right, table, rowParam, paramsParam, resolveSubquery);
                if (left == null || right == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(IsDistinctFrom),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    left, right, Expression.Constant(distinct.Negated));
            }

            case SqlWhereTruthyExpression truthy:
            {
                var value = BuildValue(truthy.Value, table, rowParam, paramsParam, resolveSubquery);
                if (value == null) return null;
                if (value.Type == typeof(bool))
                    return value;

                // Safe runtime conversion for bool?, object, int, etc.
                var asObject = value.Type == typeof(object) ? value : Expression.Convert(value, typeof(object));
                var converted = Expression.Call(
                    typeof(Convert).GetMethod(nameof(Convert.ToBoolean), new[] { typeof(object) })!,
                    asObject);
                return Expression.Equal(converted, Expression.Constant(true));
            }

            case SqlWhereInListExpression inList:
            {
                var left = BuildValue(inList.Left, table, rowParam, paramsParam, resolveSubquery);
                if (left == null) return null;

                // Only support literal-only lists
                var values = new HashSet<object?>(ListValueComparer.Instance);
                foreach (var v in inList.Values)
                {
                    if (v is not SqlWhereLiteralExpression lit)
                        return null;
                    values.Add(lit.Value);
                }

                var hashSetExpr = Expression.Constant(values);
                var containsMethod = typeof(HashSet<object>).GetMethod("Contains", new[] { typeof(object) })!;
                var containsExpr = Expression.Call(hashSetExpr, containsMethod, left);

                if (inList.Negated)
                    return Expression.Not(containsExpr);

                // For non-negated: must also guard against null LHS
                var notNullExpr = Expression.NotEqual(left, Expression.Constant(null, typeof(object)));
                return Expression.AndAlso(notNullExpr, containsExpr);
            }

            case SqlWhereInSubqueryExpression inSub:
                return BuildInSubqueryExpression(inSub, table, rowParam, paramsParam, resolveSubquery);

            case SqlWhereExistsExpression exists:
                return BuildExistsExpression(exists, resolveSubquery);

            case SqlWhereLikeExpression like:
                return BuildLikeExpression(like, table, rowParam, paramsParam);

            case SqlWhereQuantifiedComparisonExpression quantified:
                return BuildQuantifiedComparisonExpression(quantified, table, rowParam, paramsParam, resolveSubquery);

            case SqlWhereJsonContainsExpression contains:
            {
                var left = BuildValue(contains.Left, table, rowParam, paramsParam, resolveSubquery);
                var right = BuildValue(contains.Right, table, rowParam, paramsParam, resolveSubquery);
                if (left == null || right == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(JsonContains),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    left, right, Expression.Constant(contains.Operator));
            }

            case SqlWhereJsonKeyExistsExpression keyExists:
            {
                var left = BuildValue(keyExists.Left, table, rowParam, paramsParam, resolveSubquery);
                var right = BuildValue(keyExists.Right, table, rowParam, paramsParam, resolveSubquery);
                if (left == null || right == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(JsonKeyExists),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    left, right, Expression.Constant(keyExists.Operator));
            }

            default:
                return null; // CASE expressions → not compiled yet
        }
    }

    private static Expression? BuildValue(
        SqlWhereValueExpression value,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        switch (value)
        {
            case SqlWhereColumnExpression col:
            {
                var idx = FindColumnIndex(table, col.FullName);
                if (idx < 0)
                    idx = FindColumnIndex(table, col.SimpleName);
                if (idx >= 0)
                    return Expression.ArrayIndex(rowParam, Expression.Constant(idx));

                // Check JSON projections (virtual columns like json__Profile__Zip)
                var projectionEvaluator = TryBuildProjectionEvaluator(table, col.SimpleName);
                if (projectionEvaluator != null)
                {
                    // Wrap the delegate in a constant expression and invoke it
                    var evaluatorExpr = Expression.Constant(projectionEvaluator);
                    return Expression.Invoke(evaluatorExpr, rowParam);
                }

                return null;
            }

            case SqlWhereLiteralExpression lit:
                return Expression.Constant(lit.Value, typeof(object));

            case SqlWhereParameterExpression param:
                return Expression.ArrayIndex(paramsParam, Expression.Constant(param.Index));

            case SqlWhereScalarSubqueryValueExpression scalarSub:
            {
                if (resolveSubquery == null) return null;
                // Resolve uncorrelated subquery once at compile time.
                var rows = resolveSubquery(scalarSub.SubquerySql);
                var scalarValue = rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
                return Expression.Constant(scalarValue, typeof(object));
            }

            case SqlWhereUnaryValueExpression unary:
            {
                var operand = BuildValue(unary.Operand, table, rowParam, paramsParam, resolveSubquery);
                if (operand == null) return null;
                if (unary.Operator == SqlWhereUnaryOperator.Minus)
                {
                    return Expression.Call(
                        typeof(WhereCompiler).GetMethod(nameof(NegateValue),
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                        operand);
                }
                return operand;
            }

            case SqlWhereBinaryValueExpression binary:
            {
                var left = BuildValue(binary.Left, table, rowParam, paramsParam, resolveSubquery);
                var right = BuildValue(binary.Right, table, rowParam, paramsParam, resolveSubquery);
                if (left == null || right == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(ApplyBinaryOp),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    left, right, Expression.Constant(binary.Operator));
            }

            case SqlWhereCastExpression cast:
            {
                var inner = BuildValue(cast.Inner, table, rowParam, paramsParam, resolveSubquery);
                if (inner == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(ConvertCast),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    inner, Expression.Constant(cast.TargetType));
            }

            case SqlWhereCaseExpression caseExpr:
                return BuildCaseExpression(caseExpr.ExpressionText, table, rowParam, paramsParam, resolveSubquery);

            case SqlWhereFunctionCallExpression funcCall:
                return BuildFunctionCall(funcCall, table, rowParam, paramsParam, resolveSubquery);

            case SqlWhereJsonArrowExpression arrow:
            {
                var src = BuildValue(arrow.Source, table, rowParam, paramsParam, resolveSubquery);
                if (src == null) return null;
                return Expression.Call(
                    typeof(WhereCompiler).GetMethod(nameof(JsonArrowValue),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
                    Expression.Convert(src, typeof(object)),
                    Expression.Constant(arrow.JsonPath),
                    Expression.Constant(arrow.Unquote),
                    Expression.Constant(arrow.PathKind == SqlJsonPathKind.Path));
            }

            default:
                return null;
        }
    }

    // ── Subquery expression compilation ──────────────────────────────────────

    private static Expression? BuildInSubqueryExpression(
        SqlWhereInSubqueryExpression inSub,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        if (resolveSubquery == null) return null;

        var left = BuildValue(inSub.Left, table, rowParam, paramsParam, resolveSubquery);
        if (left == null) return null;

        // Resolve uncorrelated subquery once at compile time.
        var rows = resolveSubquery(inSub.SubquerySql);
        var hashSet = BuildSubqueryHashSet(rows);
        var hashSetConst = Expression.Constant(hashSet);

        var containsMethod = typeof(HashSet<object>).GetMethod("Contains", new[] { typeof(object) })!;
        var containsExpr = Expression.Call(hashSetConst, containsMethod, left);

        if (inSub.Negated)
            return Expression.Not(containsExpr);

        var notNullExpr = Expression.NotEqual(left, Expression.Constant(null, typeof(object)));
        return Expression.AndAlso(notNullExpr, containsExpr);
    }

    private static Expression? BuildExistsExpression(
        SqlWhereExistsExpression exists,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        if (resolveSubquery == null) return null;

        // Resolve uncorrelated subquery once at compile time.
        var rows = resolveSubquery(exists.SubquerySql);
        var result = rows.Count > 0;
        if (exists.Negated) result = !result;
        return Expression.Constant(result, typeof(bool));
    }

    private static Expression? BuildQuantifiedComparisonExpression(
        SqlWhereQuantifiedComparisonExpression quantified,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        if (resolveSubquery == null) return null;

        var left = BuildValue(quantified.Left, table, rowParam, paramsParam, resolveSubquery);
        if (left == null) return null;

        // Resolve uncorrelated subquery once at compile time.
        var rows = resolveSubquery(quantified.SubquerySql);
        var rowsConst = Expression.Constant(rows);

        return Expression.Call(
            typeof(WhereCompiler).GetMethod(nameof(CheckQuantifiedComparison),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!,
            left, rowsConst,
            Expression.Constant(quantified.Operator),
            Expression.Constant(quantified.Quantifier));
    }

    private static Expression? BuildLikeExpression(
        SqlWhereLikeExpression like,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam)
    {
        // Only support column LIKE literal patterns.
        if (like.Left is not SqlWhereColumnExpression colExpr)
            return null;

        var colIdx = FindColumnIndex(table, colExpr.SimpleName);
        if (colIdx < 0) return null;

        string? patternText = null;
        if (like.Pattern is SqlWhereLiteralExpression lit)
            patternText = lit.Value?.ToString();
        else if (like.Pattern is SqlWhereParameterExpression)
            return null; // Parameterized LIKE → not compiled for now

        if (patternText == null) return null;

        // Detect prefix-only pattern: 'prefix%' with no other wildcards.
        var hasLeadingPercent = patternText.StartsWith('%');
        var hasTrailingPercent = patternText.EndsWith('%');
        var wildcardCount = 0;
        foreach (var ch in patternText) { if (ch == '%') wildcardCount++; }

        // Only optimize prefix-only LIKE (trailing % only, exactly one % at end).
        bool isPrefixOnly = !hasLeadingPercent && hasTrailingPercent && wildcardCount == 1;

        if (!isPrefixOnly)
            return null; // Fall back: not compiled

        var prefix = patternText.TrimEnd('%');
        var cellExpr = Expression.ArrayIndex(rowParam, Expression.Constant(colIdx));

        // Convert RawStringRef or plain string to string, then call StartsWith
        var extractStringMethod = typeof(WhereCompiler).GetMethod(nameof(ExtractString),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stringExpr = Expression.Call(extractStringMethod, cellExpr);
        var nullCheck = Expression.NotEqual(stringExpr, Expression.Constant(null, typeof(string)));
        var startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string), typeof(StringComparison) })!;
        Expression callExpr = Expression.Call(
            stringExpr,
            startsWithMethod,
            Expression.Constant(prefix),
            Expression.Constant(StringComparison.OrdinalIgnoreCase));
        callExpr = Expression.AndAlso(nullCheck, callExpr);

        return like.Negated ? Expression.Not(callExpr) : callExpr;
    }

    private static Expression BuildCaseExpression(
        string caseText,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        var parts = ParseCaseParts(caseText);

        // Build from last to first: ELSE (or null default), then chain WHEN/THEN pairs
        Expression result;
        if (parts.Else != null)
        {
            var elseValueExpr = SqlWhereParser.ParseValueExpression(parts.Else);
            result = BuildValue(elseValueExpr, table, rowParam, paramsParam, resolveSubquery)
                     ?? Expression.Constant(null, typeof(object));
        }
        else
        {
            result = Expression.Constant(null, typeof(object));
        }

        for (int i = parts.WhenThenPairs.Count - 1; i >= 0; i--)
        {
            var (whenText, thenText) = parts.WhenThenPairs[i];
            var condition = BuildExpression(
                SqlWhereParser.Parse(whenText), table, rowParam, paramsParam, resolveSubquery);
            if (condition == null)
                return Expression.Constant(null, typeof(object));

            var thenValue = BuildValue(
                SqlWhereParser.ParseValueExpression(thenText), table, rowParam, paramsParam, resolveSubquery);
            if (thenValue == null)
                thenValue = Expression.Constant(null, typeof(object));

            result = Expression.Condition(condition, thenValue, result);
        }

        return result;
    }

    private sealed record CaseParts(
        List<(string When, string Then)> WhenThenPairs,
        string? Else);

    private static CaseParts ParseCaseParts(string caseText)
    {
        // Strip leading CASE and trailing END
        var text = caseText.AsSpan().Trim();
        if (text.StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
            text = text.Slice(4).TrimStart();
        if (text.EndsWith("END", StringComparison.OrdinalIgnoreCase))
            text = text.Slice(0, text.Length - 3).TrimEnd();

        var s = text.ToString();
        var pairs = new List<(string When, string Then)>();
        string? elsePart = null;

        var pos = 0;
        while (pos < s.Length)
        {
            // Skip whitespace
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            if (pos >= s.Length) break;

            if (StartsWithKeywordAt(s, pos, "WHEN"))
            {
                pos += 4;
                var whenStart = SkipWhitespace(s, pos);
                var whenEnd = FindTopLevelKeyword(s, whenStart, "THEN");
                if (whenEnd < 0) break;
                var whenText = s[whenStart..whenEnd].Trim();

                pos = whenEnd + 4;
                var thenStart = SkipWhitespace(s, pos);
                var thenEnd = FindTopLevelKeyword(s, thenStart, "WHEN", "ELSE", "END");
                var thenText = thenEnd < 0
                    ? s[thenStart..].Trim()
                    : s[thenStart..thenEnd].Trim();

                pairs.Add((whenText, thenText));
                pos = thenEnd >= 0 ? thenEnd : s.Length;
            }
            else if (StartsWithKeywordAt(s, pos, "ELSE"))
            {
                pos += 4;
                var elseStart = SkipWhitespace(s, pos);
                var elseEnd = FindTopLevelKeyword(s, elseStart, "END");
                elsePart = elseEnd < 0
                    ? s[elseStart..].Trim()
                    : s[elseStart..elseEnd].Trim();
                break;
            }
            else
            {
                pos++;
            }
        }

        return new CaseParts(pairs, elsePart);
    }

    private static bool StartsWithKeywordAt(string s, int pos, string keyword)
    {
        if (pos + keyword.Length > s.Length) return false;
        if (string.Compare(s, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        // Must be followed by whitespace or end
        var end = pos + keyword.Length;
        return end >= s.Length || !char.IsLetterOrDigit(s[end]);
    }

    private static int SkipWhitespace(string s, int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        return pos;
    }

    private static int FindTopLevelKeyword(string s, int start, params string[] keywords)
    {
        var depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '(') { depth++; continue; }
            if (s[i] == ')') { depth--; continue; }
            if (s[i] == '\'') { i++; while (i < s.Length && s[i] != '\'') i++; continue; }

            if (depth == 0)
            {
                foreach (var kw in keywords)
                {
                    if (StartsWithKeywordAt(s, i, kw))
                        return i;
                }
            }
        }
        return -1;
    }

    // ── Runtime helpers ────────────────────────────────────────────────────────

    private static HashSet<object?> BuildSubqueryHashSet(IReadOnlyList<object?[]> rows)
    {
        var set = new HashSet<object?>(ListValueComparer.Instance);
        foreach (var row in rows)
            set.Add(row.Length > 0 ? row[0] : null);
        return set;
    }

    private static bool CheckExists(IReadOnlyList<object?[]> rows)
        => rows.Count > 0;

    private static object? GetScalarSubqueryValue(IReadOnlyList<object?[]> rows)
        => rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;

    private static bool CheckQuantifiedComparison(
        object? outerValue,
        IReadOnlyList<object?[]> rows,
        SqlWhereComparisonOperator op,
        SqlWhereQuantifier quantifier)
    {
        if (rows.Count == 0)
            return quantifier == SqlWhereQuantifier.All; // ALL over empty set → true

        if (quantifier == SqlWhereQuantifier.All)
        {
            foreach (var row in rows)
            {
                var innerValue = row.Length > 0 ? row[0] : null;
                if (!CompareValues(outerValue, innerValue, op))
                    return false;
            }
            return true;
        }
        else // Any or Some
        {
            foreach (var row in rows)
            {
                var innerValue = row.Length > 0 ? row[0] : null;
                if (CompareValues(outerValue, innerValue, op))
                    return true;
            }
            return false;
        }
    }

    private static int FindColumnIndex(SqlTableDefinition table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Versucht, einen Evaluator für eine JSON-Projektionsspalte zu bauen
    /// (z. B. json__Profile__Zip → extrahiere Zip aus der JSON-Spalte Profile).
    /// </summary>
    private static Func<object?[], object?>? TryBuildProjectionEvaluator(SqlTableDefinition table, string sourceName)
    {
        if (table.Projections == null) return null;

        foreach (var proj in table.Projections)
        {
            if (!string.Equals(proj.ProjectionName, sourceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceColIdx = FindColumnIndex(table, proj.SourceColumnName);
            if (sourceColIdx < 0) return null;

            var pathSegments = proj.PathSegments;
            var resultType = proj.ResultType;

            return row =>
            {
                var jsonValue = row[sourceColIdx];
                if (jsonValue == null || jsonValue == DBNull.Value) return null;

                var jsonString = jsonValue.ToString()!;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                    var element = doc.RootElement;

                    foreach (var segment in pathSegments)
                    {
                        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (!element.TryGetProperty(segment, out element))
                                return null;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    return ConvertJsonElement(element, resultType);
                }
                catch
                {
                    return null;
                }
            };
        }

        return null;
    }

    private static object? ConvertJsonElement(System.Text.Json.JsonElement element, SqlScalarType targetType)
    {
        return targetType switch
        {
            SqlScalarType.String => element.GetString(),
            SqlScalarType.Int32 => element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetInt32() : null,
            SqlScalarType.Int64 => element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetInt64() : null,
            SqlScalarType.Double => element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetDouble() : null,
            SqlScalarType.Decimal => element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetDecimal() : null,
            SqlScalarType.Boolean => element.ValueKind == System.Text.Json.JsonValueKind.True || element.ValueKind == System.Text.Json.JsonValueKind.False ? element.GetBoolean() : null,
            SqlScalarType.DateTime => element.ValueKind == System.Text.Json.JsonValueKind.String ? DateTime.Parse(element.GetString()!) : null,
            SqlScalarType.Guid => element.ValueKind == System.Text.Json.JsonValueKind.String ? Guid.Parse(element.GetString()!) : null,
            _ => element.GetString() ?? element.GetRawText()
        };
    }

    // ── Runtime helpers ────────────────────────────────────────────────────────

    private static object NegateValue(object? value)
    {
        if (value == null) return DBNull.Value;
        return value switch
        {
            int i => -i,
            long l => -l,
            double d => -d,
            float f => -f,
            decimal m => -m,
            _ => throw new NotSupportedException($"Cannot negate type {value.GetType().Name}.")
        };
    }

    private static string? ExtractString(object? value)
    {
        if (value is RawStringRef rsr) return rsr.ToString()!;
        return value as string;
    }

    private static object? ConvertCast(object? value, SqlScalarType targetType)
    {
        if (value == null || value == DBNull.Value) return null;
        if (value is RawStringRef rsr) value = rsr.ToString();
        if (value is string s && s.Length == 0) return null;

        return targetType switch
        {
            SqlScalarType.Int32 => unchecked((int)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            SqlScalarType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            SqlScalarType.Int16 => unchecked((short)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            SqlScalarType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            SqlScalarType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            SqlScalarType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
            SqlScalarType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            SqlScalarType.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            SqlScalarType.Date => Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date,
            SqlScalarType.Time => value is TimeSpan ts ? ts : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture),
            SqlScalarType.Guid => value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
            SqlScalarType.Binary => value is byte[] b ? b : throw new NotSupportedException($"Cannot cast to Binary from {value.GetType().Name}."),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static object ApplyBinaryOp(object? left, object? right, SqlWhereBinaryOperator op)
    {
        if (left == null || right == null) return DBNull.Value;
        try
        {
            // Promote both to double for arithmetic
            var lv = Convert.ToDouble(left);
            var rv = Convert.ToDouble(right);
            var result = op switch
            {
                SqlWhereBinaryOperator.Add => lv + rv,
                SqlWhereBinaryOperator.Subtract => lv - rv,
                SqlWhereBinaryOperator.Multiply => lv * rv,
                SqlWhereBinaryOperator.Divide => lv / rv,
                SqlWhereBinaryOperator.Modulo => lv % rv,
                _ => throw new NotSupportedException($"Unknown binary operator {op}.")
            };
            return result;
        }
        catch
        {
            return DBNull.Value;
        }
    }

    private static bool IsDistinctFrom(object? left, object? right, bool negated)
    {
        if (left == null && right == null) return negated;
        if (left == null || right == null) return !negated;
        var equal = CompareValues(left, right, SqlWhereComparisonOperator.Equal);
        return negated ? equal : !equal;
    }

    internal static bool CompareValues(object? left, object? right, SqlWhereComparisonOperator op,
        CompareInfo? collation = null)
    {
        // Null handling: null == null, null sorts before non-null
        if (left == null && right == null)
            return op == SqlWhereComparisonOperator.Equal || op == SqlWhereComparisonOperator.GreaterThanOrEqual || op == SqlWhereComparisonOperator.LessThanOrEqual;

        if (left == null)
            return op == SqlWhereComparisonOperator.LessThan || op == SqlWhereComparisonOperator.LessThanOrEqual || op == SqlWhereComparisonOperator.NotEqual;

        if (right == null)
            return op == SqlWhereComparisonOperator.GreaterThan || op == SqlWhereComparisonOperator.GreaterThanOrEqual || op == SqlWhereComparisonOperator.NotEqual;

        // Fast paths for same types
        if (left is long l && right is long r) return CompareInt64Result(l.CompareTo(r), op);
        if (left is int li && right is int ri) return CompareInt32Result(li.CompareTo(ri), op);
        if (left is short ls && right is short rs) return CompareInt32Result(ls.CompareTo(rs), op);
        if (left is uint lui && right is uint rui) return CompareInt64Result(((long)lui).CompareTo((long)rui), op);
        if (left is ulong lul && right is ulong rul) return CompareResult(lul.CompareTo(rul), op);
        if (left is string ls2 && right is string rs2) return CompareResult(CollationManager.Compare(ls2, rs2, collation), op);
        if (left is RawStringRef lrs && right is string rrs2) return CompareResult(lrs.CompareTo(rrs2, collation), op);
        if (left is string ls3 && right is RawStringRef rrs) return CompareResult(-rrs.CompareTo(ls3, collation), op);
        if (left is double ld && right is double rd) return CompareDoubleResult(ld.CompareTo(rd), op);

        // Cross-type: long vs double
        if (left is long lx && right is double rx) return CompareDoubleResult(((double)lx).CompareTo(rx), op);
        if (left is double dl && right is long rl) return CompareDoubleResult(dl.CompareTo((double)rl), op);

        // Int promotion
        if (left is int il && right is long rl2) return CompareInt64Result(((long)il).CompareTo(rl2), op);
        if (left is long ll && right is int ir2) return CompareInt64Result(ll.CompareTo((long)ir2), op);
        if (left is int i3 && right is short rs3) return CompareInt32Result(i3.CompareTo((int)rs3), op);
        if (left is short ls4 && right is int ri4) return CompareInt32Result(((int)ls4).CompareTo(ri4), op);

        // TimeSpan
        if (left is TimeSpan ts1 && right is TimeSpan ts2) return CompareInt64Result(ts1.Ticks.CompareTo(ts2.Ticks), op);

        // Binary
        if (left is byte[] lb && right is byte[] rb)
            return CompareResult(CompareBytes(lb, rb), op);

        // Boolean
        if (left is bool bl && right is bool br) return CompareBoolResult(bl.CompareTo(br), op);

        // DateTime — normalize to UTC
        if (left is DateTime ldt && right is DateTime rdt)
            return CompareInt64Result(ldt.ToUniversalTime().Ticks.CompareTo(rdt.ToUniversalTime().Ticks), op);

        // DateTime vs string: try to parse the string as DateTime
        if (left is DateTime ldt2 && right is string rStr && DateTime.TryParse(rStr, out var rDt))
            return CompareInt64Result(ldt2.ToUniversalTime().Ticks.CompareTo(rDt.ToUniversalTime().Ticks), op);
        if (left is string lStr && right is DateTime rdt2 && DateTime.TryParse(lStr, out var lDt))
            return CompareInt64Result(lDt.ToUniversalTime().Ticks.CompareTo(rdt2.ToUniversalTime().Ticks), op);

        // Numeric fallback
        try
        {
            var dLeft = Convert.ToDouble(left);
            var dRight = Convert.ToDouble(right);
            return CompareDoubleResult(dLeft.CompareTo(dRight), op);
        }
        catch { }

        // String fallback
        var sLeft = Convert.ToString(left) ?? string.Empty;
        var sRight = Convert.ToString(right) ?? string.Empty;
        return CompareResult(CollationManager.Compare(sLeft, sRight, collation), op);
    }

    private static bool CompareInt32Result(int cmp, SqlWhereComparisonOperator op)
        => CompareInt64Result(cmp, op);

    private static bool CompareInt64Result(int cmp, SqlWhereComparisonOperator op)
    {
        return op switch
        {
            SqlWhereComparisonOperator.Equal => cmp == 0,
            SqlWhereComparisonOperator.NotEqual => cmp != 0,
            SqlWhereComparisonOperator.GreaterThan => cmp > 0,
            SqlWhereComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            SqlWhereComparisonOperator.LessThan => cmp < 0,
            SqlWhereComparisonOperator.LessThanOrEqual => cmp <= 0,
            _ => false
        };
    }

    private static bool CompareDoubleResult(int cmp, SqlWhereComparisonOperator op)
    {
        if (cmp == 0) return op is SqlWhereComparisonOperator.Equal or SqlWhereComparisonOperator.GreaterThanOrEqual or SqlWhereComparisonOperator.LessThanOrEqual;

        // Floating-point: treat values within epsilon as equal for EQ/NE
        return op switch
        {
            SqlWhereComparisonOperator.Equal => false,
            SqlWhereComparisonOperator.NotEqual => true,
            SqlWhereComparisonOperator.GreaterThan => cmp > 0,
            SqlWhereComparisonOperator.GreaterThanOrEqual => cmp > 0,
            SqlWhereComparisonOperator.LessThan => cmp < 0,
            SqlWhereComparisonOperator.LessThanOrEqual => cmp < 0,
            _ => false
        };
    }

    private static bool CompareBoolResult(int cmp, SqlWhereComparisonOperator op)
    {
        return op switch
        {
            SqlWhereComparisonOperator.Equal => cmp == 0,
            SqlWhereComparisonOperator.NotEqual => cmp != 0,
            SqlWhereComparisonOperator.GreaterThan => cmp > 0,
            SqlWhereComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            SqlWhereComparisonOperator.LessThan => cmp < 0,
            SqlWhereComparisonOperator.LessThanOrEqual => cmp <= 0,
            _ => false
        };
    }

    private static bool CompareResult(int cmp, SqlWhereComparisonOperator op)
    {
        return op switch
        {
            SqlWhereComparisonOperator.Equal => cmp == 0,
            SqlWhereComparisonOperator.NotEqual => cmp != 0,
            SqlWhereComparisonOperator.GreaterThan => cmp > 0,
            SqlWhereComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            SqlWhereComparisonOperator.LessThan => cmp < 0,
            SqlWhereComparisonOperator.LessThanOrEqual => cmp <= 0,
            _ => false
        };
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        for (var i = 0; i < len; i++)
        {
            if (left[i] != right[i])
                return left[i].CompareTo(right[i]);
        }
        return left.Length.CompareTo(right.Length);
    }

    // ── Scalar function compilation ─────────────────────────────────────────

    private static Expression? BuildFunctionCall(
        SqlWhereFunctionCallExpression func,
        SqlTableDefinition table,
        ParameterExpression rowParam,
        ParameterExpression paramsParam,
        Func<string, IReadOnlyList<object?[]>>? resolveSubquery)
    {
        var args = new Expression[func.Arguments.Count];
        for (int i = 0; i < func.Arguments.Count; i++)
        {
            var arg = BuildValue(func.Arguments[i], table, rowParam, paramsParam, resolveSubquery);
            if (arg == null) return null;
            args[i] = arg;
        }

        var methodName = func.FunctionName switch
        {
            "CONCAT" => nameof(ScalarConcat),
            "SUBSTRING" or "SUBSTR" => nameof(ScalarSubstring),
            "UPPER" => nameof(ScalarUpper),
            "LOWER" => nameof(ScalarLower),
            "TRIM" => nameof(ScalarTrim),
            "LENGTH" => nameof(ScalarLength),
            "ABS" => nameof(ScalarAbs),
            "ROUND" => nameof(ScalarRound),
            "CEILING" or "CEIL" => nameof(ScalarCeiling),
            "FLOOR" => nameof(ScalarFloor),
            "POWER" => nameof(ScalarPower),
            "NOW" or "CURRENT_TIMESTAMP" => nameof(ScalarNow),
            "DATEADD" => nameof(ScalarDateAdd),
            "DATEDIFF" => nameof(ScalarDateDiff),
            "REPLACE" => nameof(ScalarReplace),
            "CHARINDEX" or "POSITION" => nameof(ScalarCharIndex),
            "COALESCE" => nameof(ScalarCoalesce),
            "NULLIF" => nameof(ScalarNullIf),
            "IIF" => nameof(ScalarIIf),
            "JSON_EXTRACT" => nameof(JsonExtractValue),
            "JSON_VALUE" => nameof(JsonValue),
            "JSONB_BUILD_OBJECT" => nameof(JsonbBuildObject),
            "JSONB_BUILD_ARRAY" => nameof(JsonbBuildArray),
            "JSONB_STRIP_NULLS" => nameof(JsonbStripNulls),
            "JSONB_SET" => nameof(JsonbSet),
            "JSONB_INSERT" => nameof(JsonbInsert),
            "JSONB_PATH_EXISTS" => nameof(JsonbPathExists),
            "JSONB_PATH_QUERY" => nameof(JsonbPathQuery),
            _ => null
        };

        if (methodName == null) return null;

        var method = typeof(WhereCompiler).GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return Expression.Call(method, Expression.NewArrayInit(typeof(object), args));
    }

    private static object? ScalarConcat(object?[] args)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in args)
        {
            if (a != null)
                sb.Append(Convert.ToString(a));
        }
        return sb.ToString();
    }

    private static object? ScalarSubstring(object?[] args)
    {
        if (args.Length < 2 || args[0] == null) return null;
        var str = Convert.ToString(args[0])!;
        var start = Convert.ToInt32(args[1]);
        if (args.Length >= 3 && args[2] != null)
        {
            var length = Convert.ToInt32(args[2]);
            if (start < 1) start = 1;
            if (start > str.Length) return string.Empty;
            return str.Substring(start - 1, Math.Min(length, str.Length - (start - 1)));
        }
        if (start < 1) start = 1;
        if (start > str.Length) return string.Empty;
        return str.Substring(start - 1);
    }

    private static object? ScalarUpper(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        return Convert.ToString(args[0])!.ToUpperInvariant();
    }

    private static object? ScalarLower(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        return Convert.ToString(args[0])!.ToLowerInvariant();
    }

    private static object? ScalarTrim(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        return Convert.ToString(args[0])!.Trim();
    }

    private static object? ScalarLength(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return 0L;
        return (long)Convert.ToString(args[0])!.Length;
    }

    private static object? ScalarAbs(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var v = Convert.ToDouble(args[0], CultureInfo.InvariantCulture);
        return Math.Abs(v);
    }

    private static object? ScalarRound(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var v = Convert.ToDouble(args[0], CultureInfo.InvariantCulture);
        var decimals = args.Length >= 2 && args[1] != null ? Convert.ToInt32(args[1], CultureInfo.InvariantCulture) : 0;
        return Math.Round(v, decimals);
    }

    private static object? ScalarCeiling(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        return Math.Ceiling(Convert.ToDouble(args[0], CultureInfo.InvariantCulture));
    }

    private static object? ScalarFloor(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        return Math.Floor(Convert.ToDouble(args[0], CultureInfo.InvariantCulture));
    }

    private static object? ScalarPower(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return null;
        return Math.Pow(
            Convert.ToDouble(args[0], CultureInfo.InvariantCulture),
            Convert.ToDouble(args[1], CultureInfo.InvariantCulture));
    }

    private static object? ScalarNow(object?[] args) => DateTime.UtcNow;

    private static object? ScalarDateAdd(object?[] args)
    {
        if (args.Length < 3 || args[0] == null || args[2] == null) return null;
        var part = Convert.ToString(args[0])!.ToUpperInvariant();
        var number = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
        var dt = args[2] is DateTime d ? d
            : DateTime.Parse(Convert.ToString(args[2])!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return part switch
        {
            "DAY" or "D" => dt.AddDays(number),
            "MONTH" or "M" => dt.AddMonths(number),
            "YEAR" or "Y" => dt.AddYears(number),
            "HOUR" or "H" => dt.AddHours(number),
            "MINUTE" or "MI" => dt.AddMinutes(number),
            "SECOND" or "S" => dt.AddSeconds(number),
            _ => null
        };
    }

    private static object? ScalarDateDiff(object?[] args)
    {
        if (args.Length < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
        var part = Convert.ToString(args[0])!.ToUpperInvariant();
        var start = args[1] is DateTime s ? s
            : DateTime.Parse(Convert.ToString(args[1])!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var end = args[2] is DateTime e ? e
            : DateTime.Parse(Convert.ToString(args[2])!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var diff = end - start;
        return (long)(part switch
        {
            "DAY" or "D" => diff.TotalDays,
            "HOUR" or "H" => diff.TotalHours,
            "MINUTE" or "MI" => diff.TotalMinutes,
            "SECOND" or "S" => diff.TotalSeconds,
            _ => diff.TotalDays
        });
    }

    private static object? ScalarReplace(object?[] args)
    {
        if (args.Length < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
        return Convert.ToString(args[0])!.Replace(
            Convert.ToString(args[1])!, Convert.ToString(args[2])!);
    }

    private static object? ScalarCharIndex(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return 0L;
        var substr = Convert.ToString(args[0])!;
        var str = Convert.ToString(args[1])!;
        var start = args.Length >= 3 && args[2] != null
            ? Convert.ToInt32(args[2], CultureInfo.InvariantCulture) - 1 : 0;
        if (start < 0) start = 0;
        if (start >= str.Length) return 0L;
        var idx = str.IndexOf(substr, start, StringComparison.OrdinalIgnoreCase);
        return (long)(idx + 1);
    }

    private static object? ScalarCoalesce(object?[] args)
    {
        foreach (var a in args)
            if (a != null) return a;
        return null;
    }

    private static object? ScalarNullIf(object?[] args)
    {
        if (args.Length < 2) return null;
        if (CompareValues(args[0], args[1], SqlWhereComparisonOperator.Equal))
            return null;
        return args[0];
    }

    private static object? ScalarIIf(object?[] args)
    {
        if (args.Length < 3 || args[0] == null) return args.Length >= 3 ? args[2] : null;
        if (args[0] is bool b) return b ? args[1] : args[2];
        if (args[0] is int i) return i != 0 ? args[1] : args[2];
        return args[0] != null ? args[1] : args[2];
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static object? JsonArrowValue(object? source, string jsonPath, bool unquote, bool isPathArray)
        => JsonOperators.ArrowValue(source, jsonPath, unquote, isPathArray);

    private static object? JsonExtractValue(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return null;
        var segments = JsonOperators.ParseJsonPath(Convert.ToString(args[1])!);
        return JsonOperators.ExtractJsonPathValue(args[0]!, segments, unquote: false);
    }

    private static object? JsonValue(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return null;
        var segments = JsonOperators.ParseJsonPath(Convert.ToString(args[1])!);
        return JsonOperators.ExtractJsonPathValue(args[0]!, segments, unquote: true);
    }

    private static bool JsonContains(object? left, object? right, SqlJsonContainmentOperator op)
        => JsonOperators.JsonContains(left, right, op);

    private static bool JsonKeyExists(object? left, object? right, SqlJsonKeyExistsOperator op)
        => JsonOperators.JsonKeyExists(left, right, op);

    private static string? JsonbBuildObject(object?[] args)
    {
        if (args.Length % 2 != 0) return null;
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartObject();
        for (int i = 0; i < args.Length; i += 2)
        {
            var key = Convert.ToString(args[i]);
            if (key == null) continue;
            writer.WritePropertyName(key);
            WriteJsonValue(writer, args[i + 1]);
        }
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? JsonbBuildArray(object?[] args)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        writer.WriteStartArray();
        foreach (var arg in args)
            WriteJsonValue(writer, arg);
        writer.WriteEndArray();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? JsonbStripNulls(object?[] args)
    {
        if (args.Length < 1 || args[0] == null) return null;
        var element = JsonOperators.ParseToElement(args[0]);
        var result = StripNulls(element);
        return result;
    }

    private static string? JsonbSet(object?[] args)
    {
        if (args.Length < 3 || args[0] == null || args[1] == null) return null;
        var path = Convert.ToString(args[1])!;
        return JsonOperators.JsonSet(args[0], path, args[2]);
    }

    private static string? JsonbInsert(object?[] args)
    {
        if (args.Length < 3 || args[0] == null || args[1] == null) return null;
        var path = Convert.ToString(args[1])!;
        return JsonOperators.JsonInsert(args[0], path, args[2]);
    }

    private static bool JsonbPathExists(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return false;
        var path = Convert.ToString(args[1])!;
        return JsonOperators.JsonPathExists(args[0], path);
    }

    private static string? JsonbPathQuery(object?[] args)
    {
        if (args.Length < 2 || args[0] == null || args[1] == null) return null;
        var path = Convert.ToString(args[1])!;
        return JsonOperators.JsonPathQuery(args[0], path);
    }

    private static void WriteJsonValue(System.Text.Json.Utf8JsonWriter writer, object? value)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        if (value is int i) { writer.WriteNumberValue(i); return; }
        if (value is long l) { writer.WriteNumberValue(l); return; }
        if (value is double d) { writer.WriteNumberValue(d); return; }
        if (value is float f) { writer.WriteNumberValue((double)f); return; }
        if (value is bool b) { writer.WriteBooleanValue(b); return; }
        if (value is string s)
        {
            // Try parsing as embedded JSON
            if (s.StartsWith('{') || s.StartsWith('['))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(s);
                    doc.RootElement.WriteTo(writer);
                    return;
                }
                catch { }
            }
            writer.WriteStringValue(s);
            return;
        }
        writer.WriteStringValue(Convert.ToString(value));
    }

    private static string StripNulls(System.Text.Json.JsonElement element)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);
        WriteElementStrippingNulls(writer, element);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElementStrippingNulls(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                    writer.WritePropertyName(prop.Name);
                    WriteElementStrippingNulls(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElementStrippingNulls(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed class ListValueComparer : IEqualityComparer<object?>
    {
        public static readonly ListValueComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            // Normalise RawStringRef to string for comparisons
            if (x is RawStringRef rsrX) x = rsrX.ToString()!;
            if (y is RawStringRef rsrY) y = rsrY.ToString()!;
            if (x == null || y == null) return false;
            if (x is string sx && y is string sy)
                return WalhallaSql.Collation.CollationManager.Equals(sx, sy, null);
            if (x is byte[] bx && y is byte[] by)
                return CompareBytes(bx, by) == 0;
            return x.Equals(y);
        }

        public int GetHashCode(object? obj)
        {
            if (obj == null) return 0;
            if (obj is RawStringRef rsr) return WalhallaSql.Collation.CollationManager.GetHashCode(rsr.ToString()!, null);
            if (obj is string s) return WalhallaSql.Collation.CollationManager.GetHashCode(s, null);
            return obj.GetHashCode();
        }
    }
}
