using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
