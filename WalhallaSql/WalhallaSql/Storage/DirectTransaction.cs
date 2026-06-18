using System;
using System.Collections.Generic;
using Walhalla.Storage.Contract;

namespace WalhallaSql.Storage;

/// <summary>
/// Read-only Snapshot über einen Nicht-MVCC-Store (B+Tree, InMemory).
/// Liefert den „aktuellen Stand" — für M3 ausreichend; echte Snapshot-
/// Isolation erst mit MvccBPlusTree (M4/M5).
/// </summary>
internal sealed class DirectReadSnapshot : IReadSnapshot
{
    private readonly IKeyValueStore _store;

    public ulong Sequence { get; }

    public DirectReadSnapshot(IKeyValueStore store, ulong sequence = 0)
    {
        _store = store;
        Sequence = sequence;
    }

    public bool TryGet(byte[] key, out byte[]? value)
        => _store.TryGet(key, out value);

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
        => _store.Scan(fromInclusive, toExclusive);

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
        => _store.ScanPrefix(prefix);

    public void Dispose() { }
}

/// <summary>
/// Schreib-/Lesetransaktion über einen Nicht-MVCC-Store.
/// Jeder Schreibvorgang geht direkt auf den Store; es gibt kein
/// Write-Write-Conflict-Detection.
/// </summary>
internal sealed class DirectStorageTransaction : IStorageTransaction
{
    private readonly IKeyValueStore _store;
    private bool _committed;
    private bool _rolledBack;

    public ulong TxId { get; }
    public ulong Sequence => TxId;
    public TransactionStatus Status
    {
        get
        {
            if (_committed) return TransactionStatus.Committed;
            if (_rolledBack) return TransactionStatus.Aborted;
            return TransactionStatus.Active;
        }
    }

    public DirectStorageTransaction(IKeyValueStore store, ulong txId = 0)
    {
        _store = store;
        TxId = txId;
    }

    public bool TryGet(byte[] key, out byte[]? value)
        => _store.TryGet(key, out value);

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
        => _store.Scan(fromInclusive, toExclusive);

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
        => _store.ScanPrefix(prefix);

    public void Upsert(byte[] key, byte[] value)
        => _store.Upsert(key, value);

    public void Delete(byte[] key)
        => _store.Delete(key);

    public void Commit()
        => _committed = true;

    public void Rollback()
        => _rolledBack = true;

    public void Dispose() { }
}
