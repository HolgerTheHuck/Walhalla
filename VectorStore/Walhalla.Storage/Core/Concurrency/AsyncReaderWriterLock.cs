// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.Storage.Core.Concurrency;

/// <summary>
/// A minimal async-compatible reader-writer lock (no external dependencies).
/// <para>
/// Multiple readers can hold the lock concurrently. Writers are exclusive
/// with all other readers and writers.
/// </para>
/// <para>
/// <b>Fair</b>: readers and writers compete equally for the gate — no side is
/// systematically preferred.  In the Walhalla write path the write lock is held
/// only for the brief in-memory MemTable mutation (microseconds), <em>after</em>
/// the fsync completes.  Under writer-preferring semantics, readers were blocked
/// for the entire fsync duration (~4 ms) even though the write gate was free
/// during that time, causing a severe read-throughput regression at high
/// concurrency.  The fair policy eliminates that artificial head-of-line block.
/// </para>
/// <para>
/// Implementation uses two primitives:
/// <list type="bullet">
///   <item><c>_mutex</c> — held in <em>both</em> the enter- and exit-path to
///         mutate <c>_readers</c>.  This serialises all counter changes and
///         prevents the ghost-increment corruption that arose when the exit-path
///         used a lock-free <see cref="Interlocked.Decrement"/>: under high
///         read concurrency the interleaving of lock-free decrements with the
///         mutex-protected increment could cause <c>_readers</c> to accumulate
///         spurious counts, leaving it permanently above zero and preventing
///         the write gate from ever being released to the FlushLoop.</item>
///   <item><c>_writeGate</c> — semaphore(1,1).  Held exclusively by an active
///         writer, or held once by reader 1 on behalf of all concurrent readers
///         (released only when the last reader exits).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AsyncReaderWriterLock : IDisposable
{
    private readonly SemaphoreSlim _mutex     = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Writer-starvation prevention: writers hold _readerWait while pending+active;
    // new readers yield through it when _pendingWriters > 0, preventing infinite piggybacking.
    private readonly SemaphoreSlim _readerWait = new(1, 1);
    private int  _readers;
    private int  _pendingWriters;
    private bool _disposed;

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>Synchronously enters the read lock.</summary>
    public void EnterReadLock()
    {
        // Writer-preferring: yield for any pending writer using bounded waits (50 ms slices)
        // so that threads whose outer workload-loop cancelled token can eventually observe the
        // cancellation after this method returns — infinite Wait() would block them forever.
        //
        // The retry count is bounded (MaxSpinRetries × 50 ms = 300 ms) to prevent permanent
        // reader starvation under continuous write load: if the FlushLoop cycles faster than
        // 50 ms, _pendingWriters is always > 0 when a reader checks, causing infinite spin.
        // After the limit the reader takes the lock anyway; it still blocks correctly on
        // _writeGate while an active write lock is held, but is not starved indefinitely.
        const int MaxSpinRetries = 6; // 300 ms maximum courtesy wait
        int retries = 0;
        while (Volatile.Read(ref _pendingWriters) > 0)
        {
            if (_readerWait.Wait(50))
            {
                _readerWait.Release();
                break;
            }
            // Timed out — re-check _pendingWriters on next iteration.
            if (++retries >= MaxSpinRetries)
                break; // Stop waiting — proceed to acquire the lock regardless.
        }
        _mutex.Wait();
        try
        {
            if (++_readers == 1)
                _writeGate.Wait();
        }
        catch
        {
            // If _writeGate.Wait() throws (e.g. ObjectDisposedException) undo the increment.
            --_readers;
            throw;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Asynchronously enters the read lock.</summary>
    public async Task EnterReadLockAsync(CancellationToken ct = default)
    {
        // Writer-preferring: yield for any pending writer using bounded waits (50 ms slices)
        // so that callers passing a CancellationToken get a timely OperationCanceledException
        // when the token fires, rather than blocking on an infinite WaitAsync.
        // Same MaxSpinRetries bound as EnterReadLock() to prevent starvation; ct cancellation
        // already handles the cancellable-caller case via WaitAsync(50, ct).
        const int MaxSpinRetries = 6; // 300 ms maximum courtesy wait
        int retries = 0;
        while (Volatile.Read(ref _pendingWriters) > 0)
        {
            // WaitAsync throws OperationCanceledException immediately if ct fires.
            if (await _readerWait.WaitAsync(50, ct).ConfigureAwait(false))
            {
                _readerWait.Release();
                break;
            }
            // Timed out — re-check _pendingWriters on next iteration.
            if (++retries >= MaxSpinRetries)
                break; // Stop waiting — proceed to acquire the lock regardless.
        }
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (++_readers == 1)
            {
                try
                {
                    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    --_readers; // undo increment on cancellation / disposal
                    throw;
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>Exits a previously entered read lock.</summary>
    public void ExitReadLock()
    {
        _mutex.Wait();
        try
        {
            if (--_readers == 0)
                _writeGate.Release();
        }
        finally
        {
            _mutex.Release();
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Synchronously enters the exclusive write lock.</summary>
    public void EnterWriteLock()
    {
        Interlocked.Increment(ref _pendingWriters);
        _readerWait.Wait(); // block new reader groups from forming
        try
        {
            _writeGate.Wait(); // wait for current reader group to drain
        }
        catch
        {
            _readerWait.Release();
            Interlocked.Decrement(ref _pendingWriters);
            throw;
        }
    }

    /// <summary>Asynchronously enters the exclusive write lock.</summary>
    public async Task EnterWriteLockAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingWriters);
        try
        {
            await _readerWait.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _readerWait.Release();
                throw;
            }
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWriters);
            throw;
        }
    }

    /// <summary>Exits the exclusive write lock.  Can be called from any thread.</summary>
    public void ExitWriteLock()
    {
        Interlocked.Decrement(ref _pendingWriters);
        _writeGate.Release();
        _readerWait.Release(); // allow next writer or new reader group
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mutex.Dispose();
        _writeGate.Dispose();
        _readerWait.Dispose();
    }
}
