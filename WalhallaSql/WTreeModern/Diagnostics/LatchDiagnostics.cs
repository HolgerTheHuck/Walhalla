using System.Collections.Concurrent;

namespace WTreeModern.Diagnostics;

/// <summary>
/// Thread-sichere Wait-for-Graph Diagnostik für Latches.
/// Trackt wer welche Latches hält und wer auf welche wartet.
/// Erkennt Deadlock-Zyklen sofort (vor dem Timeout).
/// </summary>
public sealed class LatchDiagnostics : ILatchDiagnostics
{
    private readonly ConcurrentDictionary<int, HashSet<long>> _heldByThread = new();
    private readonly ConcurrentDictionary<long, List<(int ThreadId, bool Exclusive)>> _waitingOn = new();
    private readonly object _graphLock = new();

    public void OnWaitStart(long handle, bool exclusive)
    {
        int threadId = Environment.CurrentManagedThreadId;

        lock (_graphLock)
        {
            var waiters = _waitingOn.GetOrAdd(handle, _ => new());
            waiters.Add((threadId, exclusive));

            // Deadlock-Check: Wenn Thread A auf H wartet,
            // prüfe ob Holder von H auf etwas wartet, das A hält.
            if (DetectCycle(threadId, handle))
            {
                waiters.RemoveAll(w => w.ThreadId == threadId);
                throw new WTreeDeadlockException(
                    $"Deadlock detected in Wait-for-Graph: Thread {threadId} waiting on handle {handle}.");
            }
        }
    }

    public void OnWaitEnd(long handle)
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_graphLock)
        {
            if (_waitingOn.TryGetValue(handle, out var list))
                list.RemoveAll(w => w.ThreadId == threadId);
        }
    }

    public void OnAcquired(long handle, bool exclusive)
    {
        int threadId = Environment.CurrentManagedThreadId;
        var held = _heldByThread.GetOrAdd(threadId, _ => new());
        lock (_graphLock)
        {
            held.Add(handle);
        }
    }

    public void OnReleased(long handle, bool exclusive)
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (_heldByThread.TryGetValue(threadId, out var held))
        {
            lock (_graphLock)
            {
                held.Remove(handle);
            }
        }
    }

    /// <summary>
    /// Prüft, ob ein Zyklus entsteht, wenn Thread T auf handle H wartet.
    /// Algorithmus: Wenn ein anderer Thread T2 H hält und auf etwas wartet,
    /// das T hält, dann ist ein Zyklus vorhanden.
    /// </summary>
    private bool DetectCycle(int waitingThread, long waitingOnHandle)
    {
        // Finde alle Threads, die waitingOnHandle halten
        var holders = new List<int>();
        foreach (var (threadId, heldSet) in _heldByThread)
        {
            if (heldSet.Contains(waitingOnHandle) && threadId != waitingThread)
                holders.Add(threadId);
        }

        // Für jeden Holder: prüfe ob er auf etwas wartet, das waitingThread hält
        if (_heldByThread.TryGetValue(waitingThread, out var waitingThreadHeld))
        {
            foreach (var holderThread in holders)
            {
                foreach (var (handle, waiters) in _waitingOn)
                {
                    if (waiters.Any(w => w.ThreadId == holderThread) && waitingThreadHeld.Contains(handle))
                        return true;
                }
            }
        }

        return false;
    }
}
