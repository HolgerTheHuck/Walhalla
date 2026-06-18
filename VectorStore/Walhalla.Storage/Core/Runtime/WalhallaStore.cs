// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Walhalla.Storage.Core;
using Walhalla.Storage.Core.Caching;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Core.Transformers;
using Walhalla.Storage.Core.Configuration;
using Microsoft.Extensions.Logging;
using Walhalla.Storage.Core.Concurrency;
using Walhalla.Storage.Core.Logging;
using Walhalla.Storage.Core.Recovery;
using Walhalla.Storage.Core.Transactions;
using Walhalla.Storage.Ods.Paging;
using Walhalla.Storage.Ods.Tree;
using Walhalla.Storage.Contract;

namespace Walhalla.Storage.Core.Runtime;

public sealed class WalhallaStore : IKeyValueStore, IDisposable
{
    internal const int CurrentOdsFormatVersion = 2;
    private const byte DeltaDeleteMarker = 0;
    private const byte DeltaPutMarker = 1;

    private readonly AsyncReaderWriterLock _lock = new();
    // Kept sorted by the store's configured IKeyComparator so that Scan() can use
    // a binary lower-bound search instead of a full O(n) linear scan + post-sort.
    private readonly SortedList<byte[], byte[]> _memTable;
    private readonly HashSet<byte[]> _memTableDeletes = new(ByteArrayContentComparer.Instance);
    private readonly LruValueCache _cache;
    private readonly WalLog _walLog;
    private readonly CheckpointStore _checkpointStore;
    private readonly IKeyComparator _keyComparator;
    private BPlusTree _odsTree;
    private BPlusTree? _deltaTree;
    // Bloom filters for O(1) negative-lookup short-circuits (see TryGet / TryGetBorrowed).
    // _memTableBloom tracks every key ever written to _memTable; cleared on spill or recovery.
    // _deltaBloom tracks keys spilled to _deltaTree; null until after the first post-compaction spill.
    // _odsBloom tracks all keys in _odsTree; built from an ODS scan at Recover() and rebuilt
    // after every compaction or ODS rebuild. Null only during initial construction before Recover() runs.
    private readonly BloomFilter _memTableBloom;
    private BloomFilter? _deltaBloom;
    private BloomFilter? _odsBloom;
    private bool _disposed;
    private long _nextTxId;
    private long _memTableApproxBytes;
    private long _totalCheckpoints;
    private long _totalSpills;
    private TimeSpan _lastCheckpointDuration;
    private readonly ILogger<WalhallaStore>? _logger;

    // --- Group-Commit infrastructure -----------------------------------------
    // Writers enqueue a PendingGroupCommit and await its TaskCompletionSource.
    // A single background flush loop drains the queue, writes all serialised
    // WAL records in one fsync, applies all operations under one write lock,
    // then signals every waiting writer.  This reduces fsync count from O(N)
    // to O(1) per concurrent batch, which is the primary scalability bottleneck
    // under high write concurrency.
    private readonly Channel<PendingGroupCommit> _commitQueue =
        Channel.CreateUnbounded<PendingGroupCommit>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
    private CancellationTokenSource? _flushLoopCts;
    private Task? _flushLoopTask;
    // Ensures that at most one checkpoint runs at a time, whether triggered by the flush
    // loop or by a user call to CheckpointAsync(). Public calls block until the semaphore
    // is available; flush-loop calls use a non-blocking try (WaitAsync(0)) and skip if busy.
    private readonly SemaphoreSlim _checkpointSemaphore = new(1, 1);
    private long _totalGroupCommitFlushes;
    private long _totalGroupedTransactions;

    public WalhallaStore(WalhallaOptions options, ILogger<WalhallaStore>? logger = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        ValidateModeCompatibility(options);
        options.Freeze();
        if (options.StorageMode != StorageMode.BPlusTree)
            throw new NotSupportedException($"Storage mode '{options.StorageMode}' is reserved for a later milestone. Use '{StorageMode.BPlusTree}' for now.");

        Directory.CreateDirectory(options.RootPath);

        _keyComparator = ResolveKeyComparator(options);
        _memTable      = new SortedList<byte[], byte[]>(new KeyComparatorAdapter(_keyComparator));
        _cache = new LruValueCache(options.CacheSizeBytes);
        // Size the MemTable bloom for ~1 % FP rate.  The estimate (maxBytes / 100) matches the
        // average entry overhead used by EstimateEntryBytes (key + value + 32 ≈ 100 bytes).
        _memTableBloom = new BloomFilter(Math.Max(100_000, (int)(options.HybridMemTableMaxBytes / 100)));
        _walLog = new WalLog(options.WalFilePath, options.WalSyncMode);
        _checkpointStore = new CheckpointStore(options.CheckpointFilePath);
        EnsureOrValidateOdsMetadata(options, _keyComparator);
        _odsTree = new BPlusTree(new OdsPager(options.OdsFilePath, options.OdsPageSizeBytes, options.PageCacheCapacity), _keyComparator);

        if (UsesDeltaTree(options.MemTableMode))
            _deltaTree = new BPlusTree(new OdsPager(options.DeltaFilePath, options.OdsPageSizeBytes, options.PageCacheCapacity), _keyComparator);

        Recover();

        // Start the group-commit flush loop on a dedicated OS thread (LongRunning).
        // Using a dedicated thread instead of a ThreadPool thread is critical for
        // deadlock prevention: Dispose() calls GetAwaiter().GetResult() on a ThreadPool
        // thread, and if the FlushLoop itself needed a ThreadPool thread to progress,
        // sync-over-async starvation would occur.  LongRunning bypasses the pool.
        _flushLoopCts  = new CancellationTokenSource();
        _flushLoopTask = Task.Factory.StartNew(
            () => FlushLoopAsync(_flushLoopCts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public WalhallaOptions Options { get; }

    public WalhallaTransaction BeginTransaction()
    {
        ThrowIfDisposed();
        // _nextTxId is modified atomically; no lock needed here.
        // The returned transaction will acquire a write lock when Commit(Async) is called.
        return new WalhallaTransaction(this, Interlocked.Increment(ref _nextTxId));
    }

    public bool TryGet(byte[] key, out byte[]? value)
    {
        ThrowIfDisposed();
        if (key == null) throw new ArgumentNullException(nameof(key));

        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGet(key, out value))
                return true;

            if (Options.MemTableMode == MemTableMode.InMemory || Options.MemTableMode == MemTableMode.Hybrid)
            {
                if (_memTableBloom.MightContain(key) && _memTable.TryGetValue(key, out var memValue))
                {
                    var decodedMem = DecodeValue(memValue);
                    _cache.Set(key, decodedMem);
                    value = (byte[])decodedMem.Clone();
                    return true;
                }

                if (Options.MemTableMode == MemTableMode.InMemory)
                {
                    value = null;
                    return false;
                }

                if (_memTableDeletes.Contains(key))
                {
                    value = null;
                    return false;
                }
            }

            var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
            if ((_deltaBloom == null || _deltaBloom.MightContain(key)) && delta.TryGet(key, out var rawDeltaValue))
            {
                if (!TryDecodeDeltaValue(rawDeltaValue!, out var decoded))
                {
                    value = null;
                    return false;
                }

                var decodedDelta = DecodeValue(decoded);
                _cache.Set(key, decodedDelta);
                value = (byte[])decodedDelta.Clone();
                return true;
            }

            if ((_odsBloom == null || _odsBloom.MightContain(key)) && _odsTree.TryGet(key, out var baseValue))
            {
                var decodedBase = DecodeValue(baseValue!);
                _cache.Set(key, decodedBase);
                value = (byte[])decodedBase.Clone();
                return true;
            }

            value = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Zero-copy read: returns the cached or freshly-decoded array by reference without cloning.
    /// The caller must treat the returned array as read-only.  The reference is only guaranteed
    /// stable for the duration of the caller's read lock (i.e. before any concurrent write commits).
    /// </summary>
    public bool TryGetBorrowed(byte[] key, out byte[]? value)
    {
        ThrowIfDisposed();
        if (key == null) throw new ArgumentNullException(nameof(key));

        _lock.EnterReadLock();
        try
        {
            // Use TryGetBorrowed to avoid cloning on cache hit.
            if (_cache.TryGetBorrowed(key, out value))
                return true;

            if (Options.MemTableMode == MemTableMode.InMemory || Options.MemTableMode == MemTableMode.Hybrid)
            {
                if (_memTableBloom.MightContain(key) && _memTable.TryGetValue(key, out var memValue))
                {
                    var decodedMem = DecodeValue(memValue);
                    // SetWeak: cache stores the reference we return; no additional clone.
                    _cache.SetWeak(key, decodedMem);
                    value = decodedMem;
                    return true;
                }

                if (Options.MemTableMode == MemTableMode.InMemory)
                {
                    value = null;
                    return false;
                }

                if (_memTableDeletes.Contains(key))
                {
                    value = null;
                    return false;
                }
            }

            var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
            if ((_deltaBloom == null || _deltaBloom.MightContain(key)) && delta.TryGet(key, out var rawDeltaValue))
            {
                if (!TryDecodeDeltaValue(rawDeltaValue!, out var decoded))
                {
                    value = null;
                    return false;
                }

                var decodedDelta = DecodeValue(decoded);
                _cache.SetWeak(key, decodedDelta);
                value = decodedDelta;
                return true;
            }

            if ((_odsBloom == null || _odsBloom.MightContain(key)) && _odsTree.TryGet(key, out var baseValue))
            {
                var decodedBase = DecodeValue(baseValue!);
                _cache.SetWeak(key, decodedBase);
                value = decodedBase;
                return true;
            }

            value = null;
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Writes <paramref name="key"/> → <paramref name="value"/> in a single auto-committed transaction.
    /// If a <see cref="Walhalla.Storage.Core.Configuration.WalhallaOptions.Transformer"/> is configured the value
    /// is encoded before being persisted.</summary>
    public void Put(byte[] key, byte[] value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));

        using var tx = BeginTransaction();
        tx.Put(key, EncodeValue(value));
        tx.Commit();
    }

    /// <summary>
    /// Writes <paramref name="key"/> ? <paramref name="value"/> via the Group-Commit pipeline and returns
    /// once the operation is durably written and applied.
    /// </summary>
    /// <param name="ct">Controls how long the caller waits for the flush-pump to acknowledge the commit.
    /// <para>
    /// <b>Important � cancellation semantics:</b> once the operation has been handed to the internal
    /// Group-Commit queue it <em>cannot be withdrawn</em>.  If <paramref name="ct"/> fires after the
    /// operation was enqueued but before the flush pump signals completion, this method throws
    /// <see cref="OperationCanceledException"/> while the store <em>may still commit the write</em>.
    /// Callers who maintain an external model (e.g. an in-memory dictionary) must therefore use
    /// <see cref="CancellationToken.None"/> for write operations, or must not roll back their model
    /// on cancellation.  Request-level timeouts should be enforced at a higher layer, not via this token.
    /// </para></param>
    public async Task PutAsync(byte[] key, byte[] value, CancellationToken ct = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));

        using var tx = BeginTransaction();
        tx.Put(key, EncodeValue(value));
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes <paramref name="key"/> in a single auto-committed transaction (no-op if not present).</summary>
    public void Delete(byte[] key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        using var tx = BeginTransaction();
        tx.Delete(key);
        tx.Commit();
    }

    /// <summary>
    /// Removes <paramref name="key"/> via the Group-Commit pipeline and returns once the deletion is
    /// durably written and applied.
    /// </summary>
    /// <param name="ct">
    /// <b>Important � cancellation semantics:</b> see <see cref="PutAsync"/> � the same
    /// cannot-be-withdrawn constraint applies.
    /// </param>
    public async Task DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));

        using var tx = BeginTransaction();
        tx.Delete(key);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public CacheStatistics GetCacheStatistics()
    {
        _lock.EnterReadLock();
        try
        {
            return new CacheStatistics(
                _cache.HitCount,
                _cache.MissCount,
                _cache.CurrentSizeBytes,
                _cache.CapacityBytes);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public long GetWalFileSizeBytes()
    {
        if (!File.Exists(Options.WalFilePath))
            return 0;

        return new FileInfo(Options.WalFilePath).Length;
    }

    /// <summary>
    /// Returns a point-in-time snapshot of operational metrics for this instance.
    /// The call acquires a brief read lock and iterates the delta tree when applicable.
    /// </summary>
    public WalhallaDiagnostics GetDiagnostics()
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            long deltaEntryCount = 0;
            if (_deltaTree != null)
            {
                foreach (var _ in _deltaTree.EnumerateEntries())
                    deltaEntryCount++;
            }

            return new WalhallaDiagnostics(
                WalFileSizeBytes:           _walLog.SizeBytes,
                MemTableEntries:            _memTable.Count,
                MemTableApproxBytes:        _memTableApproxBytes,
                DeltaEntryCount:            deltaEntryCount,
                TotalCheckpoints:           Volatile.Read(ref _totalCheckpoints),
                TotalSpills:                Volatile.Read(ref _totalSpills),
                LastCheckpointDuration:     _lastCheckpointDuration,
                Cache: new CacheStatistics(
                    _cache.HitCount, _cache.MissCount,
                    _cache.CurrentSizeBytes, _cache.CapacityBytes),
                TotalGroupCommitFlushes:    Volatile.Read(ref _totalGroupCommitFlushes),
                TotalGroupedTransactions:   Volatile.Read(ref _totalGroupedTransactions));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void CreateBackup(string backupDirectoryPath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(backupDirectoryPath))
            throw new ArgumentException("Backup directory path must not be empty.", nameof(backupDirectoryPath));

        _lock.EnterWriteLock();
        try
        {
            Directory.CreateDirectory(backupDirectoryPath);

            // Force a consistent persisted snapshot before copying files.
            CheckpointInternalAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

            CopyIfExists(Options.OdsFilePath, Path.Combine(backupDirectoryPath, Options.OdsFileName));
            CopyIfExists(Options.WalFilePath, Path.Combine(backupDirectoryPath, Options.WalFileName));
            CopyIfExists(Options.CheckpointFilePath, Path.Combine(backupDirectoryPath, Options.CheckpointFileName));
            CopyIfExists(Options.DeltaFilePath, Path.Combine(backupDirectoryPath, Options.DeltaFileName));
            CopyIfExists(Options.OdsMetadataFilePath, Path.Combine(backupDirectoryPath, Options.OdsMetadataFileName));

            var manifest = new BackupManifest(
                DateTimeOffset.UtcNow,
                CurrentOdsFormatVersion,
                _keyComparator.Id,
                Options.MemTableMode,
                Options.OdsUpdateMode,
                Options.OdsPageSizeBytes,
                Options.WalFileName,
                Options.CheckpointFileName,
                Options.OdsFileName,
                Options.DeltaFileName,
                Options.OdsMetadataFileName);

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(backupDirectoryPath, "backup-manifest.json"), manifestJson);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public static void RestoreBackup(WalhallaOptions targetOptions, string backupDirectoryPath)
    {
        if (targetOptions == null)
            throw new ArgumentNullException(nameof(targetOptions));
        if (string.IsNullOrWhiteSpace(backupDirectoryPath))
            throw new ArgumentException("Backup directory path must not be empty.", nameof(backupDirectoryPath));
        if (!Directory.Exists(backupDirectoryPath))
            throw new DirectoryNotFoundException($"Backup directory '{backupDirectoryPath}' does not exist.");

        var manifestPath = Path.Combine(backupDirectoryPath, "backup-manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

            if (manifest.OdsFormatVersion != CurrentOdsFormatVersion)
                throw new InvalidOperationException(
                    $"Backup ODS format version {manifest.OdsFormatVersion} is incompatible with current version {CurrentOdsFormatVersion}. Migration required.");

            if (!string.Equals(manifest.KeyComparatorId, targetOptions.KeyComparatorId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Backup uses key comparator '{manifest.KeyComparatorId}', but target options specify '{targetOptions.KeyComparatorId}'.");

            if (manifest.OdsPageSizeBytes != targetOptions.OdsPageSizeBytes)
                throw new InvalidOperationException(
                    $"Backup uses ODS page size {manifest.OdsPageSizeBytes} bytes, but target options specify {targetOptions.OdsPageSizeBytes} bytes.");
        }

        Directory.CreateDirectory(targetOptions.RootPath);

        DeleteIfExists(targetOptions.WalFilePath);
        DeleteIfExists(targetOptions.CheckpointFilePath);
        DeleteIfExists(targetOptions.OdsFilePath);
        DeleteIfExists(targetOptions.DeltaFilePath);
        DeleteIfExists(targetOptions.OdsMetadataFilePath);

        CopyIfExists(Path.Combine(backupDirectoryPath, targetOptions.WalFileName), targetOptions.WalFilePath);
        CopyIfExists(Path.Combine(backupDirectoryPath, targetOptions.CheckpointFileName), targetOptions.CheckpointFilePath);
        CopyIfExists(Path.Combine(backupDirectoryPath, targetOptions.OdsFileName), targetOptions.OdsFilePath);
        CopyIfExists(Path.Combine(backupDirectoryPath, targetOptions.DeltaFileName), targetOptions.DeltaFilePath);
        CopyIfExists(Path.Combine(backupDirectoryPath, targetOptions.OdsMetadataFileName), targetOptions.OdsMetadataFilePath);
    }

    /// <summary>
    /// Migrates an on-disk database from ODS format version 1 (no page checksums) to the
    /// current version (<see cref="CurrentOdsFormatVersion"/> = 2, FNV-1a checksums).
    /// </summary>
    /// <remarks>
    /// <para>The database must be <b>closed</b> before calling this method. The WAL must be
    /// empty � call <see cref="Checkpoint"/> and dispose the runtime before migrating.</para>
    /// <para>The migration rewrites <c>ods.dat</c> (and <c>delta.ods</c> if present) in-place
    /// using an atomic rename: the original V1 file is exchanged with a freshly-written V2 temp
    /// file only after the write completes successfully. A <c>.v1bak</c> backup is deleted last
    /// to keep the window in which both files reside on disk as short as possible.</para>
    /// <para>Entry-size validation against the V2 body (4 bytes smaller per page than V1) is
    /// performed implicitly during write. If an entry exceeded the V1 body by exactly the 4
    /// missing bytes, an <see cref="InvalidOperationException"/> is thrown and the original
    /// file is left intact.</para>
    /// </remarks>
    /// <param name="options">Options describing the database to migrate. Must match the page
    /// size and comparator used when the database was created.</param>
    /// <param name="progress">Optional progress callback (0�100 per migrated file).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the WAL is non-empty, the metadata is missing, the page size mismatches,
    /// or an individual entry does not fit in the V2 page body.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the current format version is not 1 (only V1?V2 is implemented).
    /// </exception>
    public static void MigrateOdsFormat(WalhallaOptions options, IProgress<int>? progress = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        // 1. Read the ODS metadata to determine the current format version.
        if (!File.Exists(options.OdsMetadataFilePath))
            throw new InvalidOperationException(
                $"ODS metadata file not found at '{options.OdsMetadataFilePath}'. " +
                "Open the database at least once with the current engine to create the metadata file.");

        var metaJson = File.ReadAllText(options.OdsMetadataFilePath);
        var metadata = JsonSerializer.Deserialize<OdsStorageMetadata>(metaJson)
            ?? throw new InvalidDataException("ODS metadata file is corrupt or empty.");

        // 2. Already at the current version ? nothing to do.
        if (metadata.OdsFormatVersion == CurrentOdsFormatVersion)
            return;

        // 3. Only V1 ? V2 is supported.
        if (metadata.OdsFormatVersion != 1)
            throw new NotSupportedException(
                $"Migration from ODS format version {metadata.OdsFormatVersion} is not supported. " +
                $"Only version 1 ? {CurrentOdsFormatVersion} is implemented.");

        // 4. Page size in metadata must match options.
        if (metadata.OdsPageSizeBytes != options.OdsPageSizeBytes)
            throw new InvalidOperationException(
                $"ODS page size mismatch: metadata records {metadata.OdsPageSizeBytes} bytes, " +
                $"but options specify {options.OdsPageSizeBytes} bytes. " +
                "Set OdsPageSizeBytes to match the value used when the database was created.");

        // 5. WAL must be empty � otherwise uncommitted operations would be silently discarded.
        if (File.Exists(options.WalFilePath) && new FileInfo(options.WalFilePath).Length > 0)
            throw new InvalidOperationException(
                "The WAL file is not empty. Checkpoint the database before migrating " +
                "(call Checkpoint() and dispose the WalhallaStore before running migration).");

        // 6. Resolve the comparator used to build the tree so ordering is preserved.
        var comparator = ResolveKeyComparator(options);

        // 7. Migrate ods.dat (V1 ? V2).
        if (File.Exists(options.OdsFilePath))
            MigrateOdsFileV1ToV2(options.OdsFilePath, options.OdsPageSizeBytes, comparator, progress);

        // 8. Migrate delta.ods if present (same format, same page size).
        if (File.Exists(options.DeltaFilePath))
            MigrateOdsFileV1ToV2(options.DeltaFilePath, options.OdsPageSizeBytes, comparator, progress);

        // 9. Update ods.meta.json atomically to record the new format version.
        var newMeta    = new OdsStorageMetadata(CurrentOdsFormatVersion, metadata.KeyComparatorId, metadata.OdsPageSizeBytes);
        var newMetaJson = JsonSerializer.Serialize(newMeta, new JsonSerializerOptions { WriteIndented = true });
        var metaTmp     = options.OdsMetadataFilePath + ".tmp";
        File.WriteAllText(metaTmp, newMetaJson);
        File.Move(metaTmp, options.OdsMetadataFilePath, overwrite: true);
    }

    /// <summary>
    /// Rewrites a single ODS file from V1 format (no checksum) to V2 format (FNV-1a checksum).
    /// All entries are read from the V1 file using a legacy pager (no checksum verification,
    /// full V1 body size), then written into a fresh V2 temp file which is atomically renamed
    /// over the original.
    /// </summary>
    private static void MigrateOdsFileV1ToV2(
        string filePath, int pageSize, IKeyComparator comparator, IProgress<int>? progress)
    {
        var tmpPath = filePath + ".migrating";
        var bakPath = filePath + ".v1bak";
        SafeDeleteFile(tmpPath);
        SafeDeleteFile(bakPath);

        // Phase 1: read all K/V entries from the V1 file.
        // legacyV1Mode = true ? skips checksum verification and uses V1 body size (pageSize - 17).
        var allEntries = new List<KeyValuePair<byte[], byte[]>>();
        using (var v1Pager = new OdsPager(filePath, pageSize, pageCacheCapacity: 0, legacyV1Mode: true))
        using (var v1Tree  = new BPlusTree(v1Pager, comparator))
        {
            foreach (var entry in v1Tree.EnumerateEntries())
                allEntries.Add(entry);
        }

        // Phase 2: write all entries into a new V2 ODS file (standard pager, checksum enabled).
        // The V2 body is 4 bytes smaller per page; the B+Tree splits as needed.
        // If a single entry is larger than the whole V2 body (extremely unlikely), Upsert throws.
        using (var v2Pager = new OdsPager(tmpPath, pageSize, pageCacheCapacity: 0))
        using (var v2Tree  = new BPlusTree(v2Pager, comparator))
        {
            for (var i = 0; i < allEntries.Count; i++)
            {
                var entry = allEntries[i];
                try
                {
                    v2Tree.Upsert(entry.Key, entry.Value);
                }
                catch (NotSupportedException)
                {
                    // The entry occupies the 4 bytes that are now the checksum slot ? cannot migrate.
                    SafeDeleteFile(tmpPath);
                    throw new InvalidOperationException(
                        $"Entry at index {i} (key={entry.Key.Length} B, value={entry.Value.Length} B) " +
                        $"cannot fit in ODS format version 2 with page size {pageSize}. " +
                        "The combined size exceeds the V2 page body by up to 4 bytes. " +
                        "Increase OdsPageSizeBytes to allow migration.");
                }

                progress?.Report((i + 1) * 100 / Math.Max(1, allEntries.Count));
            }

            v2Tree.Flush();
        }

        // Phase 3: atomic swap.  Keep V1 as .v1bak momentarily, then delete it.
        File.Move(filePath, bakPath, overwrite: false);
        File.Move(tmpPath,  filePath, overwrite: false);
        SafeDeleteFile(bakPath);
    }

    public async Task<byte[]?> TryGetAsync(byte[] key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (key == null) throw new ArgumentNullException(nameof(key));

        await _lock.EnterReadLockAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(key, out var cached))
                return cached;

            if (Options.MemTableMode == MemTableMode.InMemory || Options.MemTableMode == MemTableMode.Hybrid)
            {
                if (_memTable.TryGetValue(key, out var memValue))
                {
                    var decodedMem = DecodeValue(memValue);
                    _cache.Set(key, decodedMem);
                    return (byte[])decodedMem.Clone();
                }

                if (Options.MemTableMode == MemTableMode.InMemory)
                    return null;

                if (_memTableDeletes.Contains(key))
                    return null;
            }

            var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
            var rawDelta = await delta.TryGetAsync(key, ct).ConfigureAwait(false);
            if (rawDelta != null)
            {
                if (!TryDecodeDeltaValue(rawDelta, out var decoded))
                    return null;
                var decodedDelta = DecodeValue(decoded);
                _cache.Set(key, decodedDelta);
                return (byte[])decodedDelta.Clone();
            }

            if (_odsBloom == null || _odsBloom.MightContain(key))
            {
                var baseValue = await _odsTree.TryGetAsync(key, ct).ConfigureAwait(false);
                if (baseValue != null)
                {
                    var decodedBase = DecodeValue(baseValue);
                    _cache.Set(key, decodedBase);
                    return decodedBase;
                }
            }

            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _checkpointSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunCheckpointAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _checkpointSemaphore.Release();
        }
    }

    public async Task CreateBackupAsync(string backupDirectoryPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(backupDirectoryPath))
            throw new ArgumentException("Backup directory path must not be empty.", nameof(backupDirectoryPath));

        await _lock.EnterWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(backupDirectoryPath);
            await CheckpointInternalAsync(ct).ConfigureAwait(false);

            CopyIfExists(Options.OdsFilePath, Path.Combine(backupDirectoryPath, Options.OdsFileName));
            CopyIfExists(Options.WalFilePath, Path.Combine(backupDirectoryPath, Options.WalFileName));
            CopyIfExists(Options.CheckpointFilePath, Path.Combine(backupDirectoryPath, Options.CheckpointFileName));
            CopyIfExists(Options.DeltaFilePath, Path.Combine(backupDirectoryPath, Options.DeltaFileName));
            CopyIfExists(Options.OdsMetadataFilePath, Path.Combine(backupDirectoryPath, Options.OdsMetadataFileName));

            var manifest = new BackupManifest(
                DateTimeOffset.UtcNow, CurrentOdsFormatVersion, _keyComparator.Id,
                Options.MemTableMode, Options.OdsUpdateMode, Options.OdsPageSizeBytes,
                Options.WalFileName, Options.CheckpointFileName, Options.OdsFileName,
                Options.DeltaFileName, Options.OdsMetadataFileName);

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(backupDirectoryPath, "backup-manifest.json"), manifestJson, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Checkpoint()
    {
        CheckpointAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    internal IReadOnlyList<KeyValuePair<byte[], byte[]>> SnapshotEntries()
    {
        _lock.EnterReadLock();
        try
        {
            if (Options.MemTableMode == MemTableMode.OnDiskBPlusTree)
            {
                // Keys/values from BuildMergedMapFromTrees already own fresh byte arrays.
                var merged = BuildMergedMapFromTrees();
                return MaterializeSnapshotEntries(merged);
            }

            if (Options.MemTableMode == MemTableMode.Hybrid)
            {
                var merged = BuildMergedMapFromTrees();
                // Use CopyTo instead of foreach to avoid SortedList enumerator _version check.
                // MemTable keys/values MAY be shared references that outlive the snapshot, so
                // we Clone() them here to guarantee snapshot isolation.
                var memCount = _memTable.Count;
                if (memCount > 0)
                {
                    var mKeys = new byte[memCount][];
                    var mVals = new byte[memCount][];
                    _memTable.Keys.CopyTo(mKeys, 0);
                    _memTable.Values.CopyTo(mVals, 0);
                    for (int mi = 0; mi < memCount; mi++)
                        merged[(byte[])mKeys[mi].Clone()] = (byte[])mVals[mi].Clone();
                }

                foreach (var deleted in _memTableDeletes)
                    merged.Remove(deleted);

                // Keys/values in merged are either from ODS (fresh from ReadLeafEntries) or
                // cloned from _memTable above — no further clone needed.
                return MaterializeSnapshotEntries(merged);
            }

            // InMemory mode: snapshot via CopyTo (array access, no enumerator version check)
            {
                var memCount = _memTable.Count;
                if (memCount == 0)
                    return Array.Empty<KeyValuePair<byte[], byte[]>>();

                var mKeys = new byte[memCount][];
                var mVals = new byte[memCount][];
                _memTable.Keys.CopyTo(mKeys, 0);
                _memTable.Values.CopyTo(mVals, 0);

                var snap = new KeyValuePair<byte[], byte[]>[memCount];
                for (int mi = 0; mi < memCount; mi++)
                    snap[mi] = new KeyValuePair<byte[], byte[]>((byte[])mKeys[mi].Clone(), (byte[])mVals[mi].Clone());
                return snap;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all live entries whose keys fall in the range
    /// [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>).
    /// Pass <see langword="null"/> for an open-ended bound.
    /// Results are sorted in key order according to the store's configured <see cref="IKeyComparator"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<byte[], byte[]>> Scan(
        byte[]? fromInclusive = null,
        byte[]? toExclusive   = null)
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            return ScanInternal(fromInclusive, toExclusive);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all live keys whose values fall in the range
    /// [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>).
    /// Pass <see langword="null"/> for an open-ended bound.
    /// Results are sorted in key order according to the store's configured <see cref="IKeyComparator"/>.
    /// </summary>
    public IReadOnlyList<byte[]> ScanKeys(
        byte[]? fromInclusive = null,
        byte[]? toExclusive   = null)
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            return ScanKeysInternal(fromInclusive, toExclusive);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<KeyValuePair<byte[], byte[]>> ScanDescending(
        byte[]? fromInclusive = null,
        byte[]? toExclusive   = null)
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            if (TryScanMemTableOnly(fromInclusive, toExclusive, descending: true, out var fastResult))
                return fastResult;

            var ascending = ScanInternal(fromInclusive, toExclusive);
            var reversed = new List<KeyValuePair<byte[], byte[]>>(ascending.Count);
            for (var i = ascending.Count - 1; i >= 0; i--)
                reversed.Add(ascending[i]);

            return reversed;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc cref="Scan"/>
    public async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> ScanAsync(
        byte[]? fromInclusive  = null,
        byte[]? toExclusive    = null,
        CancellationToken ct   = default)
    {
        ThrowIfDisposed();

        await _lock.EnterReadLockAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanInternalAsync(fromInclusive, toExclusive, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all live entries whose keys start with
    /// <paramref name="prefix"/>.
    /// Results are sorted in key order according to the store's configured <see cref="IKeyComparator"/>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
    {
        ThrowIfDisposed();
        if (prefix is null || prefix.Length == 0)
            throw new ArgumentException("Prefix must not be null or empty.", nameof(prefix));

        var toExclusive = ComputePrefixUpperBound(prefix);

        _lock.EnterReadLock();
        try
        {
            return ScanInternal(prefix, toExclusive);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc cref="ScanPrefix"/>
    public async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> ScanPrefixAsync(byte[] prefix, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (prefix is null || prefix.Length == 0)
            throw new ArgumentException("Prefix must not be null or empty.", nameof(prefix));

        var toExclusive = ComputePrefixUpperBound(prefix);

        await _lock.EnterReadLockAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanInternalAsync(prefix, toExclusive, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Computes the exclusive upper bound for a prefix scan.
    /// Increments the last byte that is not 0xFF, or appends 0x00 if all bytes are 0xFF.
    /// </summary>
    internal static byte[] ComputePrefixUpperBound(byte[] prefix)
    {
        // Find last non-0xFF byte
        int i = prefix.Length - 1;
        while (i >= 0 && prefix[i] == 0xFF)
            i--;

        if (i < 0)
        {
            // All bytes are 0xFF: append 0x00 to get a key that sorts after all keys with this prefix
            var result = new byte[prefix.Length + 1];
            prefix.CopyTo(result, 0);
            result[^1] = 0x00;
            return result;
        }

        // Increment the last non-0xFF byte
        var upper = new byte[i + 1];
        prefix.AsSpan(0, i + 1).CopyTo(upper);
        upper[i]++;
        return upper;
    }

    internal void Commit(long transactionId, IReadOnlyList<WalOperation> operations)
        => CommitAsync(transactionId, operations).GetAwaiter().GetResult();

    /// <summary>
    /// Hands <paramref name="operations"/> to the Group-Commit queue and awaits the flush pump's
    /// acknowledgement.
    /// </summary>
    /// <remarks>
    /// <b>Cancellation race window:</b> the method calls
    /// <c>WriteAsync(pending, ct)</c> followed by <c>Tcs.Task.WaitAsync(ct)</c>.
    /// These are two separate awaitables with a gap between them.  If <paramref name="ct"/>
    /// is cancelled after <c>WriteAsync</c> returns (i.e. the operation is already in the queue)
    /// but before <c>WaitAsync</c> completes, this method throws
    /// <see cref="OperationCanceledException"/> while the flush pump <em>will still commit the
    /// operation</em>.  Callers that require exact "committed-or-not" semantics must pass
    /// <see cref="CancellationToken.None"/> here.
    /// </remarks>
    internal async Task CommitAsync(long transactionId, IReadOnlyList<WalOperation> operations,
                                    CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var pending = new PendingGroupCommit(transactionId, operations);
        // WriteAsync never blocks for an unbounded channel; enqueue then wait for the
        // flush loop to durably write + apply the transaction.
        await _commitQueue.Writer.WriteAsync(pending, ct).ConfigureAwait(false);
        await pending.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Background flush loop.  Runs for the lifetime of the runtime instance and is the
    /// <b>sole writer</b> to the WAL file after construction.
    /// <para>
    /// On each iteration it drains all currently-queued commits into a batch, serialises every
    /// transaction's WAL records into one buffer, performs a single async write + fsync, then
    /// applies all operations to the in-memory state under one write-lock acquisition.
    /// This converts N concurrent fsyncs into one, the dominant cost reduction for parallel
    /// write workloads.
    /// </para>
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _commitQueue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                // Optional coalescing window: let concurrent commits accumulate for a brief
                // interval before draining the queue.  Under high-concurrency workloads this
                // merges N × fsync calls into one — significantly improving write throughput.
                // The window is intentionally skipped when GroupCommitCoalesceMs == 0 so that
                // single-writer latency is unaffected.
                if (Options.GroupCommitCoalesceMs > 0)
                    await Task.Delay(Options.GroupCommitCoalesceMs, ct).ConfigureAwait(false);

                // Collect every item that is already in the queue right now.
                var batch = new List<PendingGroupCommit>();
                while (_commitQueue.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count == 0) continue;

                Exception? flushError = null;
                try
                {
                    // -- 1. Durable WAL write (single fsync for entire batch) ------
                    var group = batch
                        .Select(static b => (b.TransactionId, b.Operations))
                        .ToList();
                    await _walLog.AppendGroupAsync(group, ct).ConfigureAwait(false);

                    // -- 2. Apply to in-memory state (brief write-lock; NO checkpoint here) --
                    // The write lock is released as soon as the MemTable is updated.
                    // Auto-checkpoint runs after writers are signalled so that readers are
                    // blocked only for the fast MemTable-mutation window, not for disk I/O.
                    await _lock.EnterWriteLockAsync(ct).ConfigureAwait(false);
                    try
                    {
                        foreach (var item in batch)
                            await ApplyOperationsAsync(item.Operations, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }

                    Interlocked.Increment(ref _totalGroupCommitFlushes);
                    Interlocked.Add(ref _totalGroupedTransactions, batch.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    flushError = new OperationCanceledException(
                        "WalhallaStore was disposed before the transaction could be committed.", ct);
                }
                catch (Exception ex)
                {
                    flushError = ex;
                }

                // Signal every writer immediately: the write is in the WAL (durable) and
                // in the MemTable (visible to readers). Auto-checkpoint is a store-internal
                // concern and must not block the caller's await.
                foreach (var item in batch)
                {
                    if (flushError == null)
                        item.Tcs.TrySetResult(true);
                    else
                        item.Tcs.TrySetException(flushError);
                }

                if (flushError is OperationCanceledException)
                    return;

                // -- 3. Auto-checkpoint OUTSIDE write lock and writer signal path -------
                // Non-blocking semaphore try: if a checkpoint is already running this is a
                // no-op; the running checkpoint will truncate the WAL when it completes.
                if (flushError == null)
                    await TriggerAutoCheckpointOutsideLockAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown via Dispose */ }
        finally
        {
            // Fail any entries that arrived after the last successful drain.
            while (_commitQueue.Reader.TryRead(out var orphan))
                orphan.Tcs.TrySetException(new ObjectDisposedException(
                    nameof(WalhallaStore),
                    "The WalhallaStore was disposed before this transaction could be committed."));
        }
    }

    private void Recover()
    {
        // Called from constructor only � no other thread has access to this instance yet.
        // Remove any *.tmp files left behind by an interrupted checkpoint.
        WarnAndDeleteStaleFile(Options.OdsTmpFilePath);
        WarnAndDeleteStaleFile(Options.DeltaTmpFilePath);

        _memTable.Clear();
        _memTableDeletes.Clear();
        _memTableApproxBytes = 0;
        _memTableBloom.Clear();
        // The delta tree content on disk is unknown after a restart; disable delta bloom
        // until after the first post-compaction spill, when its content is well-known.
        _deltaBloom = null;
        // ODS bloom will be rebuilt from a full scan at the end of Recover().
        _odsBloom = null;

        _logger?.LogDebug("Recovery started (MemTableMode={Mode}).", Options.MemTableMode);

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            var checkpointData = _checkpointStore.Load();
            foreach (var item in checkpointData)
                SetMemTableValue((byte[])item.Key.Clone(), (byte[])item.Value.Clone());
            _logger?.LogDebug("Recovery: loaded {Count} entries from checkpoint store.", checkpointData.Count);
        }

        var committed = _walLog.ReadCommittedTransactions();
        _logger?.LogDebug("Recovery: replaying {Count} committed WAL transaction(s).", committed.Count);

        foreach (var transaction in committed)
        {
            ApplyOperations(transaction.Operations);
            if (transaction.TransactionId > _nextTxId)
                _nextTxId = transaction.TransactionId;
        }

        _logger?.LogDebug("Recovery complete. Next transaction ID: {NextTxId}.", _nextTxId);

        // Build the ODS bloom from the current on-disk state (after WAL replay).
        _odsBloom = BuildOdsBloomFromScan();
    }

    private void ApplyOperations(IReadOnlyList<WalOperation> operations)
    {
        if (Options.MemTableMode == MemTableMode.OnDiskBPlusTree)
        {
            ApplyOperationsOnDiskDelta(operations);
            return;
        }

        if (Options.MemTableMode == MemTableMode.Hybrid)
        {
            ApplyOperationsHybrid(operations);
            return;
        }

        foreach (var operation in operations)
        {
            if (operation.Type == WalRecordType.Delete)
            {
                SetMemTableDelete(operation.Key);
                _cache.Remove(operation.Key);
                if (Options.OdsUpdateMode == OdsUpdateMode.Immediate)
                    _odsTree.Delete(operation.Key);
                continue;
            }

            SetMemTableValue(operation.Key, operation.Value!);
            _cache.Remove(operation.Key);
            if (Options.OdsUpdateMode == OdsUpdateMode.Immediate)
            {
                _odsTree.Upsert(operation.Key, operation.Value!);
                _odsBloom?.Add(operation.Key);
            }
        }
    }

    private async Task ApplyOperationsAsync(IReadOnlyList<WalOperation> operations, CancellationToken ct)
    {
        if (Options.MemTableMode == MemTableMode.OnDiskBPlusTree)
        {
            await ApplyOperationsOnDiskDeltaAsync(operations, ct).ConfigureAwait(false);
            return;
        }

        if (Options.MemTableMode == MemTableMode.Hybrid)
        {
            await ApplyOperationsHybridAsync(operations, ct).ConfigureAwait(false);
            return;
        }

        foreach (var operation in operations)
        {
            if (operation.Type == WalRecordType.Delete)
            {
                SetMemTableDelete(operation.Key);
                _cache.Remove(operation.Key);
                if (Options.OdsUpdateMode == OdsUpdateMode.Immediate)
                    await _odsTree.DeleteAsync(operation.Key, ct).ConfigureAwait(false);
                continue;
            }

            SetMemTableValue(operation.Key, operation.Value!);
            _cache.Remove(operation.Key);
            if (Options.OdsUpdateMode == OdsUpdateMode.Immediate)
            {
                await _odsTree.UpsertAsync(operation.Key, operation.Value!, ct).ConfigureAwait(false);
                _odsBloom?.Add(operation.Key);
            }
        }
    }

    private void ApplyOperationsHybrid(IReadOnlyList<WalOperation> operations)
    {
        foreach (var operation in operations)
        {
            _cache.Remove(operation.Key);
            if (operation.Type == WalRecordType.Delete)
            {
                SetMemTableDelete(operation.Key);
                continue;
            }

            SetMemTableValue(operation.Key, operation.Value!);
        }

        if (_memTableApproxBytes > Options.HybridMemTableMaxBytes)
            SpillMemTableOverlayToDelta();
    }

    private async Task ApplyOperationsHybridAsync(IReadOnlyList<WalOperation> operations, CancellationToken ct)
    {
        foreach (var operation in operations)
        {
            _cache.Remove(operation.Key);
            if (operation.Type == WalRecordType.Delete)
            {
                SetMemTableDelete(operation.Key);
                continue;
            }

            SetMemTableValue(operation.Key, operation.Value!);
        }

        if (_memTableApproxBytes > Options.HybridMemTableMaxBytes)
            await SpillMemTableOverlayToDeltaAsync(ct).ConfigureAwait(false);
    }

    private void ApplyOperationsOnDiskDelta(IReadOnlyList<WalOperation> operations)
    {
        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");

        foreach (var operation in operations)
        {
            _cache.Remove(operation.Key);
            if (operation.Type == WalRecordType.Delete)
            {
                delta.Upsert(operation.Key, new[] { DeltaDeleteMarker });
                continue;
            }

            delta.Upsert(operation.Key, EncodeDeltaPutValue(operation.Value!));
        }
    }

    private async Task ApplyOperationsOnDiskDeltaAsync(IReadOnlyList<WalOperation> operations, CancellationToken ct)
    {
        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");

        foreach (var operation in operations)
        {
            _cache.Remove(operation.Key);
            if (operation.Type == WalRecordType.Delete)
            {
                await delta.UpsertAsync(operation.Key, new[] { DeltaDeleteMarker }, ct).ConfigureAwait(false);
                continue;
            }

            await delta.UpsertAsync(operation.Key, EncodeDeltaPutValue(operation.Value!), ct).ConfigureAwait(false);
        }
    }

    private void RebuildOdsFromMemTable()
    {
        var tmpPath = Options.OdsTmpFilePath;
        SafeDeleteFile(tmpPath);

        using var tmpTree = new BPlusTree(
            new OdsPager(tmpPath, Options.OdsPageSizeBytes, pageCacheCapacity: 0),
            _keyComparator);
        foreach (var entry in _memTable)
            tmpTree.Upsert(entry.Key, entry.Value);
        tmpTree.Flush();

        SwapTmpOdsToLive();
        _odsBloom = BuildOdsBloomFromScan();
    }

    private async Task RebuildOdsFromMemTableAsync(CancellationToken ct)
    {
        await BuildOdsTmpFromEntriesAsync(_memTable, ct).ConfigureAwait(false);
        SwapTmpOdsToLive();
        _odsBloom = BuildOdsBloomFromScan();
    }

    private void ApplyDeltaIntoBaseIncremental()
    {
        // Sync wrapper � delegates to the async path.
        MergeIntoNewOdsThenSwapAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private async Task ApplyDeltaIntoBaseIncrementalAsync(CancellationToken ct)
    {
        await MergeIntoNewOdsThenSwapAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a fully-merged ODS file in a temp path (<c>ods.dat.tmp</c>), then atomically
    /// replaces the live <c>ods.dat</c> via <see cref="File.Move"/>.
    /// Crash safety:
    /// <list type="bullet">
    ///   <item>If the process dies before the rename, both <c>ods.dat</c> and <c>delta.dat</c>
    ///         are intact; the WAL has not been truncated; recovery proceeds normally.</item>
    ///   <item>The <c>*.tmp</c> file is cleaned up at startup by <see cref="Recover"/>.</item>
    ///   <item>WAL truncation happens <em>after</em> this method returns, in
    ///         <see cref="CheckpointInternalAsync"/>.</item>
    /// </list>
    /// </summary>
    private async Task MergeIntoNewOdsThenSwapAsync(CancellationToken ct)
    {
        var tmpPath = Options.OdsTmpFilePath;
        SafeDeleteFile(tmpPath);

        _logger?.LogDebug("Compaction 1/3: building merged ODS at '{TmpPath}'.", tmpPath);

        // -- Phase 1: write merged state into tmpPath -----------------------
        // Any exception here leaves ods.dat and delta.dat untouched.
        {
            using var tmpTree = new BPlusTree(
                new OdsPager(tmpPath, Options.OdsPageSizeBytes, 0 /* sequential write � no cache */),
                _keyComparator);

            // Base ODS entries first.
            foreach (var entry in _odsTree.EnumerateEntries())
                await tmpTree.UpsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);

            // Delta on top (overwrites / deletes base entries).
            var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
            await foreach (var entry in delta.EnumerateEntriesAsync(ct).ConfigureAwait(false))
            {
                if (TryDecodeDeltaValue(entry.Value, out var decoded))
                    await tmpTree.UpsertAsync(entry.Key, decoded, ct).ConfigureAwait(false);
                else
                    await tmpTree.DeleteAsync(entry.Key, ct).ConfigureAwait(false);
            }

            tmpTree.Flush(); // fsync before rename
        } // tmpTree disposed here � file handle released

        // -- Phase 2: atomic swap -------------------------------------------
        _logger?.LogDebug("Compaction 2/3: atomic rename '{Tmp}' ? '{Ods}'.", tmpPath, Options.OdsFilePath);

        // Close live trees so Windows can rename over them.
        _odsTree.Dispose();
        _deltaTree!.Dispose();
        _deltaTree = null;

        File.Move(tmpPath, Options.OdsFilePath, overwrite: true);

        // -- Phase 3: reset delta and reopen trees --------------------------
        SafeDeleteFile(Options.DeltaFilePath);

        _odsTree = new BPlusTree(
            new OdsPager(Options.OdsFilePath, Options.OdsPageSizeBytes, Options.PageCacheCapacity),
            _keyComparator);
        _deltaTree = new BPlusTree(
            new OdsPager(Options.DeltaFilePath, Options.OdsPageSizeBytes, Options.PageCacheCapacity),
            _keyComparator);
        // Delta is freshly empty — bloom can now be populated from the next spill onward.
        _deltaBloom = new BloomFilter(Math.Max(100_000, (int)(Options.HybridMemTableMaxBytes / 100)));
        // Rebuild ODS bloom to reflect the newly merged ODS content.
        _odsBloom = BuildOdsBloomFromScan();

        _logger?.LogDebug("Compaction 3/3: delta reset, trees reopened.");
    }

    private void SpillMemTableOverlayToDelta()
    {
        if (_memTable.Count == 0 && _memTableDeletes.Count == 0)
            return;

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        foreach (var item in _memTable)
        {
            delta.Upsert(item.Key, EncodeDeltaPutValue(item.Value));
            _deltaBloom?.Add(item.Key);
        }

        foreach (var deletedKey in _memTableDeletes)
        {
            delta.Upsert(deletedKey, new[] { DeltaDeleteMarker });
            _deltaBloom?.Add(deletedKey);
        }

        _memTable.Clear();
        _memTableDeletes.Clear();
        _memTableApproxBytes = 0;
        _memTableBloom.Clear();
        _totalSpills++;
    }

    private async Task SpillMemTableOverlayToDeltaAsync(CancellationToken ct)
    {
        if (_memTable.Count == 0 && _memTableDeletes.Count == 0)
            return;

        _logger?.LogDebug("MemTable spill to delta: {Puts} put(s), {Deletes} delete(s).",
            _memTable.Count, _memTableDeletes.Count);

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        // Snapshot via CopyTo before async iteration — avoids enumerator _version check
        // when the await suspends between MoveNext() calls.
        var spillCount = _memTable.Count;
        var spillKeys = new byte[spillCount][];
        var spillVals = new byte[spillCount][];
        if (spillCount > 0)
        {
            _memTable.Keys.CopyTo(spillKeys, 0);
            _memTable.Values.CopyTo(spillVals, 0);
        }
        for (int si = 0; si < spillCount; si++)
        {
            await delta.UpsertAsync(spillKeys[si], EncodeDeltaPutValue(spillVals[si]), ct).ConfigureAwait(false);
            _deltaBloom?.Add(spillKeys[si]);
        }

        foreach (var deletedKey in _memTableDeletes)
        {
            await delta.UpsertAsync(deletedKey, new[] { DeltaDeleteMarker }, ct).ConfigureAwait(false);
            _deltaBloom?.Add(deletedKey);
        }

        _memTable.Clear();
        _memTableDeletes.Clear();
        _memTableApproxBytes = 0;
        _memTableBloom.Clear();
        _totalSpills++;
    }

    private Dictionary<byte[], byte[]> BuildMergedMapFromTrees()
    {
        // entry.Key / entry.Value from EnumerateEntries (via ReadLeafEntries → .ToArray()) are
        // already freshly-allocated byte arrays. No Clone() needed here.
        var merged = new Dictionary<byte[], byte[]>(ByteArrayContentComparer.Instance);
        foreach (var entry in _odsTree.EnumerateEntries())
            merged[entry.Key] = entry.Value;

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        foreach (var entry in delta.EnumerateEntries())
        {
            if (TryDecodeDeltaValue(entry.Value, out var decoded))
                merged[entry.Key] = decoded;
            else
                merged.Remove(entry.Key);
        }

        return merged;
    }

    private static KeyValuePair<byte[], byte[]>[] MaterializeSnapshotEntries(Dictionary<byte[], byte[]> merged)
    {
        if (merged.Count == 0)
            return Array.Empty<KeyValuePair<byte[], byte[]>>();

        var snapshot = new KeyValuePair<byte[], byte[]>[merged.Count];
        var index = 0;
        foreach (var entry in merged)
            snapshot[index++] = entry;

        return snapshot;
    }

    private Dictionary<byte[], byte[]> BuildMergedRangeMap(byte[]? fromInclusive, byte[]? toExclusive)
    {
        // entry.Key / entry.Value from EnumerateRange (via ReadLeafEntries) are fresh arrays.
        var merged = new Dictionary<byte[], byte[]>(ByteArrayContentComparer.Instance);
        foreach (var entry in _odsTree.EnumerateRange(fromInclusive, toExclusive))
            merged[entry.Key] = entry.Value;

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        foreach (var entry in delta.EnumerateRange(fromInclusive, toExclusive))
        {
            if (TryDecodeDeltaValue(entry.Value, out var decoded))
                merged[entry.Key] = decoded;
            else
                merged.Remove(entry.Key);
        }

        return merged;
    }

    /// <summary>
    /// Lazily merges ODS and Delta ranges in sorted key order.  Delta entries take precedence;
    /// delete markers (where <see cref="TryDecodeDeltaValue"/> returns false) suppress ODS entries.
    /// The MemTable overlay for <see cref="MemTableMode.Hybrid"/> is NOT applied here —
    /// callers that need MemTable entries must layer them on top.
    /// Both source iterators are sorted by the same <see cref="_keyComparator"/>, so this is a
    /// single-pass O(n) merge without buffering the full result set.
    /// </summary>
    private IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateMergedRange(
        byte[]? fromInclusive, byte[]? toExclusive)
    {
        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");

        using var odsIt = _odsTree.EnumerateRange(fromInclusive, toExclusive).GetEnumerator();
        using var deltaIt = delta.EnumerateRange(fromInclusive, toExclusive).GetEnumerator();

        bool hasOds = odsIt.MoveNext();
        bool hasDelta = deltaIt.MoveNext();

        while (hasOds || hasDelta)
        {
            int cmp;
            if (!hasOds)
                cmp = 1;
            else if (!hasDelta)
                cmp = -1;
            else
                cmp = _keyComparator.Compare(odsIt.Current.Key, deltaIt.Current.Key);

            if (cmp < 0)
            {
                // ODS key is smaller — no delta entry for it, yield ODS value as-is
                yield return odsIt.Current;
                hasOds = odsIt.MoveNext();
            }
            else if (cmp > 0)
            {
                // Delta key is smaller — no ODS entry for it
                if (TryDecodeDeltaValue(deltaIt.Current.Value, out var decodedNew))
                    yield return new KeyValuePair<byte[], byte[]>(deltaIt.Current.Key, decodedNew);
                // else: delete marker for a key that was never in ODS — skip
                hasDelta = deltaIt.MoveNext();
            }
            else
            {
                // Same key — delta wins
                if (TryDecodeDeltaValue(deltaIt.Current.Value, out var decodedOverride))
                    yield return new KeyValuePair<byte[], byte[]>(deltaIt.Current.Key, decodedOverride);
                // else: delete marker — suppress the ODS entry entirely
                hasOds = odsIt.MoveNext();
                hasDelta = deltaIt.MoveNext();
            }
        }
    }

    private async Task<Dictionary<byte[], byte[]>> BuildMergedRangeMapAsync(
        byte[]? fromInclusive, byte[]? toExclusive, CancellationToken ct)
    {
        // entry.Key / entry.Value from EnumerateRangeAsync (via ReadLeafEntries) are fresh arrays.
        var merged = new Dictionary<byte[], byte[]>(ByteArrayContentComparer.Instance);
        await foreach (var entry in _odsTree.EnumerateRangeAsync(fromInclusive, toExclusive, ct).ConfigureAwait(false))
            merged[entry.Key] = entry.Value;

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        await foreach (var entry in delta.EnumerateRangeAsync(fromInclusive, toExclusive, ct).ConfigureAwait(false))
        {
            if (TryDecodeDeltaValue(entry.Value, out var decoded))
                merged[entry.Key] = decoded;
            else
                merged.Remove(entry.Key);
        }

        return merged;
    }

    private HashSet<byte[]> BuildMergedRangeKeySet(byte[]? fromInclusive, byte[]? toExclusive)
    {
        var merged = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
        foreach (var entry in _odsTree.EnumerateRange(fromInclusive, toExclusive))
            merged.Add(entry.Key);

        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");
        foreach (var entry in delta.EnumerateRange(fromInclusive, toExclusive))
        {
            if (TryDecodeDeltaValue(entry.Value, out _))
            {
                merged.Add(entry.Key);
                continue;
            }

            merged.Remove(entry.Key);
        }

        return merged;
    }

    private IReadOnlyList<KeyValuePair<byte[], byte[]>> ScanInternal(byte[]? from, byte[]? to)
    {
        if (TryScanMemTableOnly(from, to, descending: false, out var fastResult))
            return fastResult;

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            // _memTable is a SortedList — binary lower-bound lets us skip keys < from
            // entirely and break as soon as key >= to.  No post-sort needed.
            var keys   = _memTable.Keys;
            var values = _memTable.Values;
            int start  = from != null ? MemTableLowerBound(from) : 0;
            var result = new List<KeyValuePair<byte[], byte[]>>();
            for (int i = start; i < keys.Count; i++)
            {
                byte[] k = keys[i];
                if (to != null && _keyComparator.Compare(k, to) >= 0) break;
                result.Add(new KeyValuePair<byte[], byte[]>((byte[])k.Clone(), DecodeValue(values[i])));
            }
            return result;
        }

        if (Options.MemTableMode == MemTableMode.Hybrid)
        {
            // Use EnumerateMergedRange for ODS+Delta merge, then overlay MemTable.
            // A dictionary is still required to apply MemTable overrides and delete-set.
            var merged = new Dictionary<byte[], byte[]>(ByteArrayContentComparer.Instance);
            foreach (var entry in EnumerateMergedRange(from, to))
                merged[entry.Key] = entry.Value;

            var memKeys = _memTable.Keys;
            var memVals = _memTable.Values;
            var memStart = from != null ? MemTableLowerBound(from) : 0;
            var memEnd   = to   != null ? MemTableLowerBound(to)   : memKeys.Count;
            for (var mi = memStart; mi < memEnd; mi++)
                merged[memKeys[mi]] = memVals[mi];
            foreach (var deleted in _memTableDeletes)
                merged.Remove(deleted);

            var sorted = new List<KeyValuePair<byte[], byte[]>>(merged.Count);
            if (Options.Transformer is null)
            {
                foreach (var pair in merged)
                    sorted.Add(new KeyValuePair<byte[], byte[]>((byte[])pair.Key.Clone(), pair.Value));
            }
            else
            {
                foreach (var pair in merged)
                    sorted.Add(new KeyValuePair<byte[], byte[]>((byte[])pair.Key.Clone(), DecodeValue(pair.Value)));
            }
            sorted.Sort((a, b) => _keyComparator.Compare(a.Key, b.Key));
            return sorted;
        }

        // OnDiskBPlusTree only: EnumerateMergedRange already yields entries in sorted order.
        // No dictionary allocation; enumerate directly into the result list.
        var odsList = new List<KeyValuePair<byte[], byte[]>>();
        if (Options.Transformer is null)
        {
            foreach (var entry in EnumerateMergedRange(from, to))
                odsList.Add(new KeyValuePair<byte[], byte[]>((byte[])entry.Key.Clone(), entry.Value));
        }
        else
        {
            foreach (var entry in EnumerateMergedRange(from, to))
                odsList.Add(new KeyValuePair<byte[], byte[]>((byte[])entry.Key.Clone(), DecodeValue(entry.Value)));
        }
        return odsList;
    }

    private IReadOnlyList<byte[]> ScanKeysInternal(byte[]? from, byte[]? to)
    {
        if (TryScanMemTableOnlyKeys(from, to, out var fastResult))
            return fastResult;

        if (TryScanPersistedKeysLinear(from, to, out var persistedResult))
            return persistedResult;

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            var keys = _memTable.Keys;
            int start = from != null ? MemTableLowerBound(from) : 0;
            int endExclusive = to != null ? MemTableLowerBound(to) : keys.Count;
            var result = new List<byte[]>(Math.Max(0, endExclusive - start));
            for (int i = start; i < endExclusive; i++)
                result.Add((byte[])keys[i].Clone());
            return result;
        }

        var merged = BuildMergedRangeKeySet(from, to);

        if (Options.MemTableMode == MemTableMode.Hybrid)
        {
            var memKeys  = _memTable.Keys;
            var memStart = from != null ? MemTableLowerBound(from) : 0;
            var memEnd   = to   != null ? MemTableLowerBound(to)   : memKeys.Count;
            for (var mi = memStart; mi < memEnd; mi++)
                merged.Add(memKeys[mi]);

            foreach (var deleted in _memTableDeletes)
                merged.Remove(deleted);
        }

        var sorted = new List<byte[]>(merged.Count);
        foreach (var key in merged)
            sorted.Add((byte[])key.Clone());

        sorted.Sort((left, right) => _keyComparator.Compare(left, right));
        return sorted;
    }

    private bool TryScanPersistedKeysLinear(
        byte[]? from,
        byte[]? to,
        out IReadOnlyList<byte[]> result)
    {
        result = Array.Empty<byte[]>();

        if (Options.MemTableMode == MemTableMode.InMemory)
            return false;

        if (_memTable.Count != 0 || _memTableDeletes.Count != 0)
            return false;

        result = ScanPersistedKeysLinear(from, to);
        return true;
    }

    private List<byte[]> ScanPersistedKeysLinear(byte[]? from, byte[]? to)
    {
        var result = new List<byte[]>();
        var delta = _deltaTree ?? throw new InvalidOperationException("Delta tree was not initialized.");

        using var baseEnumerator = _odsTree.EnumerateRange(from, to).GetEnumerator();
        using var deltaEnumerator = delta.EnumerateRange(from, to).GetEnumerator();

        var hasBase = baseEnumerator.MoveNext();
        var hasDelta = deltaEnumerator.MoveNext();

        while (hasBase || hasDelta)
        {
            if (!hasDelta)
            {
                result.Add((byte[])baseEnumerator.Current.Key.Clone());
                hasBase = baseEnumerator.MoveNext();
                continue;
            }

            if (!hasBase)
            {
                if (TryDecodeDeltaValue(deltaEnumerator.Current.Value, out _))
                    result.Add((byte[])deltaEnumerator.Current.Key.Clone());

                hasDelta = deltaEnumerator.MoveNext();
                continue;
            }

            var keyComparison = _keyComparator.Compare(baseEnumerator.Current.Key, deltaEnumerator.Current.Key);
            if (keyComparison < 0)
            {
                result.Add((byte[])baseEnumerator.Current.Key.Clone());
                hasBase = baseEnumerator.MoveNext();
                continue;
            }

            if (keyComparison > 0)
            {
                if (TryDecodeDeltaValue(deltaEnumerator.Current.Value, out _))
                    result.Add((byte[])deltaEnumerator.Current.Key.Clone());

                hasDelta = deltaEnumerator.MoveNext();
                continue;
            }

            if (TryDecodeDeltaValue(deltaEnumerator.Current.Value, out _))
                result.Add((byte[])deltaEnumerator.Current.Key.Clone());

            hasBase = baseEnumerator.MoveNext();
            hasDelta = deltaEnumerator.MoveNext();
        }

        return result;
    }

    private async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> ScanInternalAsync(
        byte[]? from, byte[]? to, CancellationToken ct)
    {
        if (TryScanMemTableOnly(from, to, descending: false, out var fastResult))
            return fastResult;

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            var keys   = _memTable.Keys;
            var values = _memTable.Values;
            int start  = from != null ? MemTableLowerBound(from) : 0;
            var result = new List<KeyValuePair<byte[], byte[]>>();
            for (int i = start; i < keys.Count; i++)
            {
                byte[] k = keys[i];
                if (to != null && _keyComparator.Compare(k, to) >= 0) break;
                result.Add(new KeyValuePair<byte[], byte[]>((byte[])k.Clone(), DecodeValue(values[i])));
            }
            return result;
        }

        var merged = await BuildMergedRangeMapAsync(from, to, ct).ConfigureAwait(false);

        if (Options.MemTableMode == MemTableMode.Hybrid)
        {
            var memKeys = _memTable.Keys;
            var memVals = _memTable.Values;
            var memStart = from != null ? MemTableLowerBound(from) : 0;
            var memEnd   = to   != null ? MemTableLowerBound(to)   : memKeys.Count;
            for (var mi = memStart; mi < memEnd; mi++)
                merged[memKeys[mi]] = memVals[mi];
            foreach (var deleted in _memTableDeletes)
                merged.Remove(deleted);
        }

        var sorted = new List<KeyValuePair<byte[], byte[]>>(merged.Count);
        if (Options.Transformer is null)
        {
            foreach (var pair in merged)
                sorted.Add(new KeyValuePair<byte[], byte[]>((byte[])pair.Key.Clone(), pair.Value));
        }
        else
        {
            foreach (var pair in merged)
                sorted.Add(new KeyValuePair<byte[], byte[]>((byte[])pair.Key.Clone(), DecodeValue(pair.Value)));
        }

        sorted.Sort((a, b) => _keyComparator.Compare(a.Key, b.Key));
        return sorted;
    }

    private bool TryScanMemTableOnly(
        byte[]? from,
        byte[]? to,
        bool descending,
        out IReadOnlyList<KeyValuePair<byte[], byte[]>> result)
    {
        result = Array.Empty<KeyValuePair<byte[], byte[]>>();

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            result = ScanMemTableOnly(from, to, descending);
            return true;
        }

        if (Options.MemTableMode != MemTableMode.Hybrid)
            return false;

        if (Volatile.Read(ref _totalSpills) != 0)
            return false;

        if (HasPersistedEntriesInRange(_odsTree, from, to))
            return false;

        var delta = _deltaTree;
        if (delta != null && HasPersistedEntriesInRange(delta, from, to))
            return false;

        result = ScanMemTableOnly(from, to, descending);
        return true;
    }

    private bool TryScanMemTableOnlyKeys(
        byte[]? from,
        byte[]? to,
        out IReadOnlyList<byte[]> result)
    {
        result = Array.Empty<byte[]>();

        if (Options.MemTableMode == MemTableMode.InMemory)
        {
            result = ScanMemTableKeysOnly(from, to);
            return true;
        }

        if (Options.MemTableMode != MemTableMode.Hybrid)
            return false;

        if (Volatile.Read(ref _totalSpills) != 0)
            return false;

        if (HasPersistedEntriesInRange(_odsTree, from, to))
            return false;

        var delta = _deltaTree;
        if (delta != null && HasPersistedEntriesInRange(delta, from, to))
            return false;

        result = ScanMemTableKeysOnly(from, to);
        return true;
    }

    private List<KeyValuePair<byte[], byte[]>> ScanMemTableOnly(byte[]? from, byte[]? to, bool descending)
    {
        var keys = _memTable.Keys;
        var values = _memTable.Values;
        var start = from != null ? MemTableLowerBound(from) : 0;
        var endExclusive = to != null ? MemTableLowerBound(to) : keys.Count;
        var result = new List<KeyValuePair<byte[], byte[]>>(Math.Max(0, endExclusive - start));

        if (descending)
        {
            for (var i = endExclusive - 1; i >= start; i--)
            {
                var key = keys[i];
                result.Add(new KeyValuePair<byte[], byte[]>((byte[])key.Clone(), DecodeValue(values[i])));
            }

            return result;
        }

        for (var i = start; i < endExclusive; i++)
        {
            var key = keys[i];
            result.Add(new KeyValuePair<byte[], byte[]>((byte[])key.Clone(), DecodeValue(values[i])));
        }

        return result;
    }

    private List<byte[]> ScanMemTableKeysOnly(byte[]? from, byte[]? to)
    {
        var keys = _memTable.Keys;
        var start = from != null ? MemTableLowerBound(from) : 0;
        var endExclusive = to != null ? MemTableLowerBound(to) : keys.Count;
        var result = new List<byte[]>(Math.Max(0, endExclusive - start));

        for (var i = start; i < endExclusive; i++)
            result.Add((byte[])keys[i].Clone());

        return result;
    }

    private static bool HasPersistedEntriesInRange(BPlusTree tree, byte[]? from, byte[]? to)
    {
        using var enumerator = tree.EnumerateRange(from, to).GetEnumerator();
        return enumerator.MoveNext();
    }

    // Called inside the write lock � must NOT re-acquire it.
    private void TriggerAutoCheckpointIfNeeded()
    {
        if (Options.AutoCheckpointWalThresholdBytes <= 0) return;
        if (_walLog.SizeBytes < Options.AutoCheckpointWalThresholdBytes) return;

        _logger?.LogDebug(
            "Auto-checkpoint triggered (WAL {WalBytes} >= threshold {Threshold}).",
            _walLog.SizeBytes, Options.AutoCheckpointWalThresholdBytes);

        CheckpointInternalAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task TriggerAutoCheckpointIfNeededAsync(CancellationToken ct)
    {
        if (Options.AutoCheckpointWalThresholdBytes <= 0) return;
        if (_walLog.SizeBytes < Options.AutoCheckpointWalThresholdBytes) return;

        _logger?.LogDebug(
            "Auto-checkpoint triggered (WAL {WalBytes} >= threshold {Threshold}).",
            _walLog.SizeBytes, Options.AutoCheckpointWalThresholdBytes);

        await CheckpointInternalAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Auto-checkpoint called from the flush loop, outside any write lock.
    /// Uses a non-blocking semaphore try: if a checkpoint is already running, this is a no-op
    /// because the running checkpoint will truncate the WAL when it completes.
    /// </summary>
    private async Task TriggerAutoCheckpointOutsideLockAsync(CancellationToken ct)
    {
        if (Options.AutoCheckpointWalThresholdBytes <= 0) return;
        if (_walLog.SizeBytes < Options.AutoCheckpointWalThresholdBytes) return;

        _logger?.LogDebug(
            "Auto-checkpoint triggered outside lock (WAL {WalBytes} >= threshold {Threshold}).",
            _walLog.SizeBytes, Options.AutoCheckpointWalThresholdBytes);

        if (!await _checkpointSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger?.LogDebug("Auto-checkpoint skipped – another checkpoint is already running.");
            return;
        }

        try
        {
            await RunCheckpointAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _checkpointSemaphore.Release();
        }
    }

    /// <summary>
    /// Dispatches to the mode-appropriate checkpoint implementation.
    /// For <see cref="MemTableMode.InMemory"/> + <see cref="OdsUpdateMode.CheckpointOnly"/>
    /// (the default embedded configuration) the snapshot-based path is used, holding the
    /// write lock only for two brief critical sections rather than for the full ODS rebuild.
    /// All other modes fall back to the legacy full-lock checkpoint.
    /// </summary>
    private async Task RunCheckpointAsync(CancellationToken ct)
    {
        if (Options.MemTableMode == MemTableMode.InMemory
            && Options.OdsUpdateMode == OdsUpdateMode.CheckpointOnly)
        {
            await CheckpointInMemorySnapshotAsync(ct).ConfigureAwait(false);
            return;
        }

        // Legacy path: hold the write lock for the complete checkpoint duration.
        await _lock.EnterWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            await CheckpointInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Snapshot-based checkpoint for <see cref="MemTableMode.InMemory"/> +
    /// <see cref="OdsUpdateMode.CheckpointOnly"/> (the default embedded mode).
    /// <para>
    /// The write lock is held for exactly two brief critical sections:
    /// <list type="bullet">
    ///   <item><b>Phase 1</b> – copy MemTable entry references to a local array (μs range;
    ///         no byte-array cloning, only struct copies).</item>
    ///   <item><b>Phase 3</b> – swap the rebuilt ODS into place, persist the full MemTable
    ///         state to the checkpoint store, and truncate the WAL (ms range).</item>
    /// </list>
    /// Phase 2 (ODS rebuild) runs completely lock-free: reads and writes that arrive while
    /// the new B+Tree is being written to disk are unaffected.
    /// </para>
    /// <para>
    /// Crash safety:
    /// <list type="bullet">
    ///   <item>Crash before Phase 3: temp ODS file is cleaned at startup; recovery uses old
    ///         ODS + checkpoint store + WAL.</item>
    ///   <item>Crash inside Phase 3 after checkpoint-store write but before WAL truncation:
    ///         recovery re-applies the WAL (idempotent upserts, correct result).</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task CheckpointInMemorySnapshotAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger?.LogDebug("Snapshot checkpoint started (InMemory/CheckpointOnly).");

        // Phase 1 – brief write lock: snapshot MemTable entry references.
        // KeyValuePair<byte[], byte[]> copies only references, not the byte arrays themselves.
        KeyValuePair<byte[], byte[]>[] snapshot;
        await _lock.EnterWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _memTable.Count == 0
                ? Array.Empty<KeyValuePair<byte[], byte[]>>()
                : _memTable.ToArray();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Phase 2 – lock-free ODS rebuild from snapshot.
        // Reads and writes proceed normally while the B+Tree pages are serialised to disk.
        await BuildOdsTmpFromEntriesAsync(snapshot, ct).ConfigureAwait(false);

        // Phase 3 – brief write lock: activate new ODS, persist full MemTable, truncate WAL.
        // _checkpointStore.Save uses the CURRENT _memTable (not the snapshot) so that writes
        // committed after Phase 1 are also covered, making full WAL truncation safe.
        await _lock.EnterWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            SwapTmpOdsToLive();
            _checkpointStore.Save(_memTable);
            _walLog.Truncate();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        sw.Stop();
        _lastCheckpointDuration = sw.Elapsed;
        Interlocked.Increment(ref _totalCheckpoints);
        _logger?.LogDebug("Snapshot checkpoint complete in {Duration}ms. WAL truncated.", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Builds a fresh B+Tree into the temp ODS file from <paramref name="entries"/> without
    /// swapping it into the live file.  Call <see cref="SwapTmpOdsToLive"/> under a write lock
    /// to activate the result.
    /// </summary>
    private async Task BuildOdsTmpFromEntriesAsync(
        IEnumerable<KeyValuePair<byte[], byte[]>> entries, CancellationToken ct)
    {
        var tmpPath = Options.OdsTmpFilePath;
        SafeDeleteFile(tmpPath);

        using var tmpTree = new BPlusTree(
            new OdsPager(tmpPath, Options.OdsPageSizeBytes, pageCacheCapacity: 0),
            _keyComparator);

        foreach (var entry in entries)
            await tmpTree.UpsertAsync(entry.Key, entry.Value, ct).ConfigureAwait(false);

        tmpTree.Flush();
    }

    /// <summary>
    /// Atomically replaces the live ODS file with the pre-built temp ODS and reopens
    /// <see cref="_odsTree"/> against the new file.  Must be called under the write lock.
    /// </summary>
    private void SwapTmpOdsToLive()
    {
        _odsTree.Dispose();
        File.Move(Options.OdsTmpFilePath, Options.OdsFilePath, overwrite: true);
        _odsTree = new BPlusTree(
            new OdsPager(Options.OdsFilePath, Options.OdsPageSizeBytes, Options.PageCacheCapacity),
            _keyComparator);
        WriteOdsMetadata();
    }

    /// <summary>
    /// Scans every entry in <see cref="_odsTree"/> and builds a fresh <see cref="BloomFilter"/>
    /// that covers all keys currently on disk.  Called after Recover() and after each ODS rebuild
    /// or compaction so that negative lookups in TryGet can skip the BPlusTree read.
    /// </summary>
    private BloomFilter BuildOdsBloomFromScan()
    {
        // Size generously: 2× the MemTable estimate to accommodate a larger ODS.
        var bloom = new BloomFilter(Math.Max(1_000_000, (int)(Options.HybridMemTableMaxBytes / 50)));
        foreach (var entry in _odsTree.EnumerateEntries())
            bloom.Add(entry.Key);
        return bloom;
    }

    /// <summary>Internal checkpoint logic extracted so it can be called from both
    /// <see cref="Checkpoint"/> and <see cref="CheckpointAsync"/> without duplicating
    /// semaphore management.</summary>
    private async Task CheckpointInternalAsync(CancellationToken ct)
    {
        _logger?.LogDebug("Checkpoint started (MemTableMode={Mode}, OdsUpdateMode={OdsMode}).",
            Options.MemTableMode, Options.OdsUpdateMode);

        var sw = Stopwatch.StartNew();

        if (UsesDeltaTree(Options.MemTableMode))
        {
            if (Options.MemTableMode == MemTableMode.Hybrid)
                await SpillMemTableOverlayToDeltaAsync(ct).ConfigureAwait(false);
            await ApplyDeltaIntoBaseIncrementalAsync(ct).ConfigureAwait(false);
        }
        else
        {
            if (Options.OdsUpdateMode == OdsUpdateMode.CheckpointOnly)
                await RebuildOdsFromMemTableAsync(ct).ConfigureAwait(false);
            _checkpointStore.Save(_memTable);
        }

        _walLog.Truncate();

        sw.Stop();
        _lastCheckpointDuration = sw.Elapsed;
        _totalCheckpoints++;

        _logger?.LogDebug("Checkpoint complete in {Duration}ms. WAL truncated.", sw.ElapsedMilliseconds);
    }

    private static byte[] EncodeDeltaPutValue(byte[] value)
    {
        var encoded = new byte[value.Length + 1];
        encoded[0] = DeltaPutMarker;
        value.CopyTo(encoded.AsSpan(1));
        return encoded;
    }

    private static bool TryDecodeDeltaValue(byte[] encoded, out byte[] value)
    {
        if (encoded.Length == 0)
            throw new InvalidDataException("Delta value payload is invalid.");

        if (encoded[0] == DeltaDeleteMarker)
        {
            value = Array.Empty<byte>();
            return false;
        }

        if (encoded[0] != DeltaPutMarker)
            throw new InvalidDataException("Unknown delta value marker.");

        value = encoded.AsSpan(1).ToArray();
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalhallaStore));
    }

    // Binary lower-bound search in _memTable.Keys: returns the index of the first
    // key that is >= target according to the store's key comparator.  O(log n).
    // Must be called while holding _lock (read or write).
    private int MemTableLowerBound(byte[] target)
    {
        var keys = _memTable.Keys;
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_keyComparator.Compare(keys[mid], target) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private void SetMemTableValue(byte[] key, byte[] value)
    {
        if (_memTableDeletes.Remove(key))
            _memTableApproxBytes -= EstimateDeleteBytes(key);

        if (_memTable.TryGetValue(key, out var existing))
            _memTableApproxBytes -= EstimateEntryBytes(key, existing);

        _memTable[key] = value;
        _memTableApproxBytes += EstimateEntryBytes(key, value);
        _memTableBloom.Add(key);
    }

    private void SetMemTableDelete(byte[] key)
    {
        if (_memTable.TryGetValue(key, out var existing))
        {
            _memTableApproxBytes -= EstimateEntryBytes(key, existing);
            _memTable.Remove(key);
        }

        if (_memTableDeletes.Add(key))
            _memTableApproxBytes += EstimateDeleteBytes(key);
    }

    private static long EstimateEntryBytes(byte[] key, byte[] value) => key.Length + value.Length + 32L;

    private static long EstimateDeleteBytes(byte[] key) => key.Length + 16L;

    private static bool UsesDeltaTree(MemTableMode mode)
    {
        return mode == MemTableMode.OnDiskBPlusTree || mode == MemTableMode.Hybrid;
    }

    /// <summary>
    /// Validates that the <see cref="MemTableMode"/> and <see cref="OdsUpdateMode"/> combination
    /// in <paramref name="options"/> is semantically sensible. Throws <see cref="InvalidOperationException"/>
    /// for combinations that would produce redundant double-writes without any benefit.
    /// </summary>
    internal static void ValidateModeCompatibility(WalhallaOptions options)
    {
        if (options.OdsUpdateMode == OdsUpdateMode.Immediate &&
            (options.MemTableMode == MemTableMode.OnDiskBPlusTree ||
             options.MemTableMode == MemTableMode.Hybrid))
        {
            throw new InvalidOperationException(
                $"The combination of OdsUpdateMode.Immediate with MemTableMode.{options.MemTableMode} is not " +
                "supported. OdsUpdateMode.Immediate writes every operation directly to the ODS B+Tree, while " +
                $"MemTableMode.{options.MemTableMode} additionally buffers writes in a delta tree. " +
                "This results in redundant double-writes without any durability or performance benefit. " +
                "Use OdsUpdateMode.CheckpointOnly instead.");
        }
    }

    /// <summary>
    /// Verifies that a key+value pair fits inside a single ODS leaf-page body.
    /// An entry occupies <c>8 + key.Length + effectiveValueLength</c> bytes in the page body,
    /// where <c>effectiveValueLength = value.Length + 1</c> in delta-tree modes (one marker byte overhead).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the entry is too large.</exception>
    // ── Transformer helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the transformer-encoded form of <paramref name="value"/>.
    /// Returns the original array when no transformer is configured.
    /// </summary>
    private byte[] EncodeValue(byte[] value)
        => Options.Transformer is { } t ? t.Encode(value) : value;

    /// <summary>
    /// Returns the transformer-decoded form of <paramref name="stored"/>.
    /// Returns the original array when no transformer is configured.
    /// </summary>
    private byte[] DecodeValue(byte[] stored)
        => Options.Transformer is { } t ? t.Decode(stored) : stored;

    internal void ValidateEntrySize(byte[] key, byte[] value)
    {
        // Page body = pageSize - header(17) - checksum(4) = pageSize - 21.
        // Each leaf entry consumes: 4(keyLen field) + 4(valLen field) + key.Length + effectiveValue.Length.
        // Delta-encoded values have a 1-byte marker prefix ? effectiveValueLength = value.Length + 1.
        const int headerBytes   = 17; // OdsPageHeader.SizeInBytes
        const int checksumBytes =  4; // OdsPage.ChecksumSizeInBytes
        const int entryOverhead =  8; // two sizeof(int) length fields
        const int deltaMarker   =  1; // EncodeDeltaPutValue prefix byte

        var bodySize       = Options.OdsPageSizeBytes - headerBytes - checksumBytes;
        var markerOverhead = UsesDeltaTree(Options.MemTableMode) ? deltaMarker : 0;
        var required       = entryOverhead + key.Length + value.Length + markerOverhead;

        if (required > bodySize)
        {
            var maxCombined = bodySize - entryOverhead - markerOverhead;
            throw new ArgumentException(
                $"The combined key+value size ({key.Length + value.Length} bytes) exceeds the maximum " +
                $"of {maxCombined} bytes for the configured ODS page size of {Options.OdsPageSizeBytes} bytes. " +
                "Increase OdsPageSizeBytes or reduce the key/value size.");
        }
    }

    private static void CopyIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return;

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void SafeDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { /* already gone � fine */ }
        catch (DirectoryNotFoundException) { /* already gone � fine */ }
    }

    private void WarnAndDeleteStaleFile(string path)
    {
        if (!File.Exists(path)) return;
        _logger?.LogWarning(
            "Stale compaction file '{Path}' found � last checkpoint was interrupted. Removing.", path);
        SafeDeleteFile(path);
    }

    private void WriteOdsMetadata()
    {
        var metadata = new OdsStorageMetadata(CurrentOdsFormatVersion, _keyComparator.Id, Options.OdsPageSizeBytes);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Options.OdsMetadataFilePath, json);
    }

    private static IKeyComparator ResolveKeyComparator(WalhallaOptions options)
    {
        if (BuiltInKeyComparators.All.TryGetValue(options.KeyComparatorId, out var builtIn))
            return builtIn;

        if (options.CustomKeyComparators != null &&
            options.CustomKeyComparators.TryGetValue(options.KeyComparatorId, out var custom))
        {
            if (!string.Equals(custom.Id, options.KeyComparatorId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Custom comparator id mismatch: expected '{options.KeyComparatorId}', got '{custom.Id}'.");

            return custom;
        }

        throw new InvalidOperationException(
            $"Unknown key comparator '{options.KeyComparatorId}'. Register it via '{nameof(WalhallaOptions.CustomKeyComparators)}' " +
            $"or use one of: {string.Join(", ", BuiltInKeyComparators.All.Keys)}");
    }

    private static void EnsureOrValidateOdsMetadata(WalhallaOptions options, IKeyComparator comparator)
    {
        if (File.Exists(options.OdsMetadataFilePath))
        {
            var json = File.ReadAllText(options.OdsMetadataFilePath);
            var metadata = JsonSerializer.Deserialize<OdsStorageMetadata>(json)
                ?? throw new InvalidDataException("ODS metadata file is invalid.");

            if (metadata.OdsFormatVersion != CurrentOdsFormatVersion)
                throw new InvalidOperationException(
                    $"ODS format version mismatch. Expected {CurrentOdsFormatVersion}, found {metadata.OdsFormatVersion}. " +
                    "Use a fresh data directory or migrate your data from the old format.");

            if (!string.Equals(metadata.KeyComparatorId, comparator.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Key comparator mismatch. DB uses '{metadata.KeyComparatorId}', requested '{comparator.Id}'.");

            if (metadata.OdsPageSizeBytes != options.OdsPageSizeBytes)
                throw new InvalidOperationException(
                    $"ODS page size mismatch. DB uses {metadata.OdsPageSizeBytes}, requested {options.OdsPageSizeBytes}.");

            return;
        }

        // Legacy compatibility: if metadata is missing and there is already data, only allow default comparator.
        if (File.Exists(options.OdsFilePath) && new FileInfo(options.OdsFilePath).Length > 0 &&
            !string.Equals(comparator.Id, BuiltInKeyComparators.BytewiseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Existing ODS file has no metadata. Opening with non-default comparator is blocked. " +
                "Open once with default comparator or migrate/rebuild.");
        }

        var created = new OdsStorageMetadata(CurrentOdsFormatVersion, comparator.Id, options.OdsPageSizeBytes);
        var createdJson = JsonSerializer.Serialize(created, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.OdsMetadataFilePath, createdJson);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel the CTS *before* completing the channel writer.  This causes the
        // FlushLoop's next await (WaitToReadAsync / Task.Delay / AppendGroupAsync)
        // to throw OperationCanceledException immediately, which drives it into the
        // finally-block where it fails all orphaned TCS entries.  Without this
        // ordering, any thread blocked on pending.Tcs.Task (inside CommitAsync)
        // would wait forever for a FlushLoop that can never make progress —
        // a classic ThreadPool-starvation deadlock.
        _flushLoopCts?.Cancel();

        // Signal the channel as complete so the flush loop drains remaining items then exits.
        _commitQueue.Writer.TryComplete();

        // Eagerly fail items that are already in the queue but haven't been picked up yet.
        // The FlushLoop's finally-block does the same for items enqueued after the loop sees
        // the channel complete; here we cover the window between Cancel() and TryComplete().
        var disposeEx = new ObjectDisposedException(
            nameof(WalhallaStore),
            "WalhallaStore was disposed before this transaction could be committed.");
        while (_commitQueue.Reader.TryRead(out var orphan))
            orphan.Tcs.TrySetException(disposeEx);

        // Now safe to block: the FlushLoop runs on its own dedicated OS thread (LongRunning),
        // so sync-blocking here does NOT starve the ThreadPool.
        try { _flushLoopTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* errors were already propagated to individual TCS */ }

        _walLog.Dispose();
        _deltaTree?.Dispose();
        _odsTree.Dispose();
        _lock.Dispose();
        _checkpointSemaphore.Dispose();
        _flushLoopCts?.Dispose();
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // IKeyValueStore-Vertrag (M3)
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>Alias für <see cref="Put"/>, um den gemeinsamen Vertrag zu erfüllen.</summary>
    public void Upsert(byte[] key, byte[] value) => Put(key, value);

    /// <summary>Streamender Bereichsscan (Vertrag).</summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> IKeyValueStore.Scan(
        byte[]? fromInclusive, byte[]? toExclusive)
        => Scan(fromInclusive, toExclusive);

    /// <summary>Streamender Präfix-Scan (Vertrag).</summary>
    IEnumerable<KeyValuePair<byte[], byte[]>> IKeyValueStore.ScanPrefix(byte[] prefix)
        => ScanPrefix(prefix);

    public void ScanValues(byte[]? fromInclusive, byte[]? toExclusive,
        Func<byte[], int, int, bool> action)
    {
        var list = Scan(fromInclusive, toExclusive);
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i].Value;
            if (!action(v, 0, v.Length))
                break;
        }
    }

    public void BulkUpsert(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        foreach (var kv in entries)
            Put(kv.Key, kv.Value);
    }

    public void BulkDelete(IReadOnlyList<byte[]> keys)
    {
        foreach (var key in keys)
            Delete(key);
    }

    public IStorageTransaction BeginTransaction(IsolationLevel isolation)
        => new WalhallaTransactionAdapter(BeginTransaction(), this);

    public IReadSnapshot BeginReadSnapshot()
        => new WalhallaReadSnapshotAdapter(this);

    public void Vacuum() { }

    StorageDiagnostics IKeyValueStore.GetDiagnostics()
    {
        var d = GetDiagnostics();
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
            TotalGroupedTransactions = d.TotalGroupedTransactions
        };
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // Adapter: WalhallaTransaction → IStorageTransaction
    // ═════════════════════════════════════════════════════════════════════════════

    private sealed class WalhallaTransactionAdapter : IStorageTransaction
    {
        private readonly WalhallaTransaction _tx;
        private readonly WalhallaStore _store;
        private bool _disposed;

        public ulong TxId => (ulong)_tx.TransactionId;
        public ulong Sequence => (ulong)_tx.TransactionId;
        public TransactionStatus Status => TransactionStatus.Active;

        public WalhallaTransactionAdapter(WalhallaTransaction tx, WalhallaStore store)
        {
            _tx = tx;
            _store = store;
        }

        public bool TryGet(byte[] key, out byte[]? value)
            => _store.TryGet(key, out value);

        public IEnumerable<KeyValuePair<byte[], byte[]>> Scan(
            byte[]? fromInclusive = null, byte[]? toExclusive = null)
            => _store.Scan(fromInclusive, toExclusive);

        public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefix(byte[] prefix)
            => _store.ScanPrefix(prefix);

        public void Upsert(byte[] key, byte[] value)
            => _tx.Put(key, value);

        public void Delete(byte[] key)
            => _tx.Delete(key);

        public void Commit()
            => _tx.Commit();

        public void Rollback()
            => _tx.Rollback();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tx.Dispose();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // Adapter: WalhallaStore → IReadSnapshot
    // ═════════════════════════════════════════════════════════════════════════════

    private sealed class WalhallaReadSnapshotAdapter : IReadSnapshot
    {
        private readonly WalhallaStore _store;

        public ulong Sequence => 0;

        public WalhallaReadSnapshotAdapter(WalhallaStore store)
            => _store = store;

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
    /// Represents one transaction that has been submitted to the group-commit queue but not
    /// yet durably written.  The flush loop completes <see cref="Tcs"/> once the transaction
    /// is safely on disk and applied to the in-memory state.
    /// </summary>
    private sealed class PendingGroupCommit
    {
        public PendingGroupCommit(long transactionId, IReadOnlyList<WalOperation> operations)
        {
            TransactionId = transactionId;
            Operations    = operations;
        }

        public long                          TransactionId { get; }
        public IReadOnlyList<WalOperation>   Operations    { get; }

        /// <summary>
        /// Completed (result = <see langword="true"/>) once flushed successfully, or faulted on error.
        /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> ensures completions run
        /// on the thread pool, not on the flush-loop thread.
        /// </summary>
        public TaskCompletionSource<bool> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct BackupManifest(
        DateTimeOffset CreatedAtUtc,
        int OdsFormatVersion,
        string KeyComparatorId,
        MemTableMode MemTableMode,
        OdsUpdateMode OdsUpdateMode,
        int OdsPageSizeBytes,
        string WalFileName,
        string CheckpointFileName,
        string OdsFileName,
        string DeltaFileName,
        string OdsMetadataFileName);

    private sealed record OdsStorageMetadata(
        int OdsFormatVersion,
        string KeyComparatorId,
        int OdsPageSizeBytes);
}
