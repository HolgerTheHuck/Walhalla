using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DbUi.Core.Collections;

/// <summary>
/// Thread-sichere ObservableCollection, die Zeilen fortlaufend aus einem
/// <see cref="IAsyncEnumerable{T}"/u003e laedt. Sie ist fuer
/// <code>BindingOperations.EnableCollectionSynchronization</code> in WPF
/// vorgesehen, damit der Hintergrund-Loader direkt hinzufuegen kann, ohne
/// jeden Batch auf den UI-Dispatcher zu marshallen.
///
/// Im Gegensatz zur bisherigen Implementierung wird nicht erst der gesamte
/// Reader in eine <code>List</code> gepumpt, bevor das Grid gebunden wird.
/// Stattdessen erscheinen die ersten Zeilen sofort; weitere Seiten werden
/// im Hintergrund nachgeladen, bis der Stream endet oder ein konfigurierbares
/// Limit erreicht ist.
/// </summary>
public sealed class StreamingRowCollection : ObservableCollection<object?[]>, IReadOnlyList<object?[]>, IDisposable
{
    private readonly IAsyncEnumerable<IReadOnlyList<object?[]>> _source;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loader;
    private readonly long? _maxRows;
    private readonly object _sync = new();
    private long _totalLoaded;
    private bool _disposed;

    public StreamingRowCollection(
        IAsyncEnumerable<IReadOnlyList<object?[]>> source,
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
    /// Wird fuer jede geladene Seite ausgeloest. UI kann damit "X rows loaded"
    /// aktualisieren.
    /// </summary>
    public event EventHandler<BatchLoadedEventArgs>? BatchLoaded;

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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in _source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (batch == null || batch.Count == 0)
                    continue;

                var rows = batch.ToList();
                if (_maxRows.HasValue && _totalLoaded + rows.Count > _maxRows.Value)
                {
                    var take = (int)(_maxRows.Value - _totalLoaded);
                    if (take > 0)
                        rows = rows.Take(take).ToList();
                    else
                        rows = [];
                }

                if (rows.Count == 0)
                    continue;

                lock (_sync)
                {
                    foreach (var row in rows)
                        Add(row);
                }

                _totalLoaded += rows.Count;
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
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsComplete = true;
            BatchLoaded?.Invoke(this, new BatchLoadedEventArgs(_totalLoaded, true));
        }
    }

    /// <summary>
    /// Wartet, bis mindestens <paramref name="minCount"/u003e Zeilen geladen sind
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
