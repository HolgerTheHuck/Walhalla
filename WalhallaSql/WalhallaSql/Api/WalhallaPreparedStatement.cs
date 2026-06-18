using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Collation;
using WalhallaSql.Execution;
using WalhallaSql.Execution.Join;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql;

public sealed class WalhallaPreparedStatement
{
    private readonly CompiledPlan _plan;
    private readonly object?[] _boundParams;
    private readonly IReadOnlyDictionary<string, int> _paramOrdinals;
    private readonly TableStore _store;
    private readonly RowDecoder _decoder;
    private readonly bool _decoderUsesPool;

    internal WalhallaPreparedStatement(
        CompiledPlan plan,
        IReadOnlyDictionary<string, int> paramOrdinals,
        TableStore store)
    {
        _plan = plan;
        _boundParams = new object?[plan.ParameterCount];
        _paramOrdinals = paramOrdinals;
        _store = store;

        // Only pool when projection will make copies. Full projection reuses
        // the decoded array as the result, so pooling would be a leak.
        _decoder = plan.IsFullProjection || plan.ComputedProjections != null
            ? plan.IsFullProjection && plan.ComputedProjections == null
                ? encoded => RowCodec.DecodeToArray(encoded, plan.TableDefinition)
                : encoded => RowCodec.DecodeToPooledArray(encoded, plan.TableDefinition)
            : encoded => RowCodec.DecodeColumns(encoded, plan.TableDefinition, plan.ProjectionIndices);
        _decoderUsesPool = plan.ComputedProjections != null;
    }

    public void Bind(int index, object? value)
    {
        if (index < 0 || index >= _boundParams.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        _boundParams[index] = value;
    }

    public void Bind(string name, object? value)
    {
        if (!_paramOrdinals.TryGetValue(name, out var index))
            throw new ArgumentException($"Parameter '{name}' not found.", nameof(name));
        _boundParams[index] = value;
    }

    public void ClearBindings()
    {
        Array.Clear(_boundParams, 0, _boundParams.Length);
    }

    public WalhallaResultSet Execute()
    {
        // PK point-lookup fast path: WHERE PK = @param
        if (_plan.PkLookupParameterIndex.HasValue && _plan.PkLookupColumnIndex.HasValue)
        {
            var pkValue = _boundParams[_plan.PkLookupParameterIndex.Value];
            if (pkValue == null)
                return new WalhallaResultSet(Array.Empty<WalhallaRow>(), _plan.OutputColumnNames);

            var rowId = Convert.ToInt64(pkValue);
            var encoded = _store.GetRow(_plan.TableId, rowId);
            if (encoded == null)
                return new WalhallaResultSet(Array.Empty<WalhallaRow>(), _plan.OutputColumnNames);

            var row = RowCodec.DecodeToArray(encoded, _plan.TableDefinition);
            var projected = ProjectRow(row, _plan);
            var pkSchema = new ColumnSchema(_plan.OutputColumnNames);
            var pkRow = new WalhallaRow(pkSchema, projected);
            return new WalhallaResultSet(new[] { pkRow }, _plan.OutputColumnNames);
        }

        // PK range scan path: WHERE PK BETWEEN @min AND @max, PK > @val, etc.
        if (_plan.PkRange != null)
        {
            var range = _plan.PkRange;
            long minRowId = range.MinParameterIndex >= 0
                ? Convert.ToInt64(_boundParams[range.MinParameterIndex])
                : range.HasLiteralBounds ? range.LiteralMin : long.MinValue;
            long maxRowId = range.MaxParameterIndex >= 0
                ? Convert.ToInt64(_boundParams[range.MaxParameterIndex])
                : range.HasLiteralBounds ? range.LiteralMax : long.MaxValue;

            if (!range.MinInclusive) minRowId++;
            if (!range.MaxInclusive) maxRowId--;

            var fullRows = new List<object?[]>();
            _store.ScanRowKeyRange(_plan.TableId, minRowId, maxRowId,
                encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition), null, fullRows);

            if (_plan.WhereDelegate != null)
                fullRows.RemoveAll(r => !_plan.WhereDelegate(r, _boundParams));

            return ApplyPostProcessing(fullRows, _plan);
        }

        // Index scan path: secondary index range scan → table lookup.
        if (_plan.SelectedIndex != null)
        {
            var sel = _plan.SelectedIndex;
            var sargablePredicates = sel.MatchedPredicates;

            // Build range bounds from bound parameters.
            var (startKey, endKey, startInclusive, endInclusive) =
                IndexKeyCodec.BuildRangeBounds(sargablePredicates, sel.IndexKeyTypes, _boundParams);

            // Execute index scan.
            var indexKeys = _store.ScanIndex(
                sel.IndexId, startKey, endKey, startInclusive, endInclusive);

            var fullRows = new List<object?[]>();
            var whereDelegate = _plan.WhereDelegate;

            foreach (var (tid, rowId) in indexKeys)
            {
                var encoded = _store.GetRow(tid, rowId);
                if (encoded == null) continue;

                if (whereDelegate != null)
                {
                    var fullRow = RowCodec.DecodeToPooledArray(encoded, _plan.TableDefinition);
                    if (whereDelegate(fullRow, _boundParams))
                        fullRows.Add(fullRow);
                    else
                        RowCodec.ReturnPooledArray(fullRow);
                }
                else
                {
                    fullRows.Add(RowCodec.DecodeToArray(encoded, _plan.TableDefinition));
                }
            }

            return ApplyPostProcessing(fullRows, _plan, usesPooledRows: whereDelegate != null);
        }

        // JOIN execution path for prepared statements.
        if (_plan.Join != null)
            return ExecuteJoin();

        // GROUP BY / aggregate execution path for prepared statements.
        bool hasAggregates = _plan.SelectColumns?.Any(c => c.Aggregate != null && c.WindowFunction == null) ?? false;
        if (_plan.GroupByColumns is { Count: > 0 } || hasAggregates)
            return ExecuteAggregate();

        var results = new List<object?[]>();
        Func<object?[], bool>? predicate = null;

        if (_plan.WhereDelegate != null)
        {
            var where = _plan.WhereDelegate;
            var bound = _boundParams;
            predicate = row => where(row, bound);
        }

        // When ORDER BY is present, decode full rows so ApplyPostProcessing can find ORDER BY columns.
        var needsFullRows = _plan.OrderByColumns is { Count: > 0 } && !_plan.IsFullProjection;
        var scanDecoder = needsFullRows
            ? encoded => RowCodec.DecodeToPooledArray(encoded, _plan.TableDefinition)
            : _decoder;

        var usesPooledRows = needsFullRows || _decoderUsesPool;

        _store.ScanWithPredicateFirst(_plan.TableId, _plan.TableDefinition,
            _plan.PredicateColumnIndices ?? Array.Empty<int>(),
            scanDecoder, predicate, results, int.MaxValue);

        return ApplyPostProcessing(results, _plan, usesPooledRows);
    }

    private WalhallaResultSet ApplyPostProcessing(List<object?[]> fullRows, CompiledPlan plan, bool usesPooledRows = false)
    {
        // Apply ORDER BY on full rows before projection.
        if (_plan.OrderByColumns is { Count: > 0 } orderBy)
        {
            var colIndices = new int[plan.TableDefinition.Columns.Count];
            for (int i = 0; i < colIndices.Length; i++) colIndices[i] = i;
            OrderByExecutor.SortInPlace(fullRows, orderBy, colIndices, plan.TableDefinition);
        }

        // Project rows.
        var projected = plan.IsFullProjection && plan.ComputedProjections == null
            ? fullRows
            : fullRows.ConvertAll(r => ProjectRow(r, plan));

        // Apply DISTINCT after projection.
        if (_plan.IsDistinct)
        {
            var seen = new HashSet<RowKey>(new RowKeyComparer());
            projected = projected.Where(r => seen.Add(new RowKey(r))).ToList();
        }

        // Apply paging.
        if (_plan.Offset.HasValue)
            projected = projected.Skip(_plan.Offset.Value).ToList();
        if (_plan.Limit.HasValue)
            projected = projected.Take(_plan.Limit.Value).ToList();

        var schema = new ColumnSchema(plan.OutputColumnNames);
        var rows = projected.ConvertAll(r => new WalhallaRow(schema, r));

        // Return rented arrays to pool when projection made copies.
        if (usesPooledRows && !ReferenceEquals(projected, fullRows))
        {
            foreach (var row in fullRows)
                RowCodec.ReturnPooledArray(row);
        }

        return new WalhallaResultSet(rows, plan.OutputColumnNames);
    }

    private WalhallaResultSet ExecuteJoin()
    {
        var joinPlan = _plan.Join!;

        // Read base table rows.
        var baseRows = new List<object?[]>();
        RowDecoder baseDecoder = encoded => RowCodec.DecodeToArray(encoded, joinPlan.BaseTableDef);
        _store.ScanWithPredicate(joinPlan.BaseTableId, baseDecoder, null, baseRows, int.MaxValue);

        // Apply base-table WHERE if present.
        var where = _plan.WhereDelegate;
        if (where != null)
            baseRows.RemoveAll(r => !where(r, _boundParams));

        var accumulated = baseRows;

        foreach (var step in joinPlan.Steps)
        {
            var rightRows = new List<object?[]>();
            RowDecoder rightDecoder = encoded => RowCodec.DecodeToArray(encoded, step.TableDef);
            _store.ScanWithPredicate(step.TableId, rightDecoder, null, rightRows, int.MaxValue);

            accumulated = JoinStepExecutor.ExecuteStep(accumulated, rightRows, step, _boundParams);
        }

        // Apply ORDER BY on combined rows.
        if (_plan.OrderByColumns is { Count: > 0 } orderBy)
        {
            SortJoinRows(accumulated, orderBy, joinPlan.ProjectionIndices, _plan.OutputColumnNames);
        }

        // Build projection.
        var projectedRows = new List<object?[]>();
        foreach (var row in accumulated)
        {
            var projected = new object?[_plan.OutputColumnNames.Length];
            for (int i = 0; i < _plan.OutputColumnNames.Length; i++)
            {
                var colIdx = joinPlan.ProjectionIndices[i];
                projected[i] = colIdx < row.Length ? row[colIdx] : null;
            }
            projectedRows.Add(projected);
        }

        // Apply DISTINCT.
        if (_plan.IsDistinct)
        {
            var seen = new HashSet<RowKey>(new RowKeyComparer());
            projectedRows = projectedRows.Where(r => seen.Add(new RowKey(r))).ToList();
        }

        // Apply paging.
        if (_plan.Offset.HasValue)
            projectedRows = projectedRows.Skip(_plan.Offset.Value).ToList();
        if (_plan.Limit.HasValue)
            projectedRows = projectedRows.Take(_plan.Limit.Value).ToList();

        var schema = new ColumnSchema(_plan.OutputColumnNames);
        var resultRows = projectedRows.ConvertAll(r => new WalhallaRow(schema, r));
        return new WalhallaResultSet(resultRows, _plan.OutputColumnNames);
    }

    private static void SortJoinRows(
        List<object?[]> rows,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        int[] projectionIndices,
        string[] outputNames)
    {
        if (rows.Count <= 1) return;

        var colMap = new (int Index, bool Descending)[orderBy.Count];
        for (int i = 0; i < orderBy.Count; i++)
        {
            var colIdx = -1;
            for (int j = 0; j < outputNames.Length; j++)
            {
                if (string.Equals(outputNames[j], orderBy[i].ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    colIdx = projectionIndices[j];
                    break;
                }
            }
            colMap[i] = (colIdx, orderBy[i].Descending);
        }

        rows.Sort((a, b) =>
        {
            for (int i = 0; i < colMap.Length; i++)
            {
                var (colIdx, descending) = colMap[i];
                if (colIdx < 0) continue;
                var av = colIdx < a.Length ? a[colIdx] : null;
                var bv = colIdx < b.Length ? b[colIdx] : null;
                var cmp = CompareOrderValues(av, bv);
                if (cmp != 0) return descending ? -cmp : cmp;
            }
            return 0;
        });
    }

    private static int CompareOrderValues(object? left, object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        if (left is long l && right is long r) return l.CompareTo(r);
        if (left is int il && right is int ir) return il.CompareTo(ir);
        if (left is string ls && right is string rs) return CollationManager.Compare(ls, rs, null);
        if (left is double ld && right is double rd) return ld.CompareTo(rd);
        if (left is IComparable lc && right.GetType() == left.GetType()) return lc.CompareTo(right);
        var sLeft = Convert.ToString(left) ?? string.Empty;
        var sRight = Convert.ToString(right) ?? string.Empty;
        return CollationManager.Compare(sLeft, sRight, null);
    }

    private WalhallaResultSet ExecuteAggregate()
    {
        // Scan all rows with WHERE filter.
        var rows = new List<object?[]>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition);

        Func<object?[], bool>? predicate = null;
        if (_plan.WhereDelegate != null)
        {
            var where = _plan.WhereDelegate;
            var bound = _boundParams;
            predicate = row => where(row, bound);
        }

        _store.ScanWithPredicateFirst(_plan.TableId, _plan.TableDefinition,
            _plan.PredicateColumnIndices ?? Array.Empty<int>(),
            decoder, predicate, rows, int.MaxValue);

        // Execute GROUP BY with aggregates.
        var aggregated = AggregateExecutor.ExecuteGroupBy(
            rows, _plan.GroupByColumns, _plan.SelectColumns!,
            _plan.TableDefinition, _plan.OutputColumnNames);

        // Apply HAVING filter.
        aggregated = AggregateExecutor.ApplyHaving(aggregated, _plan.Having, _plan.OutputColumnNames);

        // Build result set.
        var schema = new ColumnSchema(_plan.OutputColumnNames);
        var resultRows = aggregated.ConvertAll(r => new WalhallaRow(schema, r));
        return new WalhallaResultSet(resultRows, _plan.OutputColumnNames);
    }

    private sealed class RowKey
    {
        internal readonly object?[] _values;
        public RowKey(object?[] values) => _values = values;
    }

    private sealed class RowKeyComparer : IEqualityComparer<RowKey>
    {
        public bool Equals(RowKey? x, RowKey? y)
        {
            if (x == null || y == null) return false;
            var a = x._values; var b = y._values;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!EqualsValue(a[i], b[i])) return false;
            return true;
        }

        public int GetHashCode(RowKey obj)
        {
            var hash = new HashCode();
            foreach (var v in obj._values) hash.Add(v);
            return hash.ToHashCode();
        }

        private static bool EqualsValue(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x is string sx && y is string sy)
                return CollationManager.Equals(sx, sy, null);
            return x.Equals(y);
        }
    }

    private static object?[] ProjectRow(object?[] row, CompiledPlan plan)
    {
        if (plan.IsFullProjection && plan.ComputedProjections == null) return row;
        var result = new object?[plan.ProjectionIndices.Length];
        var comp = plan.ComputedProjections;
        for (int i = 0; i < plan.ProjectionIndices.Length; i++)
        {
            if (comp != null && comp[i] != null)
                result[i] = comp[i]!(row);
            else
                result[i] = row[plan.ProjectionIndices[i]];
        }
        return result;
    }

    internal object?[] GetBoundParameters() => _boundParams;
    internal CompiledPlan GetPlan() => _plan;
}
