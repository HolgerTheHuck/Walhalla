using System.Threading;
using WTreeModern.Diagnostics;

namespace WTreeModern.Tree;

/// <summary>
/// Hintergrund-Thread, der periodisch versionierte Blattknoten aufräumt.
/// Entfernt alte Versionen, die älter als der älteste aktive Snapshot sind.
/// </summary>
internal sealed class BackgroundGC<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly WTree<TKey, TValue> _tree;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    public BackgroundGC(WTree<TKey, TValue> tree, ILogger? logger = null)
    {
        _tree = tree;
        _logger = logger ?? NoOpLogger.Instance;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"WTree-GC-{typeof(TKey).Name}-{typeof(TValue).Name}"
        };
        _thread.Start();
    }

    private void Run()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(Interval);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_cts.Token.IsCancellationRequested)
                break;

            try
            {
                _tree.PruneAllCachedLeaves();
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "BackgroundGC prune failed.", ex);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            bool joined = _thread.Join(TimeSpan.FromSeconds(5));
            if (!joined)
                _logger.Log(LogLevel.Warning, "BackgroundGC thread did not exit within timeout.");
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "BackgroundGC thread join failed.", ex);
        }
        _cts.Dispose();
    }
}
