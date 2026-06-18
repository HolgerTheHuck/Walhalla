// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Adapter that exposes a <see cref="BlobStore"/> through the shared
/// <see cref="IKeyValueStore"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// This adapter is intentionally thin: it re-uses <see cref="BlobStore"/>'s
/// existing sync API where possible and delegates transaction/snapshot semantics
/// to the underlying <see cref="WalhallaStore"/>.
/// </para>
/// <para>
/// <b>Scan / ScanValues:</b> Because <see cref="BlobStore"/> stores only 12-byte
/// pointers in the tree, a range scan must decode each pointer and read the
/// corresponding payload from the sidecar file.  This is slower than a native
/// inline-tree scan and is therefore a temporary bridge until
/// <see cref="StorageBackend.MvccBPlusTree"/> (M4) provides transparent overflow.
/// </para>
/// <para>
/// <b>Vacuum:</b> No-op at the adapter level; the sidecar is reclaimed via
/// <see cref="BlobStore.CompactAsync"/>, which is kept outside the contract.
/// </para>
/// </remarks>
public sealed class BlobStoreIKeyValueAdapter : IKeyValueStore
{
    private readonly BlobStore _blobStore;
    private bool _disposed;

    public BlobStoreIKeyValueAdapter(BlobStore blobStore)
    {
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    // ── IKeyValueStore: point operations ─────────────────────────────────────────

    public bool TryGet(byte[] key, out byte[]? value)
        => _blobStore.TryGet(key, out value);

    public void Upsert(byte[] key, byte[] value)
        => _blobStore.Put(key, value);

    public void Delete(byte[] key)
        => _blobStore.Delete(key);

    // ── IKeyValueStore: ordered scans ────────────────────────────────────────────

    public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null, byte[]? toExclusive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Delegate to the inner WalhallaStore, then decode pointers on the fly.
        var pointers = _blobStore.Store.Scan(fromInclusive, toExclusive);
        foreach (var (key, raw) in pointers)
        {
            var ptr = BlobPointer.Decode(raw);
            var blob = _blobStore.ReadBlob(ptr);
            yield return new KeyValuePair<byte[], byte[]>(key, blob);
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // BlobStore.ScanPrefix already decodes pointers for us.
        foreach (var kv in _blobStore.ScanPrefix(prefix))
            yield return kv;
    }

    public void ScanValues(
        byte[]? fromInclusive,
        byte[]? toExclusive,
        Func<byte[] /*buffer*/, int /*offset*/, int /*length*/, bool> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        var pointers = _blobStore.Store.Scan(fromInclusive, toExclusive);
        foreach (var (_, raw) in pointers)
        {
            var ptr = BlobPointer.Decode(raw);
            var blob = _blobStore.ReadBlob(ptr);
            if (!action(blob, 0, blob.Length))
                break;
        }
    }

    // ── IKeyValueStore: bulk operations ──────────────────────────────────────────

    public void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entries.Count == 0) return;

        // BlobStore.PutBatchAsync is async but performs the real work under
        // the hood with a single WAL transaction.  We block here because
        // IKeyValueStore.BulkUpsert is synchronous; the underlying group-commit
        // or WAL flush already provides durability semantics.
        var batch = entries.Select(e => (e.Key, e.Value));
        _blobStore.PutBatchAsync(batch, CancellationToken.None)
                  .GetAwaiter()
                  .GetResult();
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var key in keys)
            _blobStore.Delete(key);
    }

    // ── IKeyValueStore: MVCC ─────────────────────────────────────────────────────

    public IStorageTransaction BeginTransaction(IsolationLevel isolation = IsolationLevel.Snapshot)
        => _blobStore.Store.BeginTransaction(isolation);

    public IReadSnapshot BeginReadSnapshot()
        => _blobStore.Store.BeginReadSnapshot();

    // ── IKeyValueStore: lifecycle ───────────────────────────────────────────────

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _blobStore.CheckpointAsync(CancellationToken.None)
                  .GetAwaiter()
                  .GetResult();
    }

    public Task CheckpointAsync(CancellationToken ct = default)
        => _blobStore.CheckpointAsync(ct);

    public void Vacuum()
    {
        // No-op at the adapter level.  Sidecar compaction is handled outside
        // the contract via BlobStore.CompactAsync until M4 integrates overflow
        // directly into the engine.
    }

    public StorageDiagnostics GetDiagnostics()
    {
        var d = _blobStore.GetDiagnostics();
        return new StorageDiagnostics
        {
            WalFileSizeBytes = d.WalFileSizeBytes,
            MemTableEntries = d.MemTableEntries,
            MemTableApproxBytes = d.MemTableApproxBytes,
            DeltaEntryCount = d.DeltaEntryCount,
            TotalCheckpoints = d.TotalCheckpoints,
            TotalSpills = d.TotalSpills,
            LastCheckpointDuration = d.LastCheckpointDuration,
            TotalGroupCommitFlushes = d.TotalGroupCommitFlushes,
            TotalGroupedTransactions = d.TotalGroupedTransactions,
        };
    }

    // ── IDisposable ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blobStore.Dispose();
    }
}
