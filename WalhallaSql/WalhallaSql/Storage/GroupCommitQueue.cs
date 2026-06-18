using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WalhallaSql.Core;

namespace WalhallaSql.Storage;

internal sealed class GroupCommitQueue : IDisposable
{
    private readonly Channel<PendingCommit> _channel;
    private readonly WalLog _walLog;
    private readonly CancellationTokenSource _cts;
    private readonly Task _flushLoop;
    private readonly int _coalesceMs;

    public GroupCommitQueue(WalLog walLog, int coalesceMs = 0)
    {
        _walLog = walLog ?? throw new ArgumentNullException(nameof(walLog));
        _coalesceMs = coalesceMs;
        _channel = Channel.CreateUnbounded<PendingCommit>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });
        _cts = new CancellationTokenSource();
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public void Enqueue(long transactionId, IReadOnlyList<WalOperation> operations)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new PendingCommit(transactionId, operations, tcs)))
        {
            // Channel is closed — flush loop is shutting down.
            // Fall back to direct synchronous WAL append so the caller doesn't lose data.
            _walLog.AppendBatch(transactionId, operations);
        }
    }

    public ValueTask EnqueueAsync(long transactionId, IReadOnlyList<WalOperation> operations,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new PendingCommit(transactionId, operations, tcs)))
        {
            // Channel is closed — flush loop is shutting down.
            // Fall back to direct synchronous WAL append.
            _walLog.AppendBatch(transactionId, operations);
            return default;
        }
        return new ValueTask(tcs.Task);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var batch = new List<PendingCommit>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for the first item or cancellation.
                var first = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                batch.Clear();
                batch.Add(first);

                // Coalesce: drain any additional items that arrive within _coalesceMs.
                if (_coalesceMs > 0)
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_coalesceMs));
                    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        while (_channel.Reader.TryRead(out var next))
                            batch.Add(next);
                        break; // only one extra window per batch
                    }
                }
                else
                {
                    // Drain everything currently waiting without extra delay.
                    while (_channel.Reader.TryRead(out var next))
                        batch.Add(next);
                }

                if (batch.Count == 0) continue;

                // Build grouped transaction list and flush to WAL.
                var group = new List<(long, IReadOnlyList<WalOperation>)>(batch.Count);
                foreach (var c in batch)
                    group.Add((c.TransactionId, c.Operations));

                await _walLog.AppendGroupAsync(group, ct).ConfigureAwait(false);

                // Signal all waiters.
                foreach (var c in batch)
                    c.CompletionSource.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // On WAL failure, fail all pending items in the batch.
                foreach (var c in batch)
                    c.CompletionSource.TrySetException(ex);
            }
        }

        // Drain remaining on shutdown.
        while (_channel.Reader.TryRead(out var remaining))
            remaining.CompletionSource.TrySetCanceled();
    }

    /// <summary>Blocks until all currently-enqueued commits have been flushed to the WAL.</summary>
    public void FlushAndWait()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(new PendingCommit(-1, Array.Empty<WalOperation>(), tcs));
        tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Wait for the flush loop to finish draining before completing the writer.
        // Otherwise a concurrent Enqueue() can hit the closed channel while the
        // loop is still draining remaining items on shutdown.
        try { _flushLoop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* expected */ }
        catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { /* expected */ }
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }

    private sealed class PendingCommit
    {
        public readonly long TransactionId;
        public readonly IReadOnlyList<WalOperation> Operations;
        public readonly TaskCompletionSource<bool> CompletionSource;

        public PendingCommit(long transactionId, IReadOnlyList<WalOperation> operations,
            TaskCompletionSource<bool> completionSource)
        {
            TransactionId = transactionId;
            Operations = operations;
            CompletionSource = completionSource;
        }
    }
}
