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
                ? WithResolvedBlobs(encoded => RowCodec.DecodeToArray(encoded, plan.TableDefinition), plan.TableId, plan.TableDefinition)
                : WithResolvedBlobs(encoded => RowCodec.DecodeToPooledArray(encoded, plan.TableDefinition), plan.TableId, plan.TableDefinition)
            : WithResolvedBlobs(encoded => RowCodec.DecodeColumns(encoded, plan.TableDefinition, plan.ProjectionIndices), plan.TableId, plan.TableDefinition, plan.ProjectionIndices);
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
        if (!_paramOrdinals.TryGetValue(name, out var index)
            && !(name.Length > 0 && name[0] != '@' && _paramOrdinals.TryGetValue("@" + name, out index)))
        {
            throw new ArgumentException($"Parameter '{name}' not found.", nameof(name));
        }
        _boundParams[index] = value;
    }

    public void ClearBindings()
    {
        Array.Clear(_boundParams, 0, _boundParams.Length);
    }

    public WalhallaResultSet Execute()
    {
        bool isAggregate = _plan.SelectColumns?.Any(c => c.Aggregate != null || c.WindowFunction != null) == true;

        // PK point-lookup fast path: WHERE PK = @param oder literal (nicht für Aggregate).
        if (!isAggregate && _plan.PkLookupColumnIndex.HasValue)
        {
            object? pkValue = _plan.PkLookupParameterIndex.HasValue
                ? _boundParams[_plan.PkLookupParameterIndex.Value]
                : _plan.PkLookupConstant;
            if (pkValue == null)
                return WalhallaResultSet.Empty(_plan.OutputColumnNames);

            var rowId = Convert.ToInt64(pkValue);
            var encoded = _store.GetRow(_plan.TableId, rowId);
            if (encoded == null)
                return WalhallaResultSet.Empty(_plan.OutputColumnNames);

            var row = RowCodec.DecodeToArray(encoded, _plan.TableDefinition);
            _store.ResolveBlobs(_plan.TableId, row, _plan.TableDefinition);
            var projected = ProjectRow(row, _plan);
            var pkRow = new WalhallaRow(_plan.OutputSchema, projected);
            return WalhallaResultSet.Single(_plan.OutputColumnNames, pkRow);
        }

        // PK range scan path: WHERE PK BETWEEN @min AND @max, PK > @val, etc.
        if (!isAggregate && _plan.PkRange != null)
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

            // Fast streaming path for simple PK-range selects: no ORDER BY, DISTINCT,
            // GROUP BY, aggregates, windows, HAVING, computed columns, LIMIT or OFFSET.
            // Decode directly into the projected output array and build WalhallaRows
            // without intermediate List<object?[]> buffers or ApplyPostProcessing.
            // WhereDelegate erwartet volle Zeilen in Tabellenspalten-Reihenfolge; bei
            // Teilprojektion darf der Stream-Pfad daher nicht den kodierten Where-Check
            // auf dem projizierten Array ausführen.
            bool canStream = _plan.IsStreamable
                && !_plan.Offset.HasValue
                && !_plan.Limit.HasValue
                && (_plan.WhereDelegate == null || _plan.IsFullProjection);

            if (canStream)
            {
                var schema = _plan.OutputSchema;
                var rows = new List<WalhallaRow>();

                RowDecoder decoder = _plan.IsFullProjection
                    ? WithResolvedBlobs(encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition), _plan.TableId, _plan.TableDefinition)
                    : WithResolvedBlobs(encoded =>
                    {
                        var output = new object?[_plan.ProjectionIndices.Length];
                        RowCodec.DecodeColumnsToRowBuffer(encoded, _plan.TableDefinition, output, _plan.DecodeMapping);
                        return output;
                    }, _plan.TableId, _plan.TableDefinition, _plan.ProjectionIndices);

                Func<object?[], bool>? wherePredicate = _plan.WhereDelegate != null
                    ? row => _plan.WhereDelegate(row, _boundParams)
                    : null;

                // Für Streaming ohne LIMIT/OFFSET reicht ein beliebiges Limit.
                _store.ScanRowKeyRange(_plan.TableId, minRowId, maxRowId,
                    decoder, wherePredicate, results: null,
                    onRow: row => rows.Add(new WalhallaRow(schema, row)),
                    limit: int.MaxValue);

                return new WalhallaResultSet(rows, _plan.OutputColumnNames);
            }

            var fullRows = new List<object?[]>();
            var limit = _plan.Limit ?? int.MaxValue;
            _store.ScanRowKeyRange(_plan.TableId, minRowId, maxRowId,
                WithResolvedBlobs(encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition), _plan.TableId, _plan.TableDefinition), null, fullRows, limit: limit);

            if (_plan.WhereDelegate != null)
                fullRows.RemoveAll(r => !_plan.WhereDelegate(r, _boundParams));

            // Paging nachträglich anwenden, falls Scan-Limit die Sortierung/DISTINCT vorher
            // berücksichtigen muss (z.B. ORDER BY + LIMIT).
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
                    var fullRow = WithResolvedBlobs(encoded => RowCodec.DecodeToPooledArray(encoded, _plan.TableDefinition), _plan.TableId, _plan.TableDefinition)(encoded);
                    if (whereDelegate(fullRow, _boundParams))
                        fullRows.Add(fullRow);
                    else
                        RowCodec.ReturnPooledArray(fullRow);
                }
                else
                {
                    fullRows.Add(WithResolvedBlobs(encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition), _plan.TableId, _plan.TableDefinition)(encoded));
                }

                // Bei reinem LIMIT ohne ORDER BY/DISTINCT reicht früherer Abbruch.
                if (_plan.Limit.HasValue && _plan.Offset == null
                    && _plan.OrderByColumns == null && !_plan.IsDistinct
                    && fullRows.Count >= _plan.Limit.Value)
                {
                    break;
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

        // Decode full rows when ORDER BY or a WHERE predicate references columns that are not
        // part of the projected output. Teilprojektion liefert nur die projizierten Spalten,
        // daher kann der kodierte Where-Check nur auf vollen Zeilen ausgeführt werden.
        var needsFullRows = !_plan.IsFullProjection
            && (_plan.OrderByColumns is { Count: > 0 } || _plan.WhereDelegate != null);

        var usesPooledRows = needsFullRows || _decoderUsesPool;

        RowDecoder scanDecoder = needsFullRows || _decoderUsesPool
            ? WithResolvedBlobs(encoded => RowCodec.DecodeToPooledArray(encoded, _plan.TableDefinition), _plan.TableId, _plan.TableDefinition)
            : _decoder;

        // Scan-Limit nur sinnvoll, wenn kein ORDER BY/DISTINCT/Offset: LIMIT wirkt erst
        // nach Sortierung/Dedup.
        var scanLimit = _plan.Limit.HasValue
            && _plan.Offset == null
            && _plan.OrderByColumns == null
            && !_plan.IsDistinct
            ? _plan.Limit.Value
            : int.MaxValue;

        _store.ScanWithPredicateFirst(_plan.TableId, _plan.TableDefinition,
            _plan.PredicateColumnIndices ?? Array.Empty<int>(),
            scanDecoder, predicate, results, scanLimit);

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

        var rows = projected.ConvertAll(r => new WalhallaRow(plan.OutputSchema, r));

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

        // Read base table rows, pushing the base-table WHERE predicate into the scan
        // so rows that do not match are never fully decoded or stored.
        var baseRows = new List<object?[]>();
        RowDecoder baseDecoder = WithResolvedBlobs(
            encoded => RowCodec.DecodeToArray(encoded, joinPlan.BaseTableDef),
            joinPlan.BaseTableId, joinPlan.BaseTableDef);
        var where = _plan.WhereDelegate;
        if (where != null && _plan.PredicateColumnIndices is { Length: > 0 })
        {
            _store.ScanWithPredicateFirst(
                joinPlan.BaseTableId, joinPlan.BaseTableDef,
                _plan.PredicateColumnIndices, baseDecoder,
                r => where(r, _boundParams), baseRows, int.MaxValue);
        }
        else
        {
            _store.ScanWithPredicate(
                joinPlan.BaseTableId, baseDecoder,
                where != null ? r => where(r, _boundParams) : null,
                baseRows, int.MaxValue);
        }

        var accumulated = baseRows;

        foreach (var step in joinPlan.Steps)
        {
            RowDecoder rightDecoder = WithResolvedBlobs(
                encoded => RowCodec.DecodeToArray(encoded, step.TableDef),
                step.TableId, step.TableDef);

            // Index-Join-Pfade brauchen keinen vollständigen Scan der rechten Tabelle.
            List<object?[]> rightRows;
            if (IndexNestedLoopJoin.TryGetIndex(_store, step, out _, out _, out _))
            {
                rightRows = new List<object?[]>();
            }
            else
            {
                rightRows = new List<object?[]>();
                _store.ScanWithPredicate(step.TableId, rightDecoder, null, rightRows, int.MaxValue);
            }

            accumulated = JoinStepExecutor.ExecuteStep(accumulated, rightRows, step, _store, rightDecoder, _boundParams);
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

        var resultRows = projectedRows.ConvertAll(r => new WalhallaRow(_plan.OutputSchema, r));
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
        // Schneller Pfad: COUNT(*) über die gesamte Tabelle ohne Filter/Gruppierung
        // benötigt keine Zeilendekodierung.
        if (_plan.WhereDelegate == null
            && (_plan.GroupByColumns == null || _plan.GroupByColumns.Count == 0)
            && _plan.Having == null
            && _plan.Join == null
            && !_plan.IsDistinct
            && _plan.Limit == null
            && _plan.Offset == null
            && _plan.OutputColumnNames.Length == 1
            && _plan.SelectColumns != null
            && _plan.SelectColumns.Count == 1
            && _plan.SelectColumns[0].Aggregate is { Function: SqlAggregateFunction.Count, Argument: null })
        {
            var count = _store.CountRows(_plan.TableId);
            var fastResult = new object?[_plan.OutputColumnNames.Length];
            fastResult[0] = count;
            return WalhallaResultSet.Single(_plan.OutputColumnNames,
                new WalhallaRow(_plan.OutputSchema, fastResult));
        }

        // Scan all rows with WHERE filter.
        var rows = new List<object?[]>();
        RowDecoder decoder = WithResolvedBlobs(
            encoded => RowCodec.DecodeToArray(encoded, _plan.TableDefinition),
            _plan.TableId, _plan.TableDefinition);

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

        // Paging auf aggregiertem Ergebnis anwenden.
        if (_plan.Offset.HasValue)
            aggregated = aggregated.Skip(_plan.Offset.Value).ToList();
        if (_plan.Limit.HasValue)
            aggregated = aggregated.Take(_plan.Limit.Value).ToList();

        // Build result set.
        var resultRows = aggregated.ConvertAll(r => new WalhallaRow(_plan.OutputSchema, r));
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

    /// <summary>
    /// Wraps a raw row decoder so that any out-of-line <see cref="BlobRef"/u003e values
    /// are resolved to <see cref="PendingBlobValue"/u003e by the sidecar file.
    /// </summary>
    private RowDecoder WithResolvedBlobs(RowDecoder decoder, int tableId, SqlTableDefinition def, int[]? projectionIndices = null) =>
        encoded =>
        {
            var row = decoder(encoded);
            _store.ResolveBlobs(tableId, row, def, projectionIndices);
            return row;
        };

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
