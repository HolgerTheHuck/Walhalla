using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;

namespace WalhallaSql.Execution;

internal static class IndexSelector
{
    /// <summary>
    /// Extract sargable predicates from a WHERE expression.
    /// Only top-level AND children are examined; OR/other expressions return empty.
    /// </summary>
    public static List<SargablePredicate> ExtractSargable(
        SqlWhereExpression? where, SqlTableDefinition table)
    {
        var result = new List<SargablePredicate>();
        if (where == null) return result;

        FlattenAnd(where, result, table);
        return result;
    }

    private static void FlattenAnd(
        SqlWhereExpression expr, List<SargablePredicate> result, SqlTableDefinition table)
    {
        if (expr is SqlWhereAndExpression and)
        {
            foreach (var child in and.Children)
                FlattenAnd(child, result, table);
        }
        else
        {
            TryConvertToSargable(expr, table, result);
        }
    }

    private static void TryConvertToSargable(
        SqlWhereExpression expr, SqlTableDefinition table, List<SargablePredicate> result)
    {
        if (expr is SqlWhereComparisonExpression cmp)
        {
            if (cmp.Left is SqlWhereColumnExpression colExpr)
            {
                var colIdx = FindColumnIndex(table, colExpr.SimpleName);
                if (colIdx < 0) return;

                if (cmp.Right is SqlWhereLiteralExpression lit)
                {
                    result.Add(new SargablePredicate(colIdx, cmp.Operator, lit.Value));
                }
                else if (cmp.Right is SqlWhereParameterExpression param)
                {
                    result.Add(new SargablePredicate(
                        colIdx, cmp.Operator, param.Index, ValueIsParameter: true));
                }
            }
            // JSON arrow: col->'$.path' = value
            else if (cmp.Left is SqlWhereJsonArrowExpression arrow)
            {
                if (arrow.Source is SqlWhereColumnExpression srcCol && cmp.Right is SqlWhereLiteralExpression litArrow)
                {
                    var colIdx = FindColumnIndex(table, srcCol.SimpleName);
                    if (colIdx >= 0)
                        result.Add(new SargablePredicate(colIdx, cmp.Operator, litArrow.Value,
                            JsonPath: arrow.JsonPath, SourceColumnName: srcCol.SimpleName));
                }
            }
            // JSON function: JSON_EXTRACT(col, '$.path') = value
            else if (cmp.Left is SqlWhereFunctionCallExpression func
                && (func.FunctionName == "JSON_EXTRACT" || func.FunctionName == "JSON_VALUE")
                && func.Arguments.Count >= 2
                && func.Arguments[0] is SqlWhereColumnExpression jsonCol
                && func.Arguments[1] is SqlWhereLiteralExpression pathLit)
            {
                var colIdx = FindColumnIndex(table, jsonCol.SimpleName);
                if (colIdx >= 0 && cmp.Right is SqlWhereLiteralExpression litFunc)
                {
                    var jsonPath = Convert.ToString(pathLit.Value) ?? "";
                    result.Add(new SargablePredicate(colIdx, cmp.Operator, litFunc.Value,
                        JsonPath: jsonPath, SourceColumnName: jsonCol.SimpleName));
                }
            }
            // Also support: literal = column (reversed)
            else if (cmp.Right is SqlWhereColumnExpression colExpr2)
            {
                var colIdx = FindColumnIndex(table, colExpr2.SimpleName);
                if (colIdx < 0) return;

                if (cmp.Left is SqlWhereLiteralExpression lit2)
                {
                    result.Add(new SargablePredicate(colIdx, FlipOperator(cmp.Operator), lit2.Value));
                }
                else if (cmp.Left is SqlWhereParameterExpression param2)
                {
                    result.Add(new SargablePredicate(
                        colIdx, FlipOperator(cmp.Operator), param2.Index, ValueIsParameter: true));
                }
            }
            // Reversed: literal = JSON arrow
            else if (cmp.Right is SqlWhereJsonArrowExpression arrowR)
            {
                if (arrowR.Source is SqlWhereColumnExpression srcColR && cmp.Left is SqlWhereLiteralExpression litArrowR)
                {
                    var colIdx = FindColumnIndex(table, srcColR.SimpleName);
                    if (colIdx >= 0)
                        result.Add(new SargablePredicate(colIdx, FlipOperator(cmp.Operator), litArrowR.Value,
                            JsonPath: arrowR.JsonPath, SourceColumnName: srcColR.SimpleName));
                }
            }
            // Reversed: literal = JSON_EXTRACT/VALUE
            else if (cmp.Right is SqlWhereFunctionCallExpression funcR
                && (funcR.FunctionName == "JSON_EXTRACT" || funcR.FunctionName == "JSON_VALUE")
                && funcR.Arguments.Count >= 2
                && funcR.Arguments[0] is SqlWhereColumnExpression jsonColR
                && funcR.Arguments[1] is SqlWhereLiteralExpression pathLitR
                && cmp.Left is SqlWhereLiteralExpression litFuncR)
            {
                var colIdx = FindColumnIndex(table, jsonColR.SimpleName);
                if (colIdx >= 0)
                {
                    var jsonPath = Convert.ToString(pathLitR.Value) ?? "";
                    result.Add(new SargablePredicate(colIdx, FlipOperator(cmp.Operator), litFuncR.Value,
                        JsonPath: jsonPath, SourceColumnName: jsonColR.SimpleName));
                }
            }
        }
        else if (expr is SqlWhereBetweenExpression between
            && between.Value is SqlWhereColumnExpression col3)
        {
            var colIdx = FindColumnIndex(table, col3.SimpleName);
            if (colIdx >= 0)
            {
                if (between.Lower is SqlWhereLiteralExpression lowerLit
                    && between.Upper is SqlWhereLiteralExpression upperLit)
                {
                    result.Add(new SargablePredicate(
                        colIdx, SqlWhereComparisonOperator.GreaterThanOrEqual, lowerLit.Value));
                    result.Add(new SargablePredicate(
                        colIdx, SqlWhereComparisonOperator.LessThanOrEqual, upperLit.Value));
                }
                else if (between.Lower is SqlWhereParameterExpression lowerParam
                    && between.Upper is SqlWhereParameterExpression upperParam)
                {
                    result.Add(new SargablePredicate(
                        colIdx, SqlWhereComparisonOperator.GreaterThanOrEqual,
                        lowerParam.Index, ValueIsParameter: true));
                    result.Add(new SargablePredicate(
                        colIdx, SqlWhereComparisonOperator.LessThanOrEqual,
                        upperParam.Index, ValueIsParameter: true));
                }
            }
        }
        else if (expr is SqlWhereNullCheckExpression nullCheck
            && nullCheck.Value is SqlWhereColumnExpression col4)
        {
            var colIdx = FindColumnIndex(table, col4.SimpleName);
            if (colIdx >= 0)
            {
                result.Add(new SargablePredicate(
                    colIdx,
                    nullCheck.Negated ? SqlWhereComparisonOperator.NotEqual : SqlWhereComparisonOperator.Equal,
                    null, IsNullCheck: true));
            }
        }
        else if (expr is SqlWhereInListExpression inList
            && inList.Left is SqlWhereColumnExpression col5)
        {
            var colIdx = FindColumnIndex(table, col5.SimpleName);
            if (colIdx >= 0)
            {
                foreach (var v in inList.Values)
                {
                    if (v is SqlWhereLiteralExpression litVal)
                    {
                        result.Add(new SargablePredicate(
                            colIdx, SqlWhereComparisonOperator.Equal, litVal.Value));
                    }
                }
            }
        }
        // ── GIN-accelerable JSONB predicates ────────────────────────────────
        else if (expr is SqlWhereJsonContainsExpression contains
            && contains.Left is SqlWhereColumnExpression jsonCol)
        {
            var colIdx = FindColumnIndex(table, jsonCol.SimpleName);
            if (colIdx >= 0 && contains.Right is SqlWhereLiteralExpression lit)
            {
                var ginOp = contains.Operator == SqlJsonContainmentOperator.Contains
                    ? GinPredicateType.Contains : GinPredicateType.ContainedBy;
                result.Add(new SargablePredicate(
                    colIdx, SqlWhereComparisonOperator.Equal, null,
                    GinOperator: ginOp,
                    GinQueryJson: Convert.ToString(lit.Value) ?? "",
                    SourceColumnName: jsonCol.SimpleName));
            }
        }
        else if (expr is SqlWhereJsonKeyExistsExpression keyExists
            && keyExists.Left is SqlWhereColumnExpression jsonCol2)
        {
            var colIdx = FindColumnIndex(table, jsonCol2.SimpleName);
            if (colIdx >= 0)
            {
                var ginOp = keyExists.Operator switch
                {
                    SqlJsonKeyExistsOperator.HasKey => GinPredicateType.KeyExists,
                    SqlJsonKeyExistsOperator.HasAnyKey => GinPredicateType.AnyKey,
                    SqlJsonKeyExistsOperator.HasAllKeys => GinPredicateType.AllKeys,
                    _ => GinPredicateType.None
                };
                var jsonText = keyExists.Right is SqlWhereLiteralExpression lit
                    ? Convert.ToString(lit.Value) ?? "" : "";
                result.Add(new SargablePredicate(
                    colIdx, SqlWhereComparisonOperator.Equal, null,
                    GinOperator: ginOp,
                    GinQueryJson: jsonText,
                    SourceColumnName: jsonCol2.SimpleName));
            }
        }
    }

    // ── Index selection ───────────────────────────────────────────────────

    public static IndexSelection? SelectBestIndex(
        IReadOnlyList<SqlIndexDefinition> indexes,
        Dictionary<string, int> indexIds,
        List<SargablePredicate> sargablePredicates,
        int[] projectedColumnIndices,
        SqlTableDefinition table,
        TableStatistics? statistics = null)
    {
        if (indexes.Count == 0 || sargablePredicates.Count == 0)
            return null;

        IndexSelection? best = null;
        int bestScore = -1;
        double bestSelectivity = double.MaxValue;

        foreach (var idx in indexes)
        {
            if (!indexIds.TryGetValue(idx.IndexName, out var indexId))
                continue;

            var (matched, residual, score) = ScoreIndex(
                idx, sargablePredicates, projectedColumnIndices, table);

            if (matched.Count == 0) continue;

            bool isBetter;
            double currentSelectivity = 1.0;

            if (score > bestScore)
            {
                isBetter = true;
                if (statistics != null)
                    currentSelectivity = ComputeMatchedSelectivity(matched, table, statistics);
            }
            else if (score == bestScore && statistics != null)
            {
                currentSelectivity = ComputeMatchedSelectivity(matched, table, statistics);
                isBetter = currentSelectivity < bestSelectivity;
            }
            else
            {
                isBetter = false;
            }

            if (isBetter)
            {
                int[] colIndices;
                SqlScalarType[] keyTypes;

                if (idx.TargetsProjection)
                {
                    var proj = table.Projections?.FirstOrDefault(
                        p => string.Equals(p.ProjectionName, idx.TargetProjectionName, StringComparison.OrdinalIgnoreCase));
                    var srcColIdx = proj != null ? FindColumnIndex(table, proj.SourceColumnName) : -1;
                    colIndices = new[] { srcColIdx };
                    keyTypes = new[] { SqlScalarType.String };
                }
                else
                {
                    colIndices = new int[idx.ColumnNames.Count];
                    keyTypes = new SqlScalarType[idx.ColumnNames.Count];
                    for (int i = 0; i < idx.ColumnNames.Count; i++)
                    {
                        colIndices[i] = FindColumnIndex(table, idx.ColumnNames[i]);
                        keyTypes[i] = table.Columns[colIndices[i]].Type;
                    }
                }

                int projectedTableColumnCount = 0;
                foreach (var pi in projectedColumnIndices)
                    if (pi >= 0) projectedTableColumnCount++;

                bool covering = idx.TargetsProjection
                    ? false
                    : projectedTableColumnCount <= idx.ColumnNames.Count
                        && AllColumnsCovered(projectedColumnIndices, idx, table);

                bestScore = score;
                bestSelectivity = currentSelectivity;
                best = new IndexSelection(
                    idx, indexId, score, covering,
                    colIndices, keyTypes,
                    matched, residual);
            }
        }

        return best;
    }

    private static double ComputeMatchedSelectivity(
        List<SargablePredicate> matched,
        SqlTableDefinition table,
        TableStatistics statistics)
    {
        double sel = 1.0;
        foreach (var pred in matched)
        {
            if (pred.ColumnIndex < 0 || pred.ColumnIndex >= table.Columns.Count) continue;
            var colName = table.Columns[pred.ColumnIndex].Name;
            statistics.Columns.TryGetValue(colName, out var colStats);
            sel *= SelectivityEstimator.EstimatePredicateSelectivity(pred.Operator, pred.Value, colStats);
        }
        return sel;
    }

    private static (List<SargablePredicate> Matched, List<SargablePredicate> Residual, int Score)
        ScoreIndex(
            SqlIndexDefinition index,
            List<SargablePredicate> sargablePredicates,
            int[] projectedColumnIndices,
            SqlTableDefinition table)
    {
        var matched = new List<SargablePredicate>();
        var residual = new List<SargablePredicate>();
        int equalityCount = 0;
        int rangeCount = 0;
        bool handledFirstRange = false;

        if (index.TargetsProjection)
        {
            // For projection indexes, match by JsonPath + SourceColumnName
            foreach (var pred in sargablePredicates)
            {
                if (pred.JsonPath == null || pred.SourceColumnName == null) continue;
                if (matched.Contains(pred)) continue;

                // Match: predicate has a JSON path (will be matched by index)
                if (pred.Operator == SqlWhereComparisonOperator.Equal && !handledFirstRange)
                {
                    matched.Add(pred);
                    equalityCount++;
                }
                else if (!handledFirstRange)
                {
                    matched.Add(pred);
                    rangeCount++;
                    handledFirstRange = true;
                }
                if (handledFirstRange) break;
            }
        }
        else
        {
            for (int i = 0; i < index.ColumnNames.Count; i++)
            {
                var idxCol = index.ColumnNames[i];
                var colIdx = FindColumnIndex(table, idxCol);
                if (colIdx < 0) continue;

                bool foundMatch = false;

                foreach (var pred in sargablePredicates)
                {
                    if (pred.ColumnIndex != colIdx) continue;
                    if (matched.Contains(pred)) continue;

                    if (pred.Operator == SqlWhereComparisonOperator.Equal && !handledFirstRange)
                    {
                        matched.Add(pred);
                        equalityCount++;
                        foundMatch = true;
                        break;
                    }
                    else if (!handledFirstRange)
                    {
                        matched.Add(pred);
                        rangeCount++;
                        handledFirstRange = true;
                        foundMatch = true;
                        break;
                    }
                }

                if (!foundMatch) break;
            }
        }

        // Remaining are residual.
        foreach (var pred in sargablePredicates)
        {
            if (!matched.Contains(pred))
                residual.Add(pred);
        }

        int projectedTableColumnCount = 0;
        foreach (var pi in projectedColumnIndices)
            if (pi >= 0) projectedTableColumnCount++;

        bool covering = projectedTableColumnCount <= index.ColumnNames.Count
            && AllColumnsCovered(projectedColumnIndices, index, table);

        // GIN indexes score very high for their specific operators (higher than any BTree equality).
        bool hasGinMatch = index.IndexType == SqlIndexType.Gin
            && matched.Any(p => p.GinOperator != GinPredicateType.None);
        int score = hasGinMatch
            ? 200 + (covering ? 30 : 0)
            : (equalityCount * 10) + (rangeCount * 5) + (index.IsUnique ? 20 : 0) + (covering ? 30 : 0);

        return (matched, residual, score);
    }

    private static bool AllColumnsCovered(
        int[] projectedColumnIndices, SqlIndexDefinition index, SqlTableDefinition table)
    {
        foreach (var projIdx in projectedColumnIndices)
        {
            if (projIdx < 0)
                continue; // computed / derived column – not stored, so index coverage is irrelevant
            var projName = table.Columns[projIdx].Name;
            bool found = false;
            foreach (var idxName in index.ColumnNames)
            {
                if (string.Equals(idxName, projName, StringComparison.OrdinalIgnoreCase))
                { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static SqlWhereComparisonOperator FlipOperator(SqlWhereComparisonOperator op)
    {
        return op switch
        {
            SqlWhereComparisonOperator.GreaterThan => SqlWhereComparisonOperator.LessThan,
            SqlWhereComparisonOperator.GreaterThanOrEqual => SqlWhereComparisonOperator.LessThanOrEqual,
            SqlWhereComparisonOperator.LessThan => SqlWhereComparisonOperator.GreaterThan,
            SqlWhereComparisonOperator.LessThanOrEqual => SqlWhereComparisonOperator.GreaterThanOrEqual,
            _ => op
        };
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
}
