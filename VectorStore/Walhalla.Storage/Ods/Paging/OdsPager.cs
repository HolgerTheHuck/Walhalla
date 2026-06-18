// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Ods.Pages;

namespace Walhalla.Storage.Ods.Paging;

/// <summary>
/// Manages page-level I/O against a single on-disk file using <see cref="System.IO.RandomAccess"/>
/// for positioned async reads and writes.  Optionally caches hot pages in an LRU page cache to
/// reduce disk reads during repeated B+Tree traversals.
/// Page buffers are rented from <see cref="ArrayPool{T}.Shared"/> and returned when the
/// caller disposes the <see cref="OdsPage"/>.
/// </summary>
internal sealed class OdsPager : IDisposable
{
    public const int MetadataPageId = 0;

    // --- I/O -----------------------------------------------------------------
    private readonly FileStream _stream;
    private OdsRootMetadata _rootMetadata;

    // --- LRU page cache ------------------------------------------------------
    private readonly int _pageCacheCapacity;
    private readonly Dictionary<int, (LinkedListNode<int> Node, byte[] Buffer)>? _cacheIndex;
    private readonly LinkedList<int>? _cacheLru;         // front = MRU, back = LRU

    // --- Write batch ---------------------------------------------------------
    // The dictionary is allocated ONCE at construction and reused for every
    // batch; only the buffers inside it are rented/returned per operation.
    // _writeBatchActive == true means writes should be deferred into _writeBatch.
    private readonly Dictionary<int, byte[]> _writeBatch = new(8);
    private bool _writeBatchActive;

    // --- Legacy V1 read mode --------------------------------------------------
    // When true: checksum verification is skipped on reads and OdsPage.Body uses
    // the full post-header region (no 4-byte checksum reservation).
    // Used exclusively by MigrateOdsFormat to read V1 (pre-checksum) ODS files.
    private readonly bool _legacyV1Mode;
    private readonly int  _checksumReserveBytes;

    public OdsPager(string path, int pageSize, int pageCacheCapacity = 0, bool legacyV1Mode = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        if (pageSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be at least 1024 bytes.");
        if (pageCacheCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(pageCacheCapacity), "Page cache capacity must be >= 0.");

        FilePath = path;
        PageSize = pageSize;
        _pageCacheCapacity = pageCacheCapacity;
        _legacyV1Mode = legacyV1Mode;
        _checksumReserveBytes = legacyV1Mode ? 0 : OdsPage.ChecksumSizeInBytes;

        if (pageCacheCapacity > 0)
        {
            _cacheIndex = new Dictionary<int, (LinkedListNode<int>, byte[])>(pageCacheCapacity + 4);
            _cacheLru = new LinkedList<int>();
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // FileOptions.Asynchronous: enables OS-level async I/O (IOCP on Windows).
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                 FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);

        if (_stream.Length == 0)
            Initialize();

        // Sync read for the single one-time metadata access at construction time.
        using var metaPage = ReadPageSync(MetadataPageId);
        _rootMetadata = OdsRootMetadata.Read(metaPage.Body);
    }

    public int PageSize { get; }
    public string FilePath { get; } = string.Empty;

    // --- Root metadata --------------------------------------------------------

    public OdsRootMetadata ReadRootMetadata() => _rootMetadata;

    public void WriteRootMetadata(OdsRootMetadata metadata)
    {
        var page = ReadPage(MetadataPageId);
        metadata.Write(page.Body);
        WritePage(page);
        page.Dispose();
        _rootMetadata = metadata;
    }

    public async ValueTask WriteRootMetadataAsync(OdsRootMetadata metadata, CancellationToken ct = default)
    {
        using var page = await ReadPageAsync(MetadataPageId, ct).ConfigureAwait(false);
        metadata.Write(page.Body);
        await WritePageAsync(page, ct).ConfigureAwait(false);
        _rootMetadata = metadata;
    }

    // --- Read page ------------------------------------------------------------

    /// <summary>
    /// Synchronously reads a page.  Priority order: write-batch ? LRU cache ? disk.
    /// The caller must dispose the returned <see cref="OdsPage"/>.
    /// </summary>
    public OdsPage ReadPage(int pageId)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        // 1. Check write-batch (pages not yet flushed to disk).
        if (_writeBatchActive && _writeBatch.TryGetValue(pageId, out var batchBuf))
        {
            var rented = ArrayPool<byte>.Shared.Rent(PageSize);
            batchBuf.AsSpan(0, PageSize).CopyTo(rented);
            return CreatePage(pageId, rented, pooled: true);
        }

        // 2. Check LRU cache BEFORE ValidatePageId — ein Cache-Hit vermeidet
        // den _stream.Length-Syscall, der bei read-heavy Workloads signifikant ist.
        if (_pageCacheCapacity > 0 && TryGetFromCache(pageId, out var cached))
        {
            // cached is already a fresh copy made inside the cache lock.
            return CreatePage(pageId, cached, pooled: false);
        }

        ValidatePageId(pageId);

        // 3. Read from disk.
        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        var fileOffset = (long)pageId * PageSize;
        var totalRead = 0;
        while (totalRead < PageSize)
        {
            var chunk = RandomAccess.Read(_stream.SafeFileHandle,
                            buffer.AsSpan(totalRead, PageSize - totalRead),
                            fileOffset + totalRead);
            if (chunk == 0) break;
            totalRead += chunk;
        }

        VerifyPageChecksum(buffer, pageId);

        if (_pageCacheCapacity > 0)
            AddOrUpdateCache(pageId, buffer.AsSpan(0, PageSize));

        return CreatePage(pageId, buffer, pooled: true);
    }

    /// <summary>
    /// Asynchronously reads a page.  Priority order: write-batch ? LRU cache ? disk.
    /// The caller must dispose the returned <see cref="OdsPage"/>.
    /// </summary>
    public async ValueTask<OdsPage> ReadPageAsync(int pageId, CancellationToken ct = default)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        // 1. Check write-batch.
        if (_writeBatchActive && _writeBatch.TryGetValue(pageId, out var batchBuf))
        {
            var rented = ArrayPool<byte>.Shared.Rent(PageSize);
            batchBuf.AsSpan(0, PageSize).CopyTo(rented);
            return CreatePage(pageId, rented, pooled: true);
        }

        // 2. Check LRU cache BEFORE ValidatePageId.
        if (_pageCacheCapacity > 0 && TryGetFromCache(pageId, out var cached))
        {
            // cached is already a fresh copy made inside the cache lock.
            return CreatePage(pageId, cached, pooled: false);
        }

        ValidatePageId(pageId);

        // 3. Read from disk.
        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        var fileOffset = (long)pageId * PageSize;
        var totalRead = 0;
        while (totalRead < PageSize)
        {
            var chunk = await RandomAccess.ReadAsync(_stream.SafeFileHandle,
                            buffer.AsMemory(totalRead, PageSize - totalRead),
                            fileOffset + totalRead, ct).ConfigureAwait(false);
            if (chunk == 0) break;
            totalRead += chunk;
        }

        VerifyPageChecksum(buffer, pageId);

        if (_pageCacheCapacity > 0)
            AddOrUpdateCache(pageId, buffer.AsSpan(0, PageSize));

        return CreatePage(pageId, buffer, pooled: true);
    }

    // --- Write page ------------------------------------------------------------

    /// <summary>
    /// Writes a page.  When a write-batch is active the page is buffered in an
    /// ArrayPool-backed dict entry; otherwise it is written directly to disk.
    /// The FNV-1a checksum is stamped into <c>page.Buffer[PageSize-4..PageSize]</c>
    /// before any I/O so that the checksum slot (which is outside <c>Body</c>)
    /// always reflects the current page content.
    /// </summary>
    public void WritePage(OdsPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (page.Buffer.Length < PageSize)
            throw new InvalidOperationException("Page buffer is smaller than the pager page size.");

        StampChecksum(page.Buffer, PageSize);

        if (_writeBatchActive)
        {
            var span = page.Buffer.AsSpan(0, PageSize);
            if (!_writeBatch.TryGetValue(page.PageId, out var batchBuf))
            {
                batchBuf = ArrayPool<byte>.Shared.Rent(PageSize);
                _writeBatch[page.PageId] = batchBuf;
            }
            span.CopyTo(batchBuf);
            return;
        }

        RandomAccess.Write(_stream.SafeFileHandle,
            page.Buffer.AsSpan(0, PageSize),
            (long)page.PageId * PageSize);

        if (_pageCacheCapacity > 0)
            UpdateCacheIfPresent(page.PageId, page.Buffer.AsSpan(0, PageSize));
    }

    /// <summary>
    /// Asynchronously writes a page.  Same batching policy as <see cref="WritePage"/>.
    /// </summary>
    public async ValueTask WritePageAsync(OdsPage page, CancellationToken ct = default)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (page.Buffer.Length < PageSize)
            throw new InvalidOperationException("Page buffer is smaller than the pager page size.");

        StampChecksum(page.Buffer, PageSize);

        // Batch mode: buffer synchronously (batch is flushed by CommitWriteBatchAsync).
        if (_writeBatchActive)
        {
            if (!_writeBatch.TryGetValue(page.PageId, out var batchBuf))
            {
                batchBuf = ArrayPool<byte>.Shared.Rent(PageSize);
                _writeBatch[page.PageId] = batchBuf;
            }
            page.Buffer.AsSpan(0, PageSize).CopyTo(batchBuf);
            return;
        }

        await RandomAccess.WriteAsync(_stream.SafeFileHandle,
            page.Buffer.AsMemory(0, PageSize),
            (long)page.PageId * PageSize, ct).ConfigureAwait(false);

        if (_pageCacheCapacity > 0)
            UpdateCacheIfPresent(page.PageId, page.Buffer.AsSpan(0, PageSize));
    }

    // --- Allocate page --------------------------------------------------------

    /// <summary>
    /// Synchronously allocates a new page; caller must dispose the returned page.
    /// </summary>
    public OdsPage AllocatePage(OdsPageType pageType, int parentPageId = -1)
    {
        var metadata = ReadRootMetadata();
        var nextPageId = metadata.LastAllocatedPageId + 1;

        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(buffer, 0, PageSize);
        var page = CreatePage(nextPageId, buffer, pooled: true);
        page.Header = new OdsPageHeader(nextPageId, pageType, parentPageId, -1, 0);

        WritePage(page);
        WriteRootMetadata(new OdsRootMetadata(metadata.RootPageId, nextPageId));
        return page;
    }

    /// <summary>
    /// Asynchronously allocates a new page; caller must dispose the returned page.
    /// </summary>
    public async ValueTask<OdsPage> AllocatePageAsync(OdsPageType pageType, int parentPageId = -1,
                                                      CancellationToken ct = default)
    {
        var metadata = ReadRootMetadata();
        var nextPageId = metadata.LastAllocatedPageId + 1;

        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(buffer, 0, PageSize);
        var page = CreatePage(nextPageId, buffer, pooled: true);
        page.Header = new OdsPageHeader(nextPageId, pageType, parentPageId, -1, 0);

        await WritePageAsync(page, ct).ConfigureAwait(false);
        await WriteRootMetadataAsync(new OdsRootMetadata(metadata.RootPageId, nextPageId), ct).ConfigureAwait(false);
        return page;
    }

    // --- Flush + Dispose ------------------------------------------------------

    public void Flush() => _stream.Flush(true);

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Flush(true);
        _stream.Dispose();
    }

    // --- Write-batch API ------------------------------------------------------

    /// <summary>
    /// Starts buffering writes.  All subsequent <see cref="WritePage"/> /
    /// <see cref="WritePageAsync"/> calls accumulate in an in-memory dict until
    /// <see cref="CommitWriteBatch"/> or <see cref="CommitWriteBatchAsync"/> is called.
    /// <para>Reads during an active batch are served from the batch first, so
    /// operations that write then re-read the same page (e.g. WriteRootMetadata)
    /// see their own uncommitted writes correctly.</para>
    /// </summary>
    public void BeginWriteBatch()
        => _writeBatchActive = true;

    /// <summary>
    /// Flushes all buffered pages to disk in ascending page-id order (so file
    /// extension is always sequential), updates the LRU cache for any page
    /// already present, and returns batch buffers to <see cref="ArrayPool{T}.Shared"/>.
    /// The internal dictionary is retained and reused for the next batch.
    /// </summary>
    public void CommitWriteBatch()
    {
        _writeBatchActive = false;
        if (_writeBatch.Count == 0) return;

        foreach (var pageId in _writeBatch.Keys.OrderBy(id => id))
        {
            var buf = _writeBatch[pageId];
            RandomAccess.Write(_stream.SafeFileHandle, buf.AsSpan(0, PageSize), (long)pageId * PageSize);
            if (_pageCacheCapacity > 0) UpdateCacheIfPresent(pageId, buf.AsSpan(0, PageSize));
            ArrayPool<byte>.Shared.Return(buf);
        }
        _writeBatch.Clear();
    }

    /// <summary>Async variant of <see cref="CommitWriteBatch"/>.</summary>
    public async ValueTask CommitWriteBatchAsync(CancellationToken ct = default)
    {
        _writeBatchActive = false;
        if (_writeBatch.Count == 0) return;

        foreach (var pageId in _writeBatch.Keys.OrderBy(id => id))
        {
            var buf = _writeBatch[pageId];
            await RandomAccess.WriteAsync(_stream.SafeFileHandle, buf.AsMemory(0, PageSize),
                (long)pageId * PageSize, ct).ConfigureAwait(false);
            if (_pageCacheCapacity > 0) UpdateCacheIfPresent(pageId, buf.AsSpan(0, PageSize));
            ArrayPool<byte>.Shared.Return(buf);
        }
        _writeBatch.Clear();
    }

    /// <summary>
    /// Discards all buffered writes without touching the file and returns batch
    /// buffers to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public void AbortWriteBatch()
    {
        _writeBatchActive = false;
        foreach (var buf in _writeBatch.Values)
            ArrayPool<byte>.Shared.Return(buf);
        _writeBatch.Clear();
    }

    // --- Cache diagnostics ----------------------------------------------------

    /// <summary>Number of cached pages in the LRU cache.</summary>
    internal int CachedPageCount { get { lock (_cacheSync) return _cacheIndex?.Count ?? 0; } }

    /// <summary>Removes all entries from the page cache.</summary>
    internal void ClearCache()
    {
        lock (_cacheSync)
        {
            _cacheIndex?.Clear();
            _cacheLru?.Clear();
        }
    }

    // --- Private helpers ------------------------------------------------------

    /// <summary>
    /// Sync-only read used during construction / initialization only (no cache interaction).
    /// </summary>
    private OdsPage ReadPageSync(int pageId)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        var fileOffset = (long)pageId * PageSize;
        var totalRead = 0;
        while (totalRead < PageSize)
        {
            var chunk = RandomAccess.Read(_stream.SafeFileHandle,
                            buffer.AsSpan(totalRead, PageSize - totalRead),
                            fileOffset + totalRead);
            if (chunk == 0) break;
            totalRead += chunk;
        }
        VerifyPageChecksum(buffer, pageId);
        return CreatePage(pageId, buffer, pooled: true);
    }

    /// <summary>
    /// Sync-only write used during initialization only (no cache).
    /// </summary>
    private void WritePageSync(OdsPage page)
    {
        StampChecksum(page.Buffer, PageSize);
        RandomAccess.Write(_stream.SafeFileHandle,
            page.Buffer.AsSpan(0, PageSize),
            (long)page.PageId * PageSize);
    }

    private void Initialize()
    {
        var metaBuf = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(metaBuf, 0, PageSize);
        using var metaPage = CreatePage(MetadataPageId, metaBuf, pooled: true);
        metaPage.Header = new OdsPageHeader(MetadataPageId, OdsPageType.Meta, -1, -1, 0);
        WritePageSync(metaPage);

        var rootBuf = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(rootBuf, 0, PageSize);
        using var rootPage = CreatePage(1, rootBuf, pooled: true);
        rootPage.Header = new OdsPageHeader(1, OdsPageType.Leaf, -1, -1, 0);
        WritePageSync(rootPage);

        // Write initial root-metadata into the meta page.
        var initialMetadata = new OdsRootMetadata(rootPage.PageId, rootPage.PageId);
        var metaWriteBuf = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(metaWriteBuf, 0, PageSize);
        using var metaWritePage = CreatePage(MetadataPageId, metaWriteBuf, pooled: true);
        metaWritePage.Header = new OdsPageHeader(MetadataPageId, OdsPageType.Meta, -1, -1, 0);
        initialMetadata.Write(metaWritePage.Body);
        WritePageSync(metaWritePage);
        _rootMetadata = initialMetadata;
    }

    private void ValidatePageId(int pageId)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        var maxPageId = (_stream.Length / PageSize) - 1;
        if (pageId > maxPageId)
            throw new InvalidOperationException($"Page '{pageId}' is out of range. Max page id is '{maxPageId}'.");
    }

    /// <summary>
    /// Factory that creates an <see cref="OdsPage"/> with the correct checksum reservation
    /// for this pager's mode (V2 = 4 bytes, legacy V1 = 0 bytes).
    /// </summary>
    private OdsPage CreatePage(int pageId, byte[] buffer, bool pooled)
        => new OdsPage(pageId, buffer, PageSize, pooled, _checksumReserveBytes);

    // --- Checksum helpers -----------------------------------------------------

    /// <summary>
    /// Computes the FNV-1a 32-bit checksum of <paramref name="data"/>.
    /// </summary>
    private static uint ComputeFnv1aChecksum(ReadOnlySpan<byte> data)
    {
        const uint FnvOffset = 2166136261u;
        const uint FnvPrime  = 16777619u;
        var hash = FnvOffset;
        foreach (var b in data)
            hash = (hash ^ b) * FnvPrime;
        return hash;
    }

    /// <summary>
    /// Stamps the FNV-1a checksum of <c>buffer[0..pageSize-4]</c> into
    /// <c>buffer[pageSize-4..pageSize]</c> (little-endian uint32).
    /// Called on every write path before the buffer reaches disk.
    /// </summary>
    private static void StampChecksum(byte[] buffer, int pageSize)
    {
        var checksum = ComputeFnv1aChecksum(buffer.AsSpan(0, pageSize - OdsPage.ChecksumSizeInBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(pageSize - OdsPage.ChecksumSizeInBytes),
            checksum);
    }

    /// <summary>
    /// Verifies the FNV-1a checksum of a page buffer that has just been read from disk.
    /// Throws <see cref="InvalidDataException"/> on mismatch.
    /// In legacy V1 mode the verification is skipped (V1 pages carry no checksum).
    /// </summary>
    private void VerifyPageChecksum(byte[] buffer, int pageId)
    {
        if (_legacyV1Mode)
            return; // V1 pages have no checksum � skip verification.

        var expectedSlot = buffer.AsSpan(PageSize - OdsPage.ChecksumSizeInBytes);
        var stored   = BinaryPrimitives.ReadUInt32LittleEndian(expectedSlot);
        var computed = ComputeFnv1aChecksum(buffer.AsSpan(0, PageSize - OdsPage.ChecksumSizeInBytes));

        if (stored != computed)
            throw new InvalidDataException(
                $"ODS page {pageId} checksum mismatch. " +
                $"Stored: 0x{stored:X8}, computed: 0x{computed:X8}. " +
                "The page is corrupt or was written by an incompatible engine version.");
    }

    // --- LRU page-cache lock -------------------------------------------------
    // The page cache is accessed by concurrent readers (multiple TryGet calls
    // running simultaneously under the AsyncReaderWriterLock read lock).  The
    // internal Dictionary and LinkedList are NOT thread-safe, so all cache
    // helpers are gated on this dedicated lock.  Disk I/O (RandomAccess) is
    // kept outside the lock to avoid holding it during blocking reads.
    private readonly object _cacheSync = new();

    // --- LRU cache helpers ----------------------------------------------------

    private bool TryGetFromCache(int pageId, out byte[] buffer)
    {
        byte[]? src;
        lock (_cacheSync)
        {
            if (!_cacheIndex!.TryGetValue(pageId, out var entry))
            {
                buffer = null!;
                return false;
            }

            // No LRU promotion on cache hit.
            //
            // Rationale: LinkedList.Remove + AddFirst + Dict-update under _cacheSync was the
            // dominant serialisation bottleneck at high read concurrency.  With N concurrent
            // readers each scanning M pages, there are N×M lock acquisitions per second all
            // waiting for each other's O(1)-but-still-blocking LinkedList writes.
            //
            // Removing the promotion converts the lock section to a single read-only Dict
            // lookup (~10 ns), reducing contention by ~10–50×.  Eviction accuracy degrades
            // from LRU to approximately FIFO for hot pages, which is acceptable:
            //  • When the working set fits in cache (typical for small–medium databases),
            //    eviction never fires and the order is irrelevant.
            //  • When eviction does fire, FIFO behaves identically to LRU for sequential
            //    scan patterns (all pages accessed equally), and only slightly worse for
            //    skewed access patterns — a reasonable trade-off for a 10× latency win.
            //
            // Future improvement: transition to ConcurrentDictionary + clock-hand eviction
            // for fully lock-free reads.
            src = entry.Buffer;
        }

        // Copy outside the lock — each reader gets its own writable page buffer.
        var copy = new byte[PageSize];
        src.AsSpan(0, PageSize).CopyTo(copy);
        buffer = copy;
        return true;
    }

    private void AddOrUpdateCache(int pageId, ReadOnlySpan<byte> data)
    {
        // Copy data to a heap buffer first (Span can't be captured across lock boundary).
        var copy = new byte[PageSize];
        data.CopyTo(copy);

        lock (_cacheSync)
        {
            if (_cacheIndex!.TryGetValue(pageId, out var existing))
            {
                // Update in-place and promote to MRU.
                copy.AsSpan().CopyTo(existing.Buffer);
                _cacheLru!.Remove(existing.Node);
                var promotedNode = _cacheLru.AddFirst(pageId);
                _cacheIndex[pageId] = (promotedNode, existing.Buffer);
                return;
            }

            if (_cacheIndex.Count >= _pageCacheCapacity)
            {
                // Evict the least recently used page.
                var lruPageId = _cacheLru!.Last!.Value;
                _cacheLru.RemoveLast();
                _cacheIndex.Remove(lruPageId);
            }

            var buf = new byte[PageSize];
            copy.AsSpan().CopyTo(buf);
            var node = _cacheLru!.AddFirst(pageId);
            _cacheIndex[pageId] = (node, buf);
        }
    }

    /// <summary>
    /// Updates an existing cache entry in-place without allocating a new buffer.
    /// If the page is not in the cache, this is a no-op � the page will enter the
    /// cache on its next read.
    /// </summary>
    private void UpdateCacheIfPresent(int pageId, ReadOnlySpan<byte> data)
    {
        // Copy to heap before entering lock (Span has stack affinity).
        var copy = new byte[PageSize];
        data.CopyTo(copy);

        lock (_cacheSync)
        {
            if (!_cacheIndex!.TryGetValue(pageId, out var existing))
                return;

            copy.AsSpan().CopyTo(existing.Buffer);
            _cacheLru!.Remove(existing.Node);
            var promotedNode = _cacheLru.AddFirst(pageId);
            _cacheIndex[pageId] = (promotedNode, existing.Buffer);
        }
    }
}

