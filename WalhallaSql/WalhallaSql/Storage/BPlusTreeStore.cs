using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;

namespace WalhallaSql.Storage;

internal sealed class BPlusTreeStore : IKeyValueStore
{
    internal readonly BPlusTree Tree;

    public BPlusTreeStore(BPlusTree tree) => Tree = tree;

    public bool TryGet(byte[] key, out byte[]? value) => Tree.TryGet(key, out value);
    public void Upsert(byte[] key, byte[] value) => Tree.Upsert(key, value);
    public void Delete(byte[] key) => Tree.Delete(key);

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
        => Tree.EnumerateRange(fromInclusive, toExclusive);

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        // BPlusTree hat keine native ScanPrefix; wir emulieren über EnumerateRange
        byte[] toExclusive = new byte[prefix.Length + 1];
        prefix.CopyTo(toExclusive, 0);
        toExclusive[^1] = 0xFF;
        return Tree.EnumerateRange(prefix, toExclusive);
    }

    public void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[], int, int, bool> action)
        => Tree.EnumerateValuesSpan(fromInclusive, toExclusive, action);

    /// <summary>
    /// Bulk upsert override: forwards the whole batch into a single pager
    /// write batch on the underlying B+-tree so that all dirty pages get
    /// flushed in one syscall round instead of one per entry. This closes
    /// the 40× InsertBatch gap measured against SQLite where the default
    /// IKeyValueStore implementation looped Upsert per entry.
    /// </summary>
    public void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        Tree.BulkUpsert(entries);
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        for (int i = 0; i < keys.Count; i++)
            Tree.Delete(keys[i]);
    }

    public IStorageTransaction BeginTransaction(IsolationLevel isolation = IsolationLevel.Snapshot)
        => new DirectStorageTransaction(this);

    public IReadSnapshot BeginReadSnapshot()
        => new DirectReadSnapshot(this);

    public void Checkpoint() { /* BPlusTree hat kein separates Checkpoint */ }
    public Task CheckpointAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Vacuum() { /* BPlusTree hat kein Vacuum */ }

    public StorageDiagnostics GetDiagnostics()
        => new StorageDiagnostics { WalFileSizeBytes = 0 };

    public void Dispose() => Tree.Dispose();
}
