using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.Execution;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql.Execution.Streaming;

/// <summary>
/// Asynchroner Adapter um einen synchronen <see cref="IEnumerable{T}"/>.
/// <para>
/// Jeder <c>MoveNextAsync</c>-Aufruf läuft synchron auf dem aufrufenden Thread ab.
/// Der Consumer muss dafür sorgen, dass die gesamte Enumeration (inkl. Dispose)
/// auf demselben Thread erfolgt, weil die zugrunde liegende Storage-Schicht
/// thread-affine ReaderWriterLockSlim-Leselocks hält.
/// </para>
/// </summary>
internal sealed class SyncEnumerableAsyncAdapter<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    private readonly IEnumerable<T>? _source;
    private IEnumerator<T>? _enumerator;
    private bool _disposed;

    public SyncEnumerableAsyncAdapter(IEnumerable<T> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public SyncEnumerableAsyncAdapter(IEnumerator<T> enumerator)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _source = null;
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (_enumerator != null)
            return this;

        _enumerator = _source!.GetEnumerator();
        return this;
    }

    public T Current
    {
        get
        {
            if (_enumerator == null)
                throw new InvalidOperationException("Keine aktuelle Zeile.");
            return _enumerator.Current;
        }
    }

    public ValueTask<bool> MoveNextAsync()
    {
        if (_disposed || _enumerator == null)
            return new ValueTask<bool>(false);

        return new ValueTask<bool>(_enumerator.MoveNext());
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _enumerator?.Dispose();
            _enumerator = null;
        }
        return default;
    }
}

/// <summary>
/// Basis-Scan-Operator: liefert Zeilen aus der Storage-Schicht.
/// </summary>
internal sealed class ScanOperator : IStreamingOperator
{
    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var plan = context.Plan;
        Func<object?[], bool>? predicate = null;
        if (plan.WhereDelegate != null)
        {
            var where = plan.WhereDelegate;
            var parameters = context.Parameters;
            predicate = row => where(row, parameters);
        }

        IEnumerable<object?[]> source;
        if (plan.PkRange != null)
        {
            var range = plan.PkRange;
            long minRowId = range.HasLiteralBounds ? range.LiteralMin : long.MinValue;
            long maxRowId = range.HasLiteralBounds ? range.LiteralMax : long.MaxValue;
            if (!range.MinInclusive) minRowId++;
            if (!range.MaxInclusive) maxRowId--;

            source = context.Store.ScanRowKeyRangeLazy(plan.TableId, minRowId, maxRowId,
                plan.TableDefinition, predicate);
        }
        else
        {
            source = context.Store.ScanWithPredicateLazy(plan.TableId, plan.TableDefinition, predicate);
        }

        return source.Select(row => new StreamingRow(row));
    }
}

/// <summary>
/// Filter-Operator: wendet die WHERE-Bedingung auf einen Strom an.
/// </summary>
internal sealed class FilterOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;
    private readonly Func<object?[], bool> _predicate;

    public FilterOperator(IStreamingOperator source, Func<object?[], bool> predicate)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        return _source.Execute(context).Where(row => _predicate(row.Values));
    }
}

/// <summary>
/// Projektions-Operator: bildet Eingabespalten auf Ausgabespalten ab und wertet berechnete Spalten aus.
/// </summary>
internal sealed class ProjectOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;
    private readonly int[] _projectionIndices;
    private readonly Func<object?[], object?>?[]? _computedProjections;
    private readonly int _outputLength;

    public ProjectOperator(IStreamingOperator source, int[] projectionIndices, Func<object?[], object?>?[]? computedProjections)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _projectionIndices = projectionIndices ?? throw new ArgumentNullException(nameof(projectionIndices));
        _computedProjections = computedProjections;
        _outputLength = projectionIndices.Length;
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var comp = _computedProjections;
        foreach (var row in _source.Execute(context))
        {
            var source = row.Values;
            var result = new object?[_outputLength];
            for (int i = 0; i < _outputLength; i++)
            {
                if (comp != null && comp[i] != null)
                    result[i] = comp[i]!(source);
                else
                    result[i] = source[_projectionIndices[i]];
            }
            yield return new StreamingRow(result);
        }
    }
}

/// <summary>
/// LIMIT/OFFSET-Operator: schneidet einen Teil des Ergebnisstroms heraus.
/// </summary>
internal sealed class LimitOffsetOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;
    private readonly int _offset;
    private readonly int _limit;

    public LimitOffsetOperator(IStreamingOperator source, int? offset, int? limit)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _offset = offset ?? 0;
        _limit = limit ?? int.MaxValue;
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        int skipped = 0;
        int yielded = 0;
        foreach (var row in _source.Execute(context))
        {
            if (skipped < _offset)
            {
                skipped++;
                continue;
            }

            if (yielded >= _limit)
                yield break;

            yielded++;
            yield return row;
        }
    }
}

/// <summary>
/// DISTINCT-Operator: hält ein Hash-Set bereits ausgegebener Zeilen und filtert Duplikate.
/// Die Eingabezeile wird kopiert, weil der unterliegende Storage-Puffer wiederverwendet wird.
/// </summary>
internal sealed class DistinctOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;

    public DistinctOperator(IStreamingOperator source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var seen = new HashSet<object?[]>(new StreamingRowEqualityComparer());
        foreach (var row in _source.Execute(context))
        {
            var copy = row.Values.ToArray();
            if (seen.Add(copy))
                yield return new StreamingRow(copy);
        }
    }
}

/// <summary>
/// ORDER-BY-Operator: materialisiert alle Zeilen, sortiert sie und gibt sie dann gestreamt aus.
/// Nötig, weil globale Sortierung den gesamten Eingabestrom sehen muss.
/// </summary>
internal sealed class OrderByOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;
    private readonly Comparison<object?[]> _comparer;

    public OrderByOperator(IStreamingOperator source, Comparison<object?[]> comparer)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var rows = new List<object?[]>();
        foreach (var row in _source.Execute(context))
            rows.Add(row.Values.ToArray());

        rows.Sort(_comparer);

        foreach (var r in rows)
            yield return new StreamingRow(r);
    }
}

/// <summary>
/// Aggregat-Operator: GROUP BY, Aggregate und HAVING in einem Durchlauf.
/// Die Eingabe wird in eine Liste kopiert und dann mit dem vorhandenen
/// <see cref="AggregateExecutor"/> verarbeitet; das Ergebnis wird danach Zeile für Zeile ausgegeben.
/// </summary>
internal sealed class AggregateOperator : IStreamingOperator
{
    private readonly IStreamingOperator _source;
    private readonly SqlSelectStatement _select;
    private readonly SqlTableDefinition _tableDef;
    private readonly string[] _outputNames;

    public AggregateOperator(
        IStreamingOperator source,
        SqlSelectStatement select,
        SqlTableDefinition tableDef,
        string[] outputNames)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _select = select ?? throw new ArgumentNullException(nameof(select));
        _tableDef = tableDef ?? throw new ArgumentNullException(nameof(tableDef));
        _outputNames = outputNames ?? throw new ArgumentNullException(nameof(outputNames));
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var rows = new List<object?[]>();
        foreach (var row in _source.Execute(context))
            rows.Add(row.Values.ToArray());

        var aggregated = AggregateExecutor.ExecuteGroupBy(
            rows, _select.GroupByColumns, _select.Columns, _tableDef, _outputNames);
        aggregated = AggregateExecutor.ApplyHaving(aggregated, _select.Having, _outputNames);

        foreach (var r in aggregated)
            yield return new StreamingRow(r);
    }
}

/// <summary>
/// Equality-Comparer für <c>object?[]</c> inklusive Collation-Unterstützung für Strings.
/// </summary>
internal sealed class StreamingRowEqualityComparer : IEqualityComparer<object?[]>
{
    public bool Equals(object?[]? x, object?[]? y)
    {
        if (x == null || y == null) return false;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
        {
            if (!EqualsValue(x[i], y[i])) return false;
        }
        return true;
    }

    public int GetHashCode(object?[] obj)
    {
        var hash = new HashCode();
        foreach (var v in obj)
        {
            if (v is string s)
                hash.Add(WalhallaSql.Collation.CollationManager.GetHashCode(s, null));
            else
                hash.Add(v);
        }
        return hash.ToHashCode();
    }

    private static bool EqualsValue(object? x, object? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        if (x is string sx && y is string sy)
            return WalhallaSql.Collation.CollationManager.Equals(sx, sy, null);
        return x.Equals(y);
    }
}
