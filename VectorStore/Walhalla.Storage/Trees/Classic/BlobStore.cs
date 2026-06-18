// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Walhalla.Storage.Core;
using Walhalla.Storage.Core.Runtime;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Embedded blob store built on top of <see cref="WalhallaStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architecture:</b> blob payloads are written to an append-only sidecar file
/// (<c>blobs.dat</c>).  The WAL-backed <see cref="WalhallaStore"/> holds only a
/// 12-byte pointer per key (<c>long offset + int length</c>), so the B+Tree stays
/// small regardless of value size.
/// </para>
/// <para>
/// <b>Crash semantics (write):</b> the blob payload is written through to the hardware
/// buffer (<c>FileOptions.WriteThrough</c>, OS page-cache bypassed) <em>before</em> the
/// pointer is committed to the WAL.  True storage durability depends on the drive's
/// write-back cache; use an enterprise drive or a UPS for power-loss guarantees.  A crash between the two
/// steps leaves an orphaned blob region; the pointer is never visible to readers.
/// The orphaned bytes are reclaimed by <see cref="CompactAsync"/>.
/// </para>
/// <para>
/// <b>Crash semantics (compact):</b> compaction uses a two-phase commit: the new
/// blob layout is written to <c>blobs.dat.tmp</c>, then all pointer updates and a
/// sentinel flag are committed atomically to the WAL, and finally <c>blobs.dat.tmp</c>
/// is atomically renamed over <c>blobs.dat</c>.  The constructor detects and completes
/// any interrupted compaction on startup.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Put"/>, <see cref="PutAsync"/>,
/// <see cref="TryGet"/>, <see cref="TryGetAsync"/>, <see cref="Delete"/>, and
/// <see cref="DeleteAsync"/> are all thread-safe and may be called concurrently.
/// <see cref="CompactAsync"/> must not run concurrently with write operations;
/// call it during a maintenance window or after coordinating with callers.
/// </para>
/// </remarks>
public sealed class BlobStore : IDisposable
{
    // Sentinel key stored in the pointer WAL during compaction.
    // Null-byte prefix/suffix makes accidental collision with user keys very unlikely.
    private static readonly byte[] SentinelKey      = "\x00__wbs_compact__\x00"u8.ToArray();
    private static readonly byte[] SentinelPending  = [1];
    private static readonly byte[] SentinelDone     = [0];

    private readonly WalhallaStore _store;
    private readonly string _blobFilePath;
    private readonly string _blobTmpFilePath;
    private readonly Walhalla.Storage.Core.Configuration.WalSyncMode _syncMode;
    private SafeFileHandle? _blobHandle;
    private long _blobLength; // guarded by _appendLock
    private readonly object _appendLock = new();
    private volatile bool _isCompacting;
    private bool _disposed;

    public string RootPath { get; }

    /// <summary>Exposes the underlying pointer store for advanced scenarios (e.g. transactions).</summary>
    public WalhallaStore Store => _store;

    /// <summary>Reads a blob from the sidecar given its decoded pointer.</summary>
    internal byte[] ReadBlob(BlobPointer ptr) => ReadFromSidecar(ptr.Offset, ptr.Length);

    // ── construction ───────────────────────────────────────────────────────────

    /// <summary>Opens (or creates) a <see cref="BlobStore"/> at the location described by
    /// <paramref name="options"/>.</summary>
    public BlobStore(BlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.RootPath);

        RootPath = options.RootPath;

        _blobFilePath    = options.BlobFilePath;
        _blobTmpFilePath = options.BlobTmpFilePath;
        _syncMode        = options.WalSyncMode;

        _store = new WalhallaStore(options.BuildWalhallaOptions());

        // Complete any interrupted compaction before opening the blob handle.
        // CompleteFileSwap() (called from RecoverCompaction) may already open
        // the blob handle, in which case we must not open it again.
        RecoverCompaction();

        if (_blobHandle is null)
        {
            _blobHandle = OpenBlobFileHandle(_blobFilePath, create: true);
            _blobLength = RandomAccess.GetLength(_blobHandle);
        }
    }

    // ── public write API ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="blob"/> under <paramref name="key"/>, replacing any
    /// existing value.  The previous blob payload becomes orphaned until the next
    /// <see cref="CompactAsync"/>.
    /// </summary>
    public void Put(byte[] key, byte[] blob)
    {
        ThrowIfDisposed();
        ThrowIfCompacting();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(blob);

        var pointer = AppendToSidecar(blob);
        _store.Put(key, pointer.Encode());
    }

    /// <summary>
    /// Asynchronously writes <paramref name="blob"/> under <paramref name="key"/>.
    /// </summary>
    /// <param name="ct">
    /// Controls only the wait for the Group-Commit acknowledgement, not the blob write itself.
    /// See <c>WalhallaStore.PutAsync</c> for the full cancellation-race semantics.
    /// </param>
    public async Task PutAsync(byte[] key, byte[] blob, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfCompacting();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(blob);

        var pointer = AppendToSidecar(blob);
        await _store.PutAsync(key, pointer.Encode(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Schreibt mehrere Blobs atomar in einer einzigen WAL-Transaktion.
    /// Deutlich schneller als N einzelne <see cref="PutAsync"/>-Aufrufe,
    /// da nur ein fsync/WriteThrough fällig wird.
    /// </summary>
    public async Task PutBatchAsync(IEnumerable<(byte[] Key, byte[] Blob)> items, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfCompacting();

        var list = items.ToList();
        if (list.Count == 0) return;

        // Alle Blobs sequentiell in die Sidecar-Datei schreiben (unter _appendLock)
        var pointers = new (byte[] Key, byte[] PointerBytes)[list.Count];
        lock (_appendLock)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var (key, blob) = list[i];
                ArgumentNullException.ThrowIfNull(key);
                ArgumentNullException.ThrowIfNull(blob);

                var pointer = AppendToSidecar(blob);
                pointers[i] = (key, pointer.Encode());
            }
        }

        // Alle Pointer in einer einzigen Transaktion committen
        using var tx = _store.BeginTransaction();
        foreach (var (key, ptrBytes) in pointers)
            tx.Put(key, ptrBytes);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes <paramref name="key"/>.  The associated blob payload becomes orphaned
    /// until the next <see cref="CompactAsync"/>.
    /// </summary>
    public void Delete(byte[] key)
    {
        ThrowIfDisposed();
        ThrowIfCompacting();
        _store.Delete(key);
    }

    /// <inheritdoc cref="Delete"/>
    public Task DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfCompacting();
        return _store.DeleteAsync(key, ct);
    }

    // ── public read API ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the blob stored under <paramref name="key"/>.
    /// Returns <c>false</c> when the key does not exist.
    /// </summary>
    public bool TryGet(byte[] key, [NotNullWhen(true)] out byte[]? blob)
    {
        ThrowIfDisposed();

        if (!_store.TryGet(key, out var raw) || raw == null)
        {
            blob = null;
            return false;
        }

        var ptr = BlobPointer.Decode(raw);
        blob    = ReadFromSidecar(ptr.Offset, ptr.Length);
        return true;
    }

    /// <summary>
    /// Asynchronously reads the blob stored under <paramref name="key"/>,
    /// or <c>null</c> if the key does not exist.
    /// </summary>
    public async Task<byte[]?> TryGetAsync(byte[] key)
    {
        ThrowIfDisposed();

        if (!_store.TryGet(key, out var raw) || raw == null)
            return null;

        var ptr = BlobPointer.Decode(raw);
        return await ReadFromSidecarAsync(ptr.Offset, ptr.Length).ConfigureAwait(false);
    }
    // ── public scan API ────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all entries whose keys start with <paramref name="prefix"/>.
    /// Returns decoded blobs, not pointers.
    /// </summary>
    public IReadOnlyList<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ThrowIfDisposed();

        var pointers = _store.ScanPrefix(prefix);
        var results = new List<KeyValuePair<byte[], byte[]>>(pointers.Count);

        foreach (var (key, raw) in pointers)
        {
            var ptr = BlobPointer.Decode(raw);
            var blob = ReadFromSidecar(ptr.Offset, ptr.Length);
            results.Add(new KeyValuePair<byte[], byte[]>(key, blob));
        }

        return results;
    }

    /// <inheritdoc cref="ScanPrefix"/>
    public async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> ScanPrefixAsync(byte[] prefix, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var pointers = await _store.ScanPrefixAsync(prefix, ct).ConfigureAwait(false);
        var results = new List<KeyValuePair<byte[], byte[]>>(pointers.Count);

        foreach (var (key, raw) in pointers)
        {
            var ptr = BlobPointer.Decode(raw);
            var blob = await ReadFromSidecarAsync(ptr.Offset, ptr.Length).ConfigureAwait(false);
            results.Add(new KeyValuePair<byte[], byte[]>(key, blob));
        }

        return results;
    }
    // ── maintenance ────────────────────────────────────────────────────────────

    /// <summary>Triggers an explicit checkpoint of the pointer store.</summary>
    public Task CheckpointAsync(CancellationToken ct = default) =>
        _store.CheckpointAsync(ct);

    /// <summary>Returns a point-in-time diagnostic snapshot of the pointer store.</summary>
    public WalhallaDiagnostics GetDiagnostics() => _store.GetDiagnostics();

    /// <summary>
    /// Rewrites <c>blobs.dat</c> keeping only live blobs (those still referenced by a key in
    /// the pointer store), freeing space occupied by overwritten or deleted payloads.
    /// </summary>
    /// <remarks>
    /// <b>Crash safety:</b>
    /// <list type="bullet">
    ///   <item>Pre-compaction data is preserved until the operation commits atomically.</item>
    ///   <item>An interrupted compaction is automatically detected and completed on the next
    ///   <see cref="BlobStore"/> construction.</item>
    ///   <item>Do not call concurrently with <see cref="Put"/> / <see cref="Delete"/>
    ///   operations.  Schedule during a maintenance window.</item>
    /// </list>
    /// </remarks>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_isCompacting)
            throw new InvalidOperationException("A compaction is already in progress.");
        _isCompacting = true;
        try
        {
            await CompactCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _isCompacting = false; }
    }

    private async Task CompactCoreAsync(CancellationToken ct)
    {
        // ── Phase 1: collect live pointers, sorted by offset for sequential reads ──
        var liveEntries = _store
            .Scan(null, null)
            .Where(e => !e.Key.AsSpan().SequenceEqual(SentinelKey))
            .Select(e => (Key: e.Key, Ptr: BlobPointer.Decode(e.Value)))
            .OrderBy(e => e.Ptr.Offset)
            .ToList();

        // ── Phase 2: stream live blobs into the tmp file ────────────────────────
        if (File.Exists(_blobTmpFilePath))
            File.Delete(_blobTmpFilePath);

        var newPointers = new List<(byte[] Key, BlobPointer NewPtr)>(liveEntries.Count);
        long writeOffset = 0;

        using (var tmpHandle = OpenBlobFileHandle(_blobTmpFilePath, create: true))
        {
            foreach (var (key, oldPtr) in liveEntries)
            {
                ct.ThrowIfCancellationRequested();
                var data = ReadFromSidecar(oldPtr.Offset, oldPtr.Length);
                RandomAccess.Write(tmpHandle, data, writeOffset);
                newPointers.Add((key, new BlobPointer(writeOffset, oldPtr.Length)));
                writeOffset += oldPtr.Length;
            }
            // WriteThrough was set on tmpHandle → every RandomAccess.Write call
            // bypasses the OS cache and goes directly to the hardware buffer.
            // No explicit flush call needed.
        }

        // ── Phase 3: atomic two-phase commit ───────────────────────────────────
        // Update ALL pointers + set sentinel = pending in ONE transaction.
        // After this commit, the tmp file is canonical.
        using (var tx = _store.BeginTransaction())
        {
            foreach (var (key, newPtr) in newPointers)
                tx.Put(key, newPtr.Encode());

            tx.Put(SentinelKey, SentinelPending);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // ── Phase 4: atomic file swap ──────────────────────────────────────────
        CompleteFileSwap();

        // ── Phase 5: clear sentinel ────────────────────────────────────────────
        await _store.PutAsync(SentinelKey, SentinelDone, CancellationToken.None).ConfigureAwait(false);
    }


    // ── IDisposable ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blobHandle?.Dispose();
        _store.Dispose();
    }

    // ── private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="data"/> to the blob sidecar and returns its pointer.
    /// Thread-safe: serialises concurrent writes via <c>_appendLock</c>.
    /// </summary>
    private BlobPointer AppendToSidecar(byte[] data)
    {
        lock (_appendLock)
        {
            var offset = _blobLength;
            RandomAccess.Write(_blobHandle!, data, offset);
            _blobLength += data.Length;
            return new BlobPointer(offset, data.Length);
        }
    }

    private byte[] ReadFromSidecar(long offset, int length)
    {
        var buf = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var n = RandomAccess.Read(_blobHandle!, buf.AsSpan(totalRead), offset + totalRead);
            if (n == 0) break; // unexpected EOF
            totalRead += n;
        }
        if (totalRead != length)
            throw new IOException(
                $"Blob sidecar truncated at offset {offset}: expected {length} B, got {totalRead} B.");
        return buf;
    }

    private async Task<byte[]> ReadFromSidecarAsync(long offset, int length)
    {
        var buf = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var n = await RandomAccess
                .ReadAsync(_blobHandle!, buf.AsMemory(totalRead), offset + totalRead)
                .ConfigureAwait(false);
            if (n == 0) break; // unexpected EOF
            totalRead += n;
        }
        if (totalRead != length)
            throw new IOException(
                $"Blob sidecar truncated at offset {offset}: expected {length} B, got {totalRead} B.");
        return buf;
    }

    /// <summary>
    /// Called from the constructor: inspects the sentinel key and any leftover tmp files
    /// to complete or roll back an interrupted compaction.
    /// </summary>
    private void RecoverCompaction()
    {
        var tmpExists = File.Exists(_blobTmpFilePath);

        if (!_store.TryGet(SentinelKey, out var sentinelValue))
        {
            // No sentinel → clean state.
            if (tmpExists) File.Delete(_blobTmpFilePath);
            return;
        }

        var isPending = sentinelValue != null && sentinelValue.Length > 0 && sentinelValue[0] == 1;

        if (isPending)
        {
            if (tmpExists)
            {
                // Phase 3 committed but phase 4 didn't complete → finish the swap.
                CompleteFileSwap();
            }
            // else phase 4 already completed but phase 5 (clear sentinel) crashed →
            // the blob file is consistent; just clear the sentinel below.

            _store.Put(SentinelKey, SentinelDone);
        }
        else
        {
            // sentinel = done (0): clean up any stale tmp.
            if (tmpExists) File.Delete(_blobTmpFilePath);
        }
    }

    /// <summary>
    /// Atomically replaces <c>blobs.dat</c> with <c>blobs.dat.tmp</c>
    /// and reopens the blob handle.
    /// </summary>
    private void CompleteFileSwap()
    {
        lock (_appendLock)
        {
            // Close old handle before rename (may be null when called from ctor).
            _blobHandle?.Dispose();
            File.Move(_blobTmpFilePath, _blobFilePath, overwrite: true);
            _blobHandle  = OpenBlobFileHandle(_blobFilePath, create: false);
            _blobLength  = RandomAccess.GetLength(_blobHandle);
        }
    }

    private SafeFileHandle OpenBlobFileHandle(string path, bool create)
    {
        var mode = create ? FileMode.OpenOrCreate : FileMode.Open;
        var fileOptions = FileOptions.None;
        if (_syncMode != Walhalla.Storage.Core.Configuration.WalSyncMode.None)
            fileOptions |= FileOptions.WriteThrough;

        return File.OpenHandle(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            fileOptions);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BlobStore));
    }

    private void ThrowIfCompacting()
    {
        if (_isCompacting)
            throw new InvalidOperationException(
                "Write operations are not permitted while CompactAsync is running.");
    }
}
