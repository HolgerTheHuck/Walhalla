using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WalhallaSql.Storage;

internal sealed class OdsPager : IDisposable
{
    public const int MetadataPageId = 0;

    private readonly FileStream _stream;
    private OdsRootMetadata _rootMetadata;

    private readonly int _pageCacheCapacity;
    private readonly Dictionary<int, (LinkedListNode<int> Node, byte[] Buffer)>? _cacheIndex;
    private readonly LinkedList<int>? _cacheLru;

    private readonly Dictionary<int, byte[]> _writeBatch = new(8);
    // Refcount so nested BeginWriteBatch/CommitWriteBatch calls coalesce into
    // a single flush at the outermost commit. Lets a BulkUpsert wrapper open
    // one batch around many per-entry UpsertCore calls without changing their
    // existing Begin/Commit pairs.
    private int _writeBatchDepth;
    private bool _writeBatchActive => _writeBatchDepth > 0;

    private readonly bool _legacyV1Mode;
    private readonly int _checksumReserveBytes;

    public OdsPager(string path, int pageSize, int pageCacheCapacity = 0, bool legacyV1Mode = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));
        if (pageSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be at least 1024 bytes.");
        if (pageCacheCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(pageCacheCapacity), "Page cache capacity must be >= 0.");

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

        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                 FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);

        if (_stream.Length == 0)
        {
            Initialize();
        }
        else if (_stream.Length < pageSize)
        {
            throw new InvalidDataException(
                $"ODS file '{path}' exists but is only {_stream.Length} bytes, " +
                $"which is smaller than the configured page size ({pageSize}). " +
                "The file is corrupt or was written by an incompatible engine version.");
        }
        else
        {
            var persistedPageSize = TryProbePersistedPageSize(pageSize);
            if (persistedPageSize.HasValue && persistedPageSize.Value != pageSize)
            {
                _stream.Dispose();
                PageSize = persistedPageSize.Value;
                _checksumReserveBytes = legacyV1Mode ? 0 : OdsPage.ChecksumSizeInBytes;
                _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                         FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            }

            using var metaPage = ReadPageSync(MetadataPageId);
            _rootMetadata = OdsRootMetadata.Read(metaPage.Body);
        }
    }

    public int PageSize { get; }

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

    public OdsPage ReadPage(int pageId)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        if (_writeBatchActive && _writeBatch.TryGetValue(pageId, out var batchBuf))
        {
            var rented = ArrayPool<byte>.Shared.Rent(PageSize);
            batchBuf.AsSpan(0, PageSize).CopyTo(rented);
            return CreatePage(pageId, rented, pooled: true);
        }

        // Cache lookup before ValidatePageId — cached pages were already validated on
        // their initial read, and ValidatePageId issues a _stream.Length syscall (was
        // 6% CPU in InsertBatch profile).
        if (_pageCacheCapacity > 0 && TryGetFromCache(pageId, out var cached))
            return CreatePage(pageId, cached, pooled: false);

        ValidatePageId(pageId);

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
    /// Reads page data into an existing pooled page, reusing its buffer.
    /// Returns false if the page cannot be reused (e.g. write-batch data would require a copy).
    /// </summary>
    public bool TryReadPageInto(int pageId, OdsPage page)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        if (_writeBatchActive && _writeBatch.TryGetValue(pageId, out var batchBuf))
        {
            batchBuf.AsSpan(0, PageSize).CopyTo(page.Buffer);
            page.Reuse(pageId);
            return true;
        }

        // Zero-alloc cache hit: copy cached page into our reusable buffer.
        // ValidatePageId deferred until cache miss (see ReadPage rationale).
        if (_pageCacheCapacity > 0 && TryCopyFromCache(pageId, page.Buffer.AsSpan(0, PageSize)))
        {
            page.Reuse(pageId);
            return true;
        }

        ValidatePageId(pageId);

        // Cache miss: read from disk, then populate cache for future hits.
        var fileOffset = (long)pageId * PageSize;
        var totalRead = 0;
        while (totalRead < PageSize)
        {
            var chunk = RandomAccess.Read(_stream.SafeFileHandle,
                            page.Buffer.AsSpan(totalRead, PageSize - totalRead),
                            fileOffset + totalRead);
            if (chunk == 0) break;
            totalRead += chunk;
        }

        VerifyPageChecksum(page.Buffer, pageId);

        if (_pageCacheCapacity > 0)
            AddOrUpdateCache(pageId, page.Buffer.AsSpan(0, PageSize));

        page.Reuse(pageId);
        return true;
    }

    public async ValueTask<OdsPage> ReadPageAsync(int pageId, CancellationToken ct = default)
    {
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));

        if (_writeBatchActive && _writeBatch.TryGetValue(pageId, out var batchBuf))
        {
            var rented = ArrayPool<byte>.Shared.Rent(PageSize);
            batchBuf.AsSpan(0, PageSize).CopyTo(rented);
            return CreatePage(pageId, rented, pooled: true);
        }

        if (_pageCacheCapacity > 0 && TryGetFromCache(pageId, out var cached))
            return CreatePage(pageId, cached, pooled: false);

        ValidatePageId(pageId);

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

    public async ValueTask WritePageAsync(OdsPage page, CancellationToken ct = default)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (page.Buffer.Length < PageSize)
            throw new InvalidOperationException("Page buffer is smaller than the pager page size.");

        StampChecksum(page.Buffer, PageSize);

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

    public OdsPage AllocatePage(OdsPageType pageType, int parentPageId = -1)
    {
        var metadata = ReadRootMetadata();
        var nextPageId = metadata.LastAllocatedPageId + 1;

        var buffer = ArrayPool<byte>.Shared.Rent(PageSize);
        Array.Clear(buffer, 0, PageSize);
        var page = CreatePage(nextPageId, buffer, pooled: true);
        page.Header = new OdsPageHeader(nextPageId, pageType, parentPageId, -1, 0);

        WritePage(page);
        WriteRootMetadata(metadata.WithLastAllocatedPageId(nextPageId));
        return page;
    }

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

    public void BeginWriteBatch() => _writeBatchDepth++;

    public void CommitWriteBatch()
    {
        if (_writeBatchDepth == 0) return;
        if (--_writeBatchDepth > 0) return; // nested commit: defer flush until outermost
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

    public async ValueTask CommitWriteBatchAsync(CancellationToken ct = default)
    {
        if (_writeBatchDepth == 0) return;
        if (--_writeBatchDepth > 0) return; // nested commit: defer flush until outermost
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

    public void AbortWriteBatch()
    {
        // Abort always tears down the entire batch stack — caller's responsibility
        // to not mix abort with outer surviving batches.
        _writeBatchDepth = 0;
        foreach (var buf in _writeBatch.Values)
            ArrayPool<byte>.Shared.Return(buf);
        _writeBatch.Clear();
    }

    internal int CachedPageCount { get { lock (_cacheSync) return _cacheIndex?.Count ?? 0; } }

    internal void ClearCache()
    {
        lock (_cacheSync)
        {
            _cacheIndex?.Clear();
            _cacheLru?.Clear();
        }
    }

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

        var initialMetadata = new OdsRootMetadata(rootPage.PageId, rootPage.PageId, OdsRootMetadata.ExpectedMagic, PageSize);
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

    private OdsPage CreatePage(int pageId, byte[] buffer, bool pooled)
        => new OdsPage(pageId, buffer, PageSize, pooled, _checksumReserveBytes);

    private static uint ComputeFnv1aChecksum(ReadOnlySpan<byte> data)
    {
        const uint FnvOffset = 2166136261u;
        const uint FnvPrime = 16777619u;
        var hash = FnvOffset;
        foreach (var b in data)
            hash = (hash ^ b) * FnvPrime;
        return hash;
    }

    private static void StampChecksum(byte[] buffer, int pageSize)
    {
        var checksum = ComputeFnv1aChecksum(buffer.AsSpan(0, pageSize - OdsPage.ChecksumSizeInBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(pageSize - OdsPage.ChecksumSizeInBytes), checksum);
    }

    private void VerifyPageChecksum(byte[] buffer, int pageId)
    {
        if (_legacyV1Mode) return;

        var expectedSlot = buffer.AsSpan(PageSize - OdsPage.ChecksumSizeInBytes);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(expectedSlot);
        var computed = ComputeFnv1aChecksum(buffer.AsSpan(0, PageSize - OdsPage.ChecksumSizeInBytes));

        if (stored != computed)
            throw new InvalidDataException(
                $"ODS page {pageId} checksum mismatch. " +
                $"Stored: 0x{stored:X8}, computed: 0x{computed:X8}. " +
                "The page is corrupt or was written by an incompatible engine version.");
    }

    private int? TryProbePersistedPageSize(int assumedPageSize)
    {
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(assumedPageSize);
            try
            {
                var totalRead = 0;
                var fileOffset = 0L;
                while (totalRead < assumedPageSize)
                {
                    var chunk = RandomAccess.Read(_stream.SafeFileHandle,
                        buffer.AsSpan(totalRead, assumedPageSize - totalRead), fileOffset + totalRead);
                    if (chunk == 0) break;
                    totalRead += chunk;
                }

                var header = OdsPageHeader.Read(buffer);
                var bodyOffset = OdsPageHeader.SizeInBytes;
                var bodyLength = assumedPageSize - OdsPageHeader.SizeInBytes - OdsPage.ChecksumSizeInBytes;
                var metadata = OdsRootMetadata.Read(buffer.AsSpan(bodyOffset, bodyLength));

                if (metadata.Magic == OdsRootMetadata.ExpectedMagic)
                    return metadata.PageSize;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            // Any probing failure means we treat it as legacy/old file
        }
        return null;
    }

    private readonly object _cacheSync = new();

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
            src = entry.Buffer;
        }

        var copy = new byte[PageSize];
        src.AsSpan(0, PageSize).CopyTo(copy);
        buffer = copy;
        return true;
    }

    /// <summary>Copies cached page data into <paramref name="dest"/> without allocating.</summary>
    private bool TryCopyFromCache(int pageId, Span<byte> dest)
    {
        lock (_cacheSync)
        {
            if (!_cacheIndex!.TryGetValue(pageId, out var entry))
                return false;
            entry.Buffer.AsSpan(0, PageSize).CopyTo(dest);
            return true;
        }
    }

    private void AddOrUpdateCache(int pageId, ReadOnlySpan<byte> data)
    {
        var copy = new byte[PageSize];
        data.CopyTo(copy);

        lock (_cacheSync)
        {
            if (_cacheIndex!.TryGetValue(pageId, out var existing))
            {
                copy.AsSpan().CopyTo(existing.Buffer);
                _cacheLru!.Remove(existing.Node);
                var promotedNode = _cacheLru.AddFirst(pageId);
                _cacheIndex[pageId] = (promotedNode, existing.Buffer);
                return;
            }

            if (_cacheIndex.Count >= _pageCacheCapacity)
            {
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

    private void UpdateCacheIfPresent(int pageId, ReadOnlySpan<byte> data)
    {
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
