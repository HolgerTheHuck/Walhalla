using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DbUi.Core.Providers;

namespace DbUi.Core.Collections;

/// <summary>
/// Thread-sichere ObservableCollection, die Zeilen fortlaufend aus einem
/// <see cref="IAsyncEnumerable{T}"/> laedt. Sie ist fuer
/// <code>BindingOperations.EnableCollectionSynchronization</code> in WPF
/// vorgesehen, damit der Hintergrund-Loader direkt hinzufuegen kann, ohne
/// jeden Batch auf den UI-Dispatcher zu marshallen.
///
/// Im Gegensatz zur bisherigen Implementierung wird nicht erst der gesamte
/// Reader in eine <code>List</code> gepumpt, bevor das Grid gebunden wird.
/// Stattdessen erscheinen die ersten Zeilen sofort; weitere Seiten werden
/// im Hintergrund nachgeladen, bis der Stream endet oder ein konfigurierbares
/// Limit erreicht ist.
///
/// Der Stream kann optional Spalteninformationen als erstes Element liefern.
/// Das ist notwendig, wenn der Reader erst auf dem Loader-Thread geoefnet
/// wird (z. B. weil der zugrunde liegende Enumerator thread-affine Locks haelt).
/// </summary>
public sealed class StreamingRowCollection : ObservableCollection<object?[]>, IReadOnlyList<object?[]>, IDisposable
{
    private readonly IAsyncEnumerable<(IReadOnlyList<QueryColumn>? Columns, IReadOnlyList<object?[]> Rows)> _source;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loader;
    private readonly long? _maxRows;
    private readonly object _sync = new();
    private long _totalLoaded;
    private bool _disposed;

    /// <summary>
    /// Konstruktor fuer Streams, die nur Zeilen liefern (altes Verhalten).
    /// </summary>
    public StreamingRowCollection(
        IAsyncEnumerable<IReadOnlyList<object?[]>> source,
        long? maxRows = null)
        : this(WrapWithoutColumns(source), maxRows)
    {
    }

    /// <summary>
    /// Konstruktor fuer Streams, die Spalten als erstes Element und danach
    /// Zeilen-Batches liefern.
    /// </summary>
    public StreamingRowCollection(
        IAsyncEnumerable<(IReadOnlyList<QueryColumn>? Columns, IReadOnlyList<object?[]> Rows)> source,
        long? maxRows = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _maxRows = maxRows;
        _loader = Task.Run(() => LoadAsync(_cts.Token));
    }

    /// <summary>
    /// Synchronisationsobjekt, das fuer
    /// <code>BindingOperations.EnableCollectionSynchronization</code> verwendet
    /// werden sollte.
    /// </summary>
    public object SyncRoot => _sync;

    /// <summary>
    /// Anzahl bisher aus dem Stream geladener Zeilen.
    /// </summary>
    public long TotalLoaded => _totalLoaded;

    /// <summary>
    /// True, wenn der Hintergrund-Loader fertig ist (Stream zu Ende oder abgebrochen).
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Vom Stream gelieferte Spalteninformationen. Kann <c>null</c> sein, wenn
    /// der Stream keine Spalten liefert oder sie noch nicht gelesen wurden.
    /// </summary>
    public IReadOnlyList<QueryColumn>? Columns { get; private set; }

    /// <summary>
    /// Wird fuer jede geladene Seite ausgeloest. UI kann damit "X rows loaded"
    /// aktualisieren.
    /// </summary>
    public event EventHandler<BatchLoadedEventArgs>? BatchLoaded;

    /// <summary>
    /// Wird ausgeloest, sobald der Stream Spalteninformationen liefert.
    /// </summary>
    public event EventHandler<ColumnsReadyEventArgs>? ColumnsReady;

    /// <summary>
    /// Optionale Fehlermeldung, falls der Stream mit einer Exception abbricht.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    protected override void InsertItem(int index, object?[] item)
    {
        lock (_sync) base.InsertItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        lock (_sync) base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        lock (_sync) base.ClearItems();
    }

    protected override void SetItem(int index, object?[] item)
    {
        lock (_sync) base.SetItem(index, item);
    }

    protected override void MoveItem(int oldIndex, int newIndex)
    {
        lock (_sync) base.MoveItem(oldIndex, newIndex);
    }

    /// <summary>
    /// Fuegt mehrere Zeilen hinzu. WPFs <c>ListCollectionView</c> unterstuetzt
    /// keine Range-Add-Aktionen (<c>NotifyCollectionChangedAction.Add</c> mit
    /// mehreren Items) – das fuehrt zu "Bereichsaktionen werden nicht unterstuetzt".
    /// Daher wird stattdessen <c>Reset</c> ausgeloest; das DataGrid virtualisiert
    /// dann neu.
    /// <para>
    /// Wird unter dem <code>_sync</code>-Lock aufgerufen.
    /// </para>
    /// </summary>
    private void AddRange(IList<object?[]> rows)
    {
        if (rows.Count == 0) return;

        foreach (var row in rows)
            Items.Add(row);

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in _source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (batch.Columns != null)
                {
                    Columns = batch.Columns;
                    ColumnsReady?.Invoke(this, new ColumnsReadyEventArgs(batch.Columns));
                }

                var rows = batch.Rows;
                if (rows == null || rows.Count == 0)
                    continue;

                var rowList = rows.ToList();
                if (_maxRows.HasValue && _totalLoaded + rowList.Count > _maxRows.Value)
                {
                    var take = (int)(_maxRows.Value - _totalLoaded);
                    if (take > 0)
                        rowList = rowList.Take(take).ToList();
                    else
                        rowList = [];
                }

                if (rowList.Count == 0)
                    continue;

                lock (_sync)
                {
                    AddRange(rowList);
                }

                _totalLoaded += rowList.Count;
                BatchLoaded?.Invoke(this, new BatchLoadedEventArgs(_totalLoaded, false));

                if (_maxRows.HasValue && _totalLoaded >= _maxRows.Value)
                    break;

                // UI-Thread zwischen Batches atmen lassen.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // Normales Abbruchsverhalten.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsComplete = true;
            BatchLoaded?.Invoke(this, new BatchLoadedEventArgs(_totalLoaded, true));
        }
    }

    /// <summary>
    /// Wartet, bis mindestens <paramref name="minCount"/> Zeilen geladen sind
    /// oder der Stream endet. Fuer Preloading/Tests.
    /// </summary>
    public async Task WaitForRowsAsync(int minCount, CancellationToken cancellationToken = default)
    {
        while (_totalLoaded < minCount && !IsComplete && !cancellationToken.IsCancellationRequested)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Blockiert, bis der Rest des Streams geladen ist. Nur fuer Export/Full-Materialisierung.
    /// </summary>
    public void MaterializeRemaining()
    {
        if (!_disposed)
            _loader.Wait();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _loader.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignored */ }
        _cts.Dispose();
    }

    private static async IAsyncEnumerable<(IReadOnlyList<QueryColumn>? Columns, IReadOnlyList<object?[]> Rows)> WrapWithoutColumns(
        IAsyncEnumerable<IReadOnlyList<object?[]>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var rows in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return (null, rows);
    }
}

public sealed class BatchLoadedEventArgs : EventArgs
{
    public long TotalLoaded { get; }
    public bool IsComplete { get; }

    public BatchLoadedEventArgs(long totalLoaded, bool isComplete)
    {
        TotalLoaded = totalLoaded;
        IsComplete = isComplete;
    }
}

public sealed class ColumnsReadyEventArgs : EventArgs
{
    public IReadOnlyList<QueryColumn> Columns { get; }

    public ColumnsReadyEventArgs(IReadOnlyList<QueryColumn> columns)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }
}
