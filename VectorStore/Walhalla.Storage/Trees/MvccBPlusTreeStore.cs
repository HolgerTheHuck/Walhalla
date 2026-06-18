// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Core;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Core.Configuration;
using Walhalla.Storage.Core.Logging;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Adapter, der <see cref="MvccBPlusTree"/> hinter dem gemeinsamen
/// <see cref="IKeyValueStore"/>-Vertrag exponiert.
/// </summary>
public sealed class MvccBPlusTreeStore : IKeyValueStore
{
    private readonly MvccBPlusTree _tree;
    private readonly TransactionManager _txManager;
    private readonly WalLog? _walLog;
    private readonly string? _walPath;
    private bool _disposed;

    internal MvccBPlusTreeStore(OdsPager pager, IKeyComparator? keyComparator = null, int order = 128,
        string? walPath = null, WalSyncMode walSyncMode = WalSyncMode.Fsync, int overflowThreshold = 256)
    {
        _txManager = new TransactionManager(ByteArrayObjectComparer.Instance);
        _tree = new MvccBPlusTree(pager, _txManager, keyComparator, order, overflowThreshold: overflowThreshold);
        _walPath = walPath;

        if (!string.IsNullOrEmpty(walPath))
        {
            _walLog = new WalLog(walPath, walSyncMode);
            RecoverFromWal();
        }
        else
        {
            // Ohne WAL trotzdem mit ODS-Inhalt synchronisieren, damit Scan() nach Reopen funktioniert
            var maxSeq = _tree.GetMaxSequence();
            if (maxSeq > 0)
                _txManager.AdvanceTo(maxSeq);
        }
    }

    /// <summary>
    /// Öffentlicher Konstruktor für externe Consumer (z. B. WalhallaSql).
    /// Erstellt intern den <see cref="OdsPager"/> und managed dessen Lebenszyklus.
    /// </summary>
    public MvccBPlusTreeStore(string odsPath, int pageSize = 4096, int pageCacheCapacity = 0,
        IKeyComparator? keyComparator = null, int order = 128,
        string? walPath = null, WalSyncMode walSyncMode = WalSyncMode.Fsync, int overflowThreshold = 256)
        : this(new OdsPager(odsPath, pageSize, pageCacheCapacity), keyComparator, order, walPath, walSyncMode, overflowThreshold)
    {
    }

    // Internal constructor für Tests mit vorkonfiguriertem Tree
    internal MvccBPlusTreeStore(MvccBPlusTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _txManager = tree.TransactionManager;
    }

    // ── IKeyValueStore: Auto-Commit Point-Operations ────────────────────────

    private void RecoverFromWal()
    {
        if (_walLog == null) return;

        var committed = _walLog.ReadCommittedTransactions();
        if (committed.Count == 0)
        {
            // Keine WAL-Records → trotzdem TxManager mit ODS-Inhalt synchronisieren
            var maxSeq = _tree.GetMaxSequence();
            if (maxSeq > 0)
                _txManager.AdvanceTo(maxSeq);
            return;
        }

        ulong maxCommitSeq = 0;
        foreach (var tx in committed)
        {
            var commitSeq = (ulong)tx.TransactionId;
            if (commitSeq > maxCommitSeq) maxCommitSeq = commitSeq;
            foreach (var op in tx.Operations)
            {
                if (op.Type == WalRecordType.Put)
                    _tree.Upsert(commitSeq, op.Key, op.Value!);
                else if (op.Type == WalRecordType.Delete)
                    _tree.Delete(commitSeq, op.Key);
            }
        }

        _txManager.AdvanceTo(maxCommitSeq);
        _walLog.Truncate();
    }

    public bool TryGet(byte[] key, out byte[]? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // Lese die neueste committed Version
        return _tree.TryGetLatest(key, out value);
    }

    public void Upsert(byte[] key, byte[] value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        using var tx = BeginTransaction(IsolationLevel.Snapshot);
        tx.Upsert(key, value);
        tx.Commit();
    }

    public void Delete(byte[] key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        using var tx = BeginTransaction(IsolationLevel.Snapshot);
        tx.Delete(key);
        tx.Commit();
    }

    // ── IKeyValueStore: Scans (neueste committed Version) ─────────────────────

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong seq = _txManager.AcquireSnapshot();
        try
        {
            foreach (var kv in _tree.ScanVisible(seq, fromInclusive, toExclusive))
                yield return kv;
        }
        finally
        {
            _txManager.ReleaseSnapshot(seq);
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prefix);

        ulong seq = _txManager.AcquireSnapshot();
        try
        {
            foreach (var kv in _tree.ScanPrefixVisible(seq, prefix))
                yield return kv;
        }
        finally
        {
            _txManager.ReleaseSnapshot(seq);
        }
    }

    public void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[], int, int, bool> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        foreach (var kv in Scan(fromInclusive, toExclusive))
        {
            var value = kv.Value;
            if (value == null) continue;
            if (!action(value, 0, value.Length))
                break;
        }
    }

    // ── IKeyValueStore: Bulk ────────────────────────────────────────────────

    public void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        ulong commitSequence = _txManager.AcquireCommitSequence();

        // WAL-Write vor dem Baum-Update (write-ahead semantics).
        if (_walLog != null)
        {
            var operations = new WalOperation[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var kv = entries[i];
                operations[i] = new WalOperation(WalRecordType.Put, kv.Key, kv.Value);
            }
            _walLog.AppendBatch((long)commitSequence, operations);
        }

        _tree.BulkUpsert(commitSequence, entries);

        foreach (var kv in entries)
            _txManager.RegisterCommitted(kv.Key, commitSequence);
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return;

        ulong commitSequence = _txManager.AcquireCommitSequence();

        if (_walLog != null)
        {
            var operations = new WalOperation[keys.Count];
            for (int i = 0; i < keys.Count; i++)
                operations[i] = new WalOperation(WalRecordType.Delete, keys[i], null);
            _walLog.AppendBatch((long)commitSequence, operations);
        }

        _tree.BulkDelete(commitSequence, keys);

        foreach (var key in keys)
            _txManager.RegisterCommitted(key, commitSequence);
    }

    // ── IKeyValueStore: MVCC ────────────────────────────────────────────────

    public IStorageTransaction BeginTransaction(IsolationLevel isolation = IsolationLevel.Snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new MvccBPlusTreeTransaction(_tree, _txManager, isolation, _walLog);
    }

    public IReadSnapshot BeginReadSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong seq = _txManager.AcquireSnapshot();
        return new MvccBPlusTreeSnapshot(_tree, seq, _txManager);
    }

    // ── IKeyValueStore: Wartung ───────────────────────────────────────────────

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tree.Checkpoint();
    }

    public Task CheckpointAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Checkpoint();
        return Task.CompletedTask;
    }

    public void Vacuum()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tree.Vacuum();
    }

    public StorageDiagnostics GetDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // TODO: Map interne Tree-Metriken auf StorageDiagnostics
        return new StorageDiagnostics
        {
            WalFileSizeBytes = -1,      // not yet tracked
            Extended = new System.Collections.Generic.Dictionary<string, object>
            {
                ["OldestSnapshotSeq"] = _txManager.OldestActiveSnapshot,
                ["CurrentSequence"] = _txManager.CurrentSequence
            }
        };
    }

    // ── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walLog?.Dispose();
        _tree?.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Nested: MvccBPlusTreeTransaction
// ═══════════════════════════════════════════════════════════════════════════

file sealed class MvccBPlusTreeTransaction : IStorageTransaction
{
    private readonly MvccBPlusTree _tree;
    private readonly TransactionManager _txManager;
    private readonly IsolationLevel _isolation;
    private readonly ulong _txId;
    private readonly ulong _snapshotSeq;
    private TransactionStatus _status;
    private bool _disposed;
    private readonly WalLog? _walLog;

    // Lokaler Write-Set (pending changes innerhalb dieser Tx)
    private readonly Dictionary<byte[], byte[]?> _writeSet;

    // Für Serializable Snapshot Isolation: gelesene Keys, die beim Dispose freigegeben werden.
    private readonly List<byte[]> _readSet;

    public MvccBPlusTreeTransaction(MvccBPlusTree tree, TransactionManager txManager,
        IsolationLevel isolation, WalLog? walLog = null)
    {
        _tree = tree;
        _txManager = txManager;
        _isolation = isolation;
        _txId = txManager.AcquireTxId();
        _snapshotSeq = txManager.AcquireSnapshot();
        _status = TransactionStatus.Active;
        _writeSet = new Dictionary<byte[], byte[]?>(ByteArrayComparer.Instance);
        _readSet = isolation == IsolationLevel.Serializable ? new List<byte[]>() : null!;
        _walLog = walLog;
    }

    public ulong TxId => _txId;
    public ulong Sequence => _snapshotSeq;
    public TransactionStatus Status => _status;

    public bool TryGet(byte[] key, out byte[]? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        // 1. Prüfe lokalen Write-Set (dirty read innerhalb derselben Tx)
        if (_writeSet.TryGetValue(key, out var local))
        {
            if (local == null)
            {
                value = null;
                return false; // Tombstone
            }
            value = local;
            return true;
        }

        // 2. Lese aus dem Baum mit Snapshot-Isolation
        var found = _tree.TryGetVisible(key, _snapshotSeq, out value);
        if (found && _isolation == IsolationLevel.Serializable)
        {
            _txManager.RegisterRead(key, _snapshotSeq);
            _readSet.Add(key);
        }
        return found;
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // TODO: Merge write-set into scan for repeatable read visibility
        return RegisterReadsDuringEnumeration(_tree.ScanVisible(_snapshotSeq, fromInclusive, toExclusive));
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prefix);

        return RegisterReadsDuringEnumeration(_tree.ScanPrefixVisible(_snapshotSeq, prefix));
    }

    private IEnumerable<KeyValuePair<byte[], byte[]>> RegisterReadsDuringEnumeration(
        IEnumerable<KeyValuePair<byte[], byte[]>> source)
    {
        if (_isolation != IsolationLevel.Serializable)
        {
            foreach (var kv in source)
                yield return kv;
            yield break;
        }

        foreach (var kv in source)
        {
            var key = kv.Key;
            _txManager.RegisterRead(key, _snapshotSeq);
            _readSet.Add(key);
            yield return kv;
        }
    }

    public void Upsert(byte[] key, byte[] value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        EnsureActive();

        _writeSet[key] = value;
    }

    public void Delete(byte[] key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        EnsureActive();

        _writeSet[key] = null; // Tombstone
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureActive();

        // SSI: Prüfe auf Read-Write-Konflikte
        if (_isolation == IsolationLevel.Serializable)
        {
            lock (_txManager.SerializableCommitLock)
            {
                // Eigene Reads als committed markieren, damit später committende
                // Transaktionen unseren Read berücksichtigen, aber wir nicht
                // gegen uns selbst prüfen.
                _txManager.MarkReadCommitted(_snapshotSeq);

                if (_txManager.HasReadWriteConflict(_writeSet.Keys, _snapshotSeq))
                    throw new TransactionConflictException("SSI read-write conflict detected.");

                FlushWriteSet();
            }
        }
        else if (_isolation == IsolationLevel.ReadCommitted)
        {
            // ReadCommitted erlaubt überlappende Writes
            FlushWriteSet();
        }
        else // Snapshot
        {
            FlushWriteSet();
        }

        _status = TransactionStatus.Committed;
    }

    public void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_status != TransactionStatus.Active) return;

        _tree.Rollback(_txId);
        _status = TransactionStatus.Aborted;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_status == TransactionStatus.Active)
        {
            // Rollback vor _disposed=true, damit Rollback() nicht selbst wirft
            _tree.Rollback(_txId);
            _status = TransactionStatus.Aborted;
        }

        // Serializable-Leseeinträge freigeben, damit andere Commits nicht blockiert bleiben.
        if (_isolation == IsolationLevel.Serializable)
        {
            foreach (var key in _readSet)
                _txManager.UnregisterRead(key, _snapshotSeq);
        }

        _disposed = true;
        _txManager.ReleaseSnapshot(_snapshotSeq);
    }

    private void EnsureActive()
    {
        if (_status != TransactionStatus.Active)
            throw new InvalidOperationException($"Transaction {_txId} is not active (status: {_status}).");
    }

    private void FlushWriteSet()
    {
        // Write-Write-Konfliktprüfung für Snapshot / Serializable, nicht für ReadCommitted
        if (_isolation != IsolationLevel.ReadCommitted)
        {
            foreach (var key in _writeSet.Keys)
            {
                if (_txManager.HasWriteConflict(key, _snapshotSeq))
                    throw new TransactionConflictException($"Write-write conflict on key.");
            }
        }

        ulong commitSeq = _txManager.AcquireCommitSequence();

        // WAL-Write vor dem Baum-Update (write-ahead semantics)
        if (_walLog != null)
        {
            var ops = new List<WalOperation>(_writeSet.Count);
            foreach (var (key, value) in _writeSet)
            {
                if (value == null)
                    ops.Add(new WalOperation(WalRecordType.Delete, key, null));
                else
                    ops.Add(new WalOperation(WalRecordType.Put, key, value));
            }
            _walLog.AppendBatch((long)commitSeq, ops);
        }

        foreach (var (key, value) in _writeSet)
        {
            if (value == null)
                _tree.Delete(commitSeq, key);
            else
                _tree.Upsert(commitSeq, key, value);

            _txManager.RegisterCommitted(key, commitSeq);
        }

        _tree.OnCommitted(_txId);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Nested: MvccBPlusTreeSnapshot
// ═══════════════════════════════════════════════════════════════════════════

file sealed class MvccBPlusTreeSnapshot : IReadSnapshot
{
    private readonly MvccBPlusTree _tree;
    private readonly ulong _snapshotSeq;
    private readonly TransactionManager _txManager;
    private bool _disposed;

    public MvccBPlusTreeSnapshot(MvccBPlusTree tree, ulong snapshotSeq,
        TransactionManager txManager)
    {
        _tree = tree;
        _snapshotSeq = snapshotSeq;
        _txManager = txManager;
    }

    public ulong Sequence => _snapshotSeq;

    public bool TryGet(byte[] key, out byte[]? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        return _tree.TryGetVisible(key, _snapshotSeq, out value);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _tree.ScanVisible(_snapshotSeq, fromInclusive, toExclusive);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prefix);

        return _tree.ScanPrefixVisible(_snapshotSeq, prefix);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _txManager.ReleaseSnapshot(_snapshotSeq);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Helper: Byte-Array-Comparer für das Write-Set
// ═══════════════════════════════════════════════════════════════════════════

file sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        if (x.Length != y.Length) return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        // FNV-1a 32-bit — stabil über Prozesslauf
        uint hash = 2166136261u;
        foreach (var b in obj)
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return (int)hash;
    }
}
