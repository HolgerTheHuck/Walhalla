using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Window;

/// <summary>
/// Evaluates SQL window functions over a fully materialized row set. This is the logical
/// "window operator" that sits between aggregation and projection: it partitions the input,
/// orders each partition (where ORDER BY is not already guaranteed by the input), resolves the
/// applicable frame, and produces one value per input row for each window-function column.
/// </summary>
/// <remarks>
/// Each returned value array is indexed by the row's ORIGINAL position in <c>rows</c>. The
/// original index is kept bound to its row throughout partitioning and sorting, so the
/// value↔row mapping stays correct regardless of storage order (the caller must look the value
/// up by the row's original index, not by its post-ORDER-BY position).
///
/// Memory footprint: the operator buffers the entire input plus per-partition index lists in
/// memory. For very large partitions this is O(n) additional memory; spill-to-disk for oversized
/// partitions is tracked as a v1.x backlog item.
/// </remarks>
internal static class WindowFunctionEvaluator
{
    /// <summary>Returns a map of output column index → precomputed window function values.</summary>
    public static Dictionary<int, object?[]> Compute(
        List<object?[]> rows,
        IReadOnlyList<SqlSelectColumn> columns,
        SqlTableDefinition tableDef)
    {
        var result = new Dictionary<int, object?[]>();
        if (rows.Count == 0) return result;

        for (int ci = 0; ci < columns.Count; ci++)
        {
            var wf = columns[ci].WindowFunction;
            if (wf == null) continue;

            var values = wf.Function switch
            {
                SqlWindowFunctionType.RowNumber => ComputeRowNumberValues(rows, wf, tableDef),
                SqlWindowFunctionType.Rank => ComputeRankValues(rows, wf, tableDef, dense: false),
                SqlWindowFunctionType.DenseRank => ComputeRankValues(rows, wf, tableDef, dense: true),
                SqlWindowFunctionType.NTile => ComputeNTileValues(rows, wf, tableDef),
                SqlWindowFunctionType.PercentRank => ComputePercentRankValues(rows, wf, tableDef),
                SqlWindowFunctionType.CumeDist => ComputeCumeDistValues(rows, wf, tableDef),
                SqlWindowFunctionType.Aggregate => ComputeAggregateWindowValues(rows, wf, tableDef),
                SqlWindowFunctionType.Lag => ComputeOffsetValues(rows, wf, tableDef, lead: false),
                SqlWindowFunctionType.Lead => ComputeOffsetValues(rows, wf, tableDef, lead: true),
                SqlWindowFunctionType.FirstValue => ComputeValueFunctionValues(rows, wf, tableDef, SqlWindowFunctionType.FirstValue),
                SqlWindowFunctionType.LastValue => ComputeValueFunctionValues(rows, wf, tableDef, SqlWindowFunctionType.LastValue),
                SqlWindowFunctionType.NthValue => ComputeValueFunctionValues(rows, wf, tableDef, SqlWindowFunctionType.NthValue),
                _ => null
            };

            if (values != null)
                result[ci] = values;
        }

        return result;
    }

    private static object?[] ComputeRowNumberValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        foreach (var partition in partitions)
        {
            if (wf.OrderBy != null && wf.OrderBy.Count > 0)
                SortPartition(partition, wf.OrderBy, tableDef);

            long rowNum = 1;
            foreach (var (idx, _) in partition)
                values[idx] = rowNum++;
        }

        return values;
    }

    private static object?[] ComputeRankValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef, bool dense)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        foreach (var partition in partitions)
        {
            if (wf.OrderBy != null && wf.OrderBy.Count > 0)
                SortPartition(partition, wf.OrderBy, tableDef);

            long rank = 1;
            long rowCount = 0;
            object?[]? prevRow = null;

            foreach (var (idx, row) in partition)
            {
                if (prevRow != null && !RowsEqualByOrderColumns(prevRow, row, wf.OrderBy, tableDef))
                    rank = dense ? rank + 1 : rowCount + 1;

                values[idx] = rank;
                prevRow = row;
                rowCount++;
            }
        }

        return values;
    }

    private static object?[] ComputeNTileValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        var buckets = wf.NTileBuckets ?? 1;
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        foreach (var partition in partitions)
        {
            if (wf.OrderBy != null && wf.OrderBy.Count > 0)
                SortPartition(partition, wf.OrderBy, tableDef);

            var n = partition.Count;
            if (n == 0) continue;

            // First (n % buckets) buckets receive one extra row.
            var baseSize = n / buckets;
            var remainder = n % buckets;

            var pos = 0;
            for (long bucket = 1; bucket <= buckets && pos < n; bucket++)
            {
                var size = baseSize + (bucket <= remainder ? 1 : 0);
                for (var k = 0; k < size && pos < n; k++, pos++)
                    values[partition[pos].Index] = bucket;
            }
        }

        return values;
    }

    private static object?[] ComputePercentRankValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        foreach (var partition in partitions)
        {
            if (wf.OrderBy != null && wf.OrderBy.Count > 0)
                SortPartition(partition, wf.OrderBy, tableDef);

            var n = partition.Count;
            if (n == 0) continue;

            // PERCENT_RANK = (rank - 1) / (rows - 1); single-row partitions yield 0.
            long rank = 1;
            long rowCount = 0;
            object?[]? prevRow = null;

            foreach (var (idx, row) in partition)
            {
                if (prevRow != null && !RowsEqualByOrderColumns(prevRow, row, wf.OrderBy, tableDef))
                    rank = rowCount + 1;

                values[idx] = n == 1 ? 0.0 : (double)(rank - 1) / (n - 1);
                prevRow = row;
                rowCount++;
            }
        }

        return values;
    }

    private static object?[] ComputeCumeDistValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        foreach (var partition in partitions)
        {
            if (wf.OrderBy != null && wf.OrderBy.Count > 0)
                SortPartition(partition, wf.OrderBy, tableDef);

            var n = partition.Count;
            if (n == 0) continue;

            // CUME_DIST = (# rows ordered <= current, including peers) / total rows.
            // All rows in a peer group share the cumulative count at the group's last position.
            var i = 0;
            while (i < n)
            {
                var j = i + 1;
                while (j < n && RowsEqualByOrderColumns(partition[i].Row, partition[j].Row, wf.OrderBy, tableDef))
                    j++;

                var cumeDist = (double)j / n;
                for (var k = i; k < j; k++)
                    values[partition[k].Index] = cumeDist;

                i = j;
            }
        }

        return values;
    }

    private static object?[] ComputeAggregateWindowValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);

        var aggFunc = wf.AggregateFunction ?? SqlAggregateFunction.Count;
        var hasOrderBy = wf.OrderBy != null && wf.OrderBy.Count > 0;

        // Resolve the aggregated column index (null for COUNT(*)).
        int? argIdx = null;
        if (wf.AggregateArgument != null)
        {
            argIdx = FindColumnIndex(tableDef, wf.AggregateArgument);
            if (argIdx < 0)
                throw new WalhallaException($"Unknown column '{wf.AggregateArgument}' in window aggregate.");
        }

        foreach (var partition in partitions)
        {
            if (hasOrderBy)
                SortPartition(partition, wf.OrderBy!, tableDef);

            var n = partition.Count;
            for (var i = 0; i < n; i++)
            {
                var (startIdx, endIdx) = ResolveFrameBounds(partition, i, wf, tableDef, hasOrderBy);
                values[partition[i].Index] = ComputeWindowAggregate(partition, startIdx, endIdx, aggFunc, argIdx);
            }
        }

        return values;
    }

    /// <summary>
    /// Resolves the inclusive [start, end] row range (within a sorted partition) that the window
    /// frame for the row at <paramref name="i"/> covers. A start greater than end denotes an empty frame.
    /// </summary>
    private static (int Start, int End) ResolveFrameBounds(
        List<(int Index, object?[] Row)> partition, int i, SqlWindowCall wf,
        SqlTableDefinition tableDef, bool hasOrderBy)
    {
        var n = partition.Count;

        // Default frame: with ORDER BY → RANGE UNBOUNDED PRECEDING .. CURRENT ROW (peer-aware running);
        // without ORDER BY → the entire partition.
        if (wf.Frame == null)
        {
            if (!hasOrderBy)
                return (0, n - 1);
            return (0, LastPeerIndex(partition, i, wf.OrderBy, tableDef));
        }

        var frame = wf.Frame;
        if (frame.Mode == SqlWindowFrameMode.Rows)
        {
            var start = ResolveRowsBound(frame.Start, i, n, isStart: true);
            var end = ResolveRowsBound(frame.End, i, n, isStart: false);
            return (Math.Max(0, start), Math.Min(n - 1, end));
        }

        // RANGE / GROUPS: support UNBOUNDED and CURRENT ROW bounds with peer semantics.
        var rangeStart = ResolvePeerBound(partition, i, frame.Start, wf.OrderBy, tableDef, isStart: true);
        var rangeEnd = ResolvePeerBound(partition, i, frame.End, wf.OrderBy, tableDef, isStart: false);
        return (rangeStart, rangeEnd);
    }

    private static int ResolveRowsBound(SqlWindowFrameBound bound, int i, int n, bool isStart)
    {
        return bound.BoundType switch
        {
            SqlWindowFrameBoundType.UnboundedPreceding => 0,
            SqlWindowFrameBoundType.UnboundedFollowing => n - 1,
            SqlWindowFrameBoundType.CurrentRow => i,
            SqlWindowFrameBoundType.Preceding => i - (bound.Offset ?? 0),
            SqlWindowFrameBoundType.Following => i + (bound.Offset ?? 0),
            _ => isStart ? 0 : n - 1
        };
    }

    private static int ResolvePeerBound(
        List<(int Index, object?[] Row)> partition, int i, SqlWindowFrameBound bound,
        IReadOnlyList<SqlOrderByColumn>? orderBy, SqlTableDefinition tableDef, bool isStart)
    {
        switch (bound.BoundType)
        {
            case SqlWindowFrameBoundType.UnboundedPreceding:
                return 0;
            case SqlWindowFrameBoundType.UnboundedFollowing:
                return partition.Count - 1;
            case SqlWindowFrameBoundType.CurrentRow:
                return isStart
                    ? FirstPeerIndex(partition, i, orderBy, tableDef)
                    : LastPeerIndex(partition, i, orderBy, tableDef);
            default:
                throw new WalhallaException(
                    "RANGE/GROUPS window frames support only UNBOUNDED and CURRENT ROW bounds.");
        }
    }

    private static int FirstPeerIndex(
        List<(int Index, object?[] Row)> partition, int i,
        IReadOnlyList<SqlOrderByColumn>? orderBy, SqlTableDefinition tableDef)
    {
        var start = i;
        while (start > 0 &&
               RowsEqualByOrderColumns(partition[start - 1].Row, partition[i].Row, orderBy, tableDef))
            start--;
        return start;
    }

    private static int LastPeerIndex(
        List<(int Index, object?[] Row)> partition, int i,
        IReadOnlyList<SqlOrderByColumn>? orderBy, SqlTableDefinition tableDef)
    {
        var end = i;
        while (end < partition.Count - 1 &&
               RowsEqualByOrderColumns(partition[end + 1].Row, partition[i].Row, orderBy, tableDef))
            end++;
        return end;
    }

    private static object? ComputeWindowAggregate(
        List<(int Index, object?[] Row)> partition, int startIdx, int endIdx,
        SqlAggregateFunction func, int? argIdx)
    {
        if (startIdx > endIdx)
            return func == SqlAggregateFunction.Count ? 0L : null;

        switch (func)
        {
            case SqlAggregateFunction.Count:
            {
                long count = 0;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    if (argIdx == null) count++;
                    else if (partition[k].Row[argIdx.Value] != null) count++;
                }
                return count;
            }
            case SqlAggregateFunction.Sum:
            {
                if (argIdx == null) return null;
                double sum = 0;
                var any = false;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    var v = partition[k].Row[argIdx.Value];
                    if (v != null) { sum += Convert.ToDouble(v); any = true; }
                }
                return any ? sum : (double?)null;
            }
            case SqlAggregateFunction.Avg:
            {
                if (argIdx == null) return null;
                double sum = 0;
                var count = 0;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    var v = partition[k].Row[argIdx.Value];
                    if (v != null) { sum += Convert.ToDouble(v); count++; }
                }
                return count > 0 ? sum / count : (double?)null;
            }
            case SqlAggregateFunction.Min:
            {
                if (argIdx == null) return null;
                object? min = null;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    var v = partition[k].Row[argIdx.Value];
                    if (v == null) continue;
                    if (min == null || Comparer<object>.Default.Compare(v, min) < 0) min = v;
                }
                return min;
            }
            case SqlAggregateFunction.Max:
            {
                if (argIdx == null) return null;
                object? max = null;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    var v = partition[k].Row[argIdx.Value];
                    if (v == null) continue;
                    if (max == null || Comparer<object>.Default.Compare(v, max) > 0) max = v;
                }
                return max;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Computes LAG/LEAD values: the value of the offset column from the row a given number of
    /// positions before (LAG) or after (LEAD) the current row within the ordered partition.
    /// With IGNORE NULLS only rows whose offset value is non-null are counted.
    /// </summary>
    private static object?[] ComputeOffsetValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef, bool lead)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);
        var hasOrderBy = wf.OrderBy != null && wf.OrderBy.Count > 0;

        var argIdx = FindColumnIndex(tableDef, wf.OffsetColumn!);
        if (argIdx < 0)
            throw new WalhallaException($"Unknown column '{wf.OffsetColumn}' in window function.");

        var offset = wf.OffsetAmount ?? 1;
        object? defaultValue = null;
        if (wf.OffsetDefault != null)
            defaultValue = WalhallaEngine.ParseLiteral(wf.OffsetDefault, tableDef.Columns[argIdx].Type);

        var step = lead ? 1 : -1;

        foreach (var partition in partitions)
        {
            if (hasOrderBy)
                SortPartition(partition, wf.OrderBy!, tableDef);

            var n = partition.Count;
            for (var i = 0; i < n; i++)
            {
                object? value;
                if (offset == 0)
                {
                    value = partition[i].Row[argIdx];
                }
                else if (!wf.IgnoreNulls)
                {
                    var target = i + step * offset;
                    value = target >= 0 && target < n ? partition[target].Row[argIdx] : defaultValue;
                }
                else
                {
                    var count = 0;
                    value = defaultValue;
                    for (var k = i + step; k >= 0 && k < n; k += step)
                    {
                        if (partition[k].Row[argIdx] == null) continue;
                        if (++count == offset)
                        {
                            value = partition[k].Row[argIdx];
                            break;
                        }
                    }
                }

                values[partition[i].Index] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Computes FIRST_VALUE/LAST_VALUE/NTH_VALUE within each row's frame. The default frame
    /// (RANGE UNBOUNDED PRECEDING .. CURRENT ROW when ORDER BY is present) applies, so LAST_VALUE
    /// returns the current row by default. With IGNORE NULLS, null values are skipped.
    /// </summary>
    private static object?[] ComputeValueFunctionValues(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef, SqlWindowFunctionType funcType)
    {
        var values = new object?[rows.Count];
        var partitions = PartitionRowsWithIndices(rows, wf, tableDef);
        var hasOrderBy = wf.OrderBy != null && wf.OrderBy.Count > 0;

        var argIdx = FindColumnIndex(tableDef, wf.OffsetColumn!);
        if (argIdx < 0)
            throw new WalhallaException($"Unknown column '{wf.OffsetColumn}' in window function.");

        foreach (var partition in partitions)
        {
            if (hasOrderBy)
                SortPartition(partition, wf.OrderBy!, tableDef);

            var n = partition.Count;
            for (var i = 0; i < n; i++)
            {
                var (startIdx, endIdx) = ResolveFrameBounds(partition, i, wf, tableDef, hasOrderBy);
                values[partition[i].Index] = SelectFrameValue(
                    partition, startIdx, endIdx, argIdx, funcType, wf.OffsetAmount, wf.IgnoreNulls);
            }
        }

        return values;
    }

    private static object? SelectFrameValue(
        List<(int Index, object?[] Row)> partition, int startIdx, int endIdx, int argIdx,
        SqlWindowFunctionType funcType, int? nth, bool ignoreNulls)
    {
        if (startIdx > endIdx)
            return null;

        switch (funcType)
        {
            case SqlWindowFunctionType.FirstValue:
                if (!ignoreNulls)
                    return partition[startIdx].Row[argIdx];
                for (var k = startIdx; k <= endIdx; k++)
                    if (partition[k].Row[argIdx] != null)
                        return partition[k].Row[argIdx];
                return null;

            case SqlWindowFunctionType.LastValue:
                if (!ignoreNulls)
                    return partition[endIdx].Row[argIdx];
                for (var k = endIdx; k >= startIdx; k--)
                    if (partition[k].Row[argIdx] != null)
                        return partition[k].Row[argIdx];
                return null;

            case SqlWindowFunctionType.NthValue:
            {
                var target = nth ?? 1;
                var count = 0;
                for (var k = startIdx; k <= endIdx; k++)
                {
                    if (ignoreNulls && partition[k].Row[argIdx] == null) continue;
                    if (++count == target)
                        return partition[k].Row[argIdx];
                }
                return null;
            }

            default:
                return null;
        }
    }

    private static List<List<(int Index, object?[] Row)>> PartitionRowsWithIndices(
        List<object?[]> rows, SqlWindowCall wf, SqlTableDefinition tableDef)
    {
        if (wf.PartitionBy == null || wf.PartitionBy.Count == 0)
        {
            var all = new List<(int, object?[])>(rows.Count);
            for (int i = 0; i < rows.Count; i++) all.Add((i, rows[i]));
            return new List<List<(int, object?[])>> { all };
        }

        var groups = new Dictionary<string, List<(int, object?[])>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var key = BuildPartitionKey(rows[i], wf.PartitionBy, tableDef);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<(int, object?[])>();
                groups[key] = list;
            }
            list.Add((i, rows[i]));
        }
        return groups.Values.ToList();
    }

    private static string BuildPartitionKey(
        object?[] row, IReadOnlyList<string> partitionCols, SqlTableDefinition tableDef)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var colName in partitionCols)
        {
            // Normalisiere qualifizierte Spaltennamen (z.B. "p.CustomerId" → "CustomerId")
            var unqualified = colName.Contains('.')
                ? colName[(colName.LastIndexOf('.') + 1)..]
                : colName;
            var colIdx = FindColumnIndex(tableDef, unqualified);
            if (colIdx < 0) colIdx = FindColumnIndex(tableDef, colName);
            var val = colIdx >= 0 && colIdx < row.Length ? row[colIdx] : null;
            sb.Append(val is string s ? s.ToUpperInvariant() : Convert.ToString(val) ?? "NULL");
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static void SortPartition(
        List<(int Index, object?[] Row)> partition,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        SqlTableDefinition tableDef)
    {
        if (partition.Count <= 1) return;
        var colIndices = new int[tableDef.Columns.Count];
        for (int i = 0; i < colIndices.Length; i++) colIndices[i] = i;
        // Sort the (Index, Row) tuples together so each original row index stays
        // bound to its row. Sorting only the rows (as before) would desync the
        // index from the row whenever storage order != ORDER BY order.
        // Tie-break on the original index keeps peer rows in input order, making
        // ROW_NUMBER deterministic across equal ORDER BY keys.
        var comparer = OrderByExecutor.CreateRowComparer(orderBy, colIndices, tableDef);
        partition.Sort((a, b) =>
        {
            var c = comparer.Compare(a.Row, b.Row);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
    }

    private static bool RowsEqualByOrderColumns(
        object?[] a, object?[] b, IReadOnlyList<SqlOrderByColumn>? orderBy, SqlTableDefinition tableDef)
    {
        if (orderBy == null) return true;
        foreach (var col in orderBy)
        {
            var idx = FindColumnIndex(tableDef, col.ColumnName);
            if (idx < 0) continue;
            var av = idx < a.Length ? a[idx] : null;
            var bv = idx < b.Length ? b[idx] : null;
            if (!EqualsWindowValue(av, bv)) return false;
        }
        return true;
    }

    private static bool EqualsWindowValue(object? x, object? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        if (x is string sx && y is string sy)
            return WalhallaSql.Collation.CollationManager.Equals(sx, sy, null);
        return x.Equals(y);
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
