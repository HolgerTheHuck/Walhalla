using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal static class AggregateExecutor
{
    public static List<object?[]> ExecuteGroupBy(
        List<object?[]> rows,
        IReadOnlyList<string>? groupByColumns,
        IReadOnlyList<SqlSelectColumn> columns,
        SqlTableDefinition tableDef,
        string[] outputColumnNames)
    {
        var aggColumns = new List<AggColumnInfo>();
        var groupByColIndices = new List<int>();

        // Map GROUP BY columns to indices in the base table.
        if (groupByColumns != null)
        {
            foreach (var colName in groupByColumns)
            {
                var idx = FindColumnIndex(tableDef, colName);
                if (idx < 0)
                    throw new WalhallaException($"GROUP BY column '{colName}' not found.");
                groupByColIndices.Add(idx);
            }
        }

        // Map SELECT columns to their source.
        // Each column is either a GROUP BY column reference or an aggregate.
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.Aggregate != null)
            {
                int? argIdx = null;
                if (col.Aggregate.Argument != null)
                {
                    argIdx = FindColumnIndex(tableDef, col.Aggregate.Argument);
                    if (argIdx < 0)
                        throw new WalhallaException($"Aggregate argument column '{col.Aggregate.Argument}' not found.");
                }
                aggColumns.Add(new AggColumnInfo(i, col.Aggregate.Function, argIdx, col.Alias));
            }
            else if (col.Expression == "*")
            {
                // Simple * — not valid with GROUP BY, just include all columns
                for (int j = 0; j < tableDef.Columns.Count; j++)
                    aggColumns.Add(new AggColumnInfo(j, null, null, null));
            }
            else
            {
                // Non-aggregate column — must be in GROUP BY or it's an error.
                var colName = col.Alias ?? col.Expression;
                var idx = FindColumnIndex(tableDef, colName);
                if (idx < 0)
                    throw new WalhallaException($"Column '{colName}' must appear in GROUP BY or be used in an aggregate function.");
                aggColumns.Add(new AggColumnInfo(i, null, null, col.Alias, groupByIndex: FindGroupByIndex(groupByColumns, col.Expression)));
            }
        }

        // Group rows by GROUP BY columns.
        var groups = new Dictionary<GroupKey, List<object?[]>>();
        foreach (var row in rows)
        {
            var key = new GroupKey(groupByColIndices.Count);
            for (int i = 0; i < groupByColIndices.Count; i++)
                key.Values[i] = row[groupByColIndices[i]];
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<object?[]>();
                groups[key] = list;
            }
            list.Add(row);
        }

        // Compute aggregates for each group.
        var results = new List<object?[]>();
        foreach (var (key, groupRows) in groups)
        {
            var result = new object?[outputColumnNames.Length];

            // Compute each output column.
            foreach (var info in aggColumns)
            {
                var outIdx = info.OutputIndex < outputColumnNames.Length ? info.OutputIndex : info.SourceColumnIndex ?? 0;
                if (info.Function == null)
                {
                    // GROUP BY column or plain column reference.
                    if (info.GroupByIndex >= 0 && info.GroupByIndex < key.Values.Length)
                        result[outIdx] = key.Values[info.GroupByIndex];
                    else if (info.SourceColumnIndex.HasValue && groupRows.Count > 0)
                        result[outIdx] = groupRows[0][info.SourceColumnIndex.Value];
                }
                else
                {
                    result[outIdx] = ComputeAggregate(info.Function.Value, info.ArgumentIndex, groupRows);
                }
            }

            results.Add(result);
        }

        // When there are aggregates but no input rows, still emit one result row
        // with default aggregate values (COUNT → 0, others → null).
        if (results.Count == 0 && groupByColIndices.Count == 0 && aggColumns.Any(a => a.Function != null))
        {
            var emptyResult = new object?[outputColumnNames.Length];
            foreach (var info in aggColumns)
            {
                var outIdx = info.OutputIndex < outputColumnNames.Length ? info.OutputIndex : info.SourceColumnIndex ?? 0;
                if (info.Function != null)
                    emptyResult[outIdx] = info.Function == SqlAggregateFunction.Count ? 0L : null;
            }
            results.Add(emptyResult);
        }

        return results;
    }

    public static List<object?[]> ApplyHaving(
        List<object?[]> aggregatedRows,
        SqlWhereExpression? having,
        string[] outputColumnNames)
    {
        if (having == null) return aggregatedRows;

        var evaluator = new HavingEvaluator(outputColumnNames);
        return aggregatedRows.Where(r => evaluator.Evaluate(having, r)).ToList();
    }

    private static object? ComputeAggregate(
        SqlAggregateFunction func, int? argIdx, List<object?[]> rows)
    {
        switch (func)
        {
            case SqlAggregateFunction.Count:
                if (argIdx == null) return (long)rows.Count; // COUNT(*)
                return (long)rows.Count(r => r[argIdx.Value] != null);

            case SqlAggregateFunction.Sum:
            {
                if (argIdx == null) return null;
                double sum = 0;
                foreach (var r in rows)
                {
                    var v = r[argIdx.Value];
                    if (v != null) sum += Convert.ToDouble(v);
                }
                return sum;
            }

            case SqlAggregateFunction.Avg:
            {
                if (argIdx == null) return null;
                double sum = 0;
                int count = 0;
                foreach (var r in rows)
                {
                    var v = r[argIdx.Value];
                    if (v != null) { sum += Convert.ToDouble(v); count++; }
                }
                return count > 0 ? sum / count : null;
            }

            case SqlAggregateFunction.Min:
            {
                if (argIdx == null) return null;
                object? min = null;
                foreach (var r in rows)
                {
                    var v = r[argIdx.Value];
                    if (v == null) continue;
                    if (min == null || Comparer<object>.Default.Compare(v, min) < 0)
                        min = v;
                }
                return min;
            }

            case SqlAggregateFunction.Max:
            {
                if (argIdx == null) return null;
                object? max = null;
                foreach (var r in rows)
                {
                    var v = r[argIdx.Value];
                    if (v == null) continue;
                    if (max == null || Comparer<object>.Default.Compare(v, max) > 0)
                        max = v;
                }
                return max;
            }

            default:
                return null;
        }
    }

    private static int FindColumnIndex(SqlTableDefinition table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        // Try unqualified name if qualified (e.g., "o.CustomerId" -> "CustomerId")
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            var unqualified = name[(dot + 1)..].Trim();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (string.Equals(table.Columns[i].Name, unqualified, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    private static int FindGroupByIndex(IReadOnlyList<string>? groupByColumns, string expression)
    {
        if (groupByColumns == null) return -1;
        for (int i = 0; i < groupByColumns.Count; i++)
        {
            if (string.Equals(groupByColumns[i], expression, StringComparison.OrdinalIgnoreCase))
                return i;
            // Try matching unqualified names (e.g., "o.CustomerId" vs "CustomerId")
            var dotExpr = expression.LastIndexOf('.');
            var dotGb = groupByColumns[i].LastIndexOf('.');
            var unqExpr = dotExpr >= 0 ? expression[(dotExpr + 1)..].Trim() : expression;
            var unqGb = dotGb >= 0 ? groupByColumns[i][(dotGb + 1)..].Trim() : groupByColumns[i];
            if (string.Equals(unqGb, unqExpr, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private struct AggColumnInfo
    {
        public int OutputIndex;
        public SqlAggregateFunction? Function;
        public int? ArgumentIndex;
        public string? Alias;
        public int GroupByIndex;
        public int? SourceColumnIndex => ArgumentIndex ?? (GroupByIndex >= 0 ? GroupByIndex : null);

        public AggColumnInfo(int outputIndex, SqlAggregateFunction? func, int? argIdx, string? alias, int groupByIndex = -1)
        {
            OutputIndex = outputIndex;
            Function = func;
            ArgumentIndex = argIdx;
            Alias = alias;
            GroupByIndex = groupByIndex;
        }
    }

    private sealed class GroupKey
    {
        public readonly object?[] Values;

        public GroupKey(int count) => Values = new object?[count];

        public override bool Equals(object? obj)
        {
            return obj is GroupKey other && Values.SequenceEqual(other.Values, ValueComparer.Instance);
        }

        public override int GetHashCode()
        {
            if (Values.Length == 0) return 0;
            var hash = new HashCode();
            foreach (var v in Values)
                hash.Add(v ?? 0);
            return hash.ToHashCode();
        }
    }

    private sealed class GroupKeyComparer : IEqualityComparer<object?>, IEqualityComparer<GroupKey>
    {
        public static readonly GroupKeyComparer Instance = new();

        public bool Equals(GroupKey? x, GroupKey? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.Values.SequenceEqual(y.Values, ValueComparer.Instance);
        }

        public int GetHashCode(GroupKey obj)
        {
            var hash = new HashCode();
            foreach (var v in obj.Values)
                hash.Add(v ?? 0);
            return hash.ToHashCode();
        }

        public new bool Equals(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x is GroupKey gx && y is GroupKey gy)
                return Equals(gx, gy);
            if (x is string sx && y is string sy)
                return WalhallaSql.Collation.CollationManager.Equals(sx, sy, null);
            return x.Equals(y);
        }

        public int GetHashCode(object? obj)
        {
            if (obj is GroupKey gk)
                return GetHashCode(gk);
            return obj switch
            {
                null => 0,
                string s => WalhallaSql.Collation.CollationManager.GetHashCode(s, null),
                _ => obj.GetHashCode()
            };
        }
    }

    private sealed class ValueComparer : IEqualityComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x is string sx && y is string sy)
                return WalhallaSql.Collation.CollationManager.Equals(sx, sy, null);
            return x.Equals(y);
        }

        public int GetHashCode(object? obj) => obj switch
        {
            null => 0,
            string s => WalhallaSql.Collation.CollationManager.GetHashCode(s, null),
            _ => obj.GetHashCode()
        };
    }

    private sealed class HavingEvaluator
    {
        private readonly string[] _columnNames;

        public HavingEvaluator(string[] columnNames) => _columnNames = columnNames;

        public bool Evaluate(SqlWhereExpression expr, object?[] row)
        {
            switch (expr)
            {
                case SqlWhereComparisonExpression cmp:
                    return EvaluateComparison(cmp, row);

                case SqlWhereAndExpression and:
                    return Evaluate(and.Children[0], row) && Evaluate(and.Children[1], row);

                case SqlWhereOrExpression or:
                    return Evaluate(or.Children[0], row) || Evaluate(or.Children[1], row);

                case SqlWhereNotExpression not:
                    return !Evaluate(not.Inner, row);

                default:
                    return false;
            }
        }

        private bool EvaluateComparison(SqlWhereComparisonExpression cmp, object?[] row)
        {
            var left = GetValue(cmp.Left, row);
            var right = GetValue(cmp.Right, row);

            return cmp.Operator switch
            {
                SqlWhereComparisonOperator.Equal => Equals(left, right) || (left != null && left.Equals(right)),
                SqlWhereComparisonOperator.NotEqual => !(Equals(left, right) || (left != null && left.Equals(right))),
                SqlWhereComparisonOperator.GreaterThan => Compare(left, right) > 0,
                SqlWhereComparisonOperator.GreaterThanOrEqual => Compare(left, right) >= 0,
                SqlWhereComparisonOperator.LessThan => Compare(left, right) < 0,
                SqlWhereComparisonOperator.LessThanOrEqual => Compare(left, right) <= 0,
                _ => false
            };
        }

        private object? GetValue(SqlWhereValueExpression value, object?[] row)
        {
            switch (value)
            {
                case SqlWhereColumnExpression col:
                    for (int i = 0; i < _columnNames.Length; i++)
                    {
                        if (string.Equals(_columnNames[i], col.SimpleName, StringComparison.OrdinalIgnoreCase))
                            return row[i];
                    }
                    return null;

                case SqlWhereLiteralExpression lit:
                    return lit.Value;

                default:
                    return null;
            }
        }

        private static int Compare(object? a, object? b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            if (a is IComparable ca)
                return ca.CompareTo(Convert.ChangeType(b, a.GetType()));

            return string.CompareOrdinal(a.ToString(), b.ToString());
        }
    }
}
