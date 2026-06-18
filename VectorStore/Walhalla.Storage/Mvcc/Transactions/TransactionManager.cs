// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Walhalla.Storage.Mvcc.Transactions;

/// <summary>Globaler Transaktionskoordinator.
/// Verwaltet Sequenznummern, aktive Snapshots und Konflikterkennung.</summary>
public sealed class TransactionManager
{
    private ulong _nextTxId = 1;
    private ulong _globalSequence = 1;

    private readonly object _lock = new();
    private readonly HashSet<ulong> _activeSnapshots = [];

    // Write-Lock-Tabelle: Key -> letzte committed Sequence
    private readonly ConcurrentDictionary<object, ulong> _writeLockTable;

    // Read-Lock-Tabelle für SSI: Key -> aktive Serializable-Leser (StartSequences)
    private readonly ConcurrentDictionary<object, List<ReadLockEntry>> _readLockTable;

    public TransactionManager(IEqualityComparer<object>? keyComparer = null)
    {
        var comparer = keyComparer ?? EqualityComparer<object>.Default;
        _writeLockTable = new ConcurrentDictionary<object, ulong>(comparer);
        _readLockTable = new ConcurrentDictionary<object, List<ReadLockEntry>>(comparer);
    }

    /// <summary>
    /// Globaler Lock für die Commit-Phase von Serializable-Transaktionen.
    /// Serialisiert SSI-Konfliktprüfung und Commit-Registrierung atomar.
    /// </summary>
    public object SerializableCommitLock { get; } = new();

    /// <summary>Erzeugt eine neue globale Commit-Sequenznummer.</summary>
    public ulong AcquireCommitSequence()
    {
        return Interlocked.Increment(ref _globalSequence);
    }

    /// <summary>
    /// Erhöht <see cref="CurrentSequence"/> auf mindestens <paramref name="sequence"/>,
    /// falls diese größer ist. Wird nach Recovery verwendet.
    /// </summary>
    public void AdvanceTo(ulong sequence)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _globalSequence);
            if (current >= sequence) return;
            if (Interlocked.CompareExchange(ref _globalSequence, sequence, current) == current)
                return;
        }
    }

    /// <summary>Erzeugt eine neue TxId.</summary>
    public ulong AcquireTxId()
    {
        return Interlocked.Increment(ref _nextTxId);
    }

    /// <summary>Reserviert einen eindeutigen Snapshot für eine neue Transaktion.</summary>
    public ulong AcquireSnapshot()
    {
        ulong seq = Interlocked.Increment(ref _globalSequence);
        lock (_lock)
        {
            _activeSnapshots.Add(seq);
        }
        return seq;
    }

    /// <summary>Gibt einen Snapshot frei.</summary>
    public void ReleaseSnapshot(ulong snapshotSeq)
    {
        lock (_lock)
        {
            _activeSnapshots.Remove(snapshotSeq);
        }
    }

    /// <summary>Gibt die älteste noch aktive Snapshot-Sequenz zurück.</summary>
    public ulong OldestActiveSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _activeSnapshots.Count == 0
                    ? Interlocked.Read(ref _globalSequence)
                    : _activeSnapshots.Min();
            }
        }
    }

    /// <summary>Aktuelle globale Sequenz.</summary>
    public ulong CurrentSequence => Interlocked.Read(ref _globalSequence);

    // ── Write-Write-Konflikterkennung ─────────────────────────────────────────

    /// <summary>
    /// Prüft, ob ein Key seit <paramref name="startSequence"/> von einer
    /// anderen Transaktion committed wurde.
    /// </summary>
    public bool HasWriteConflict(object key, ulong startSequence)
    {
        if (_writeLockTable.TryGetValue(key, out var lastSeq))
            return lastSeq > startSequence;
        return false;
    }

    /// <summary>Registriert einen committed Write für die Konflikterkennung.</summary>
    public void RegisterCommitted(object key, ulong sequence)
    {
        _writeLockTable[key] = sequence;
    }

    // ── SSI Read-Write-Konflikterkennung ─────────────────────────────────────

    public void RegisterRead(object key, ulong startSeq)
    {
        _readLockTable.AddOrUpdate(key,
            _ => new List<ReadLockEntry> { new(startSeq, false) },
            (_, list) => { lock (list) { list.Add(new ReadLockEntry(startSeq, false)); } return list; });
    }

    /// <summary>
    /// Markiert alle Read-Locks der angegebenen Transaktion als committed.
    /// Wird innerhalb von <see cref="SerializableCommitLock"/> aufgerufen.
    /// </summary>
    public void MarkReadCommitted(ulong startSeq)
    {
        foreach (var list in _readLockTable.Values)
        {
            lock (list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].StartSeq == startSeq)
                        list[i] = new ReadLockEntry(startSeq, true);
                }
            }
        }
    }

    public void UnregisterRead(object key, ulong startSeq)
    {
        if (_readLockTable.TryGetValue(key, out var list))
        {
            lock (list) { list.RemoveAll(e => e.StartSeq == startSeq); }
            if (list.Count == 0)
                _readLockTable.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Prüft, ob einer der Keys von einer anderen *committed* Serializable-Transaktion
    /// gelesen wurde (Read-Write-Konflikt für SSI). Nur committede Leser zählen,
    /// damit nicht beide konkurrierenden Transaktionen gleichzeitig abbrechen.
    /// </summary>
    public bool HasReadWriteConflict(IEnumerable<object> keys, ulong startSeq)
    {
        foreach (var key in keys)
        {
            if (_readLockTable.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    foreach (var entry in list)
                        if (entry.StartSeq != startSeq && entry.Committed)
                            return true;
                }
            }
        }
        return false;
    }

    /// <summary>Wird aufgerufen, wenn eine Transaktion endet.</summary>
    public void ReleaseTransaction(ulong startSequence)
    {
        ReleaseSnapshot(startSequence);
    }

    private readonly record struct ReadLockEntry(ulong StartSeq, bool Committed);
}
