using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using WalhallaSql.Core;

namespace WalhallaSql.Storage;

internal sealed class BPlusTree : IDisposable
{
    private readonly OdsPager _pager;
    private long _borrowLeafCount;
    private long _borrowInternalCount;
    private long _mergeLeafCount;
    private long _mergeInternalCount;

    public BPlusTree(OdsPager pager)
    {
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
    }

    public int RootPageId => _pager.ReadRootMetadata().RootPageId;

    internal long BorrowLeafCount => _borrowLeafCount;
    internal long BorrowInternalCount => _borrowInternalCount;
    internal long MergeLeafCount => _mergeLeafCount;
    internal long MergeInternalCount => _mergeInternalCount;

    public bool TryGet(ReadOnlySpan<byte> key, out byte[]? value)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        using var leafPage = ReadLeafForKey(key);
        var entries = ReadLeafEntries(leafPage);

        foreach (var entry in entries)
        {
            if (CompareKeys(entry.Key, key) != 0)
                continue;

            value = (byte[])entry.Value.Clone();
            return true;
        }

        value = null;
        return false;
    }

    public void Upsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        UpsertCore(key.ToArray(), value.ToArray());
    }

    public void Upsert(byte[] key, byte[] value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (key.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));

        UpsertCore(key, value);
    }

    private void UpsertCore(byte[] keyBytes, byte[] valueBytes)
    {
        _pager.BeginWriteBatch();
        try
        {
            var path = new Stack<UpsertPathFrame>();

            var rootId = _pager.ReadRootMetadata().RootPageId;
            var currentPage = _pager.ReadPage(rootId);

            while (currentPage.Header.PageType == OdsPageType.Internal)
            {
                var childIndex = FindChildIndexNonAlloc(currentPage, keyBytes);
                path.Push(new UpsertPathFrame(currentPage.PageId, childIndex));
                var childPageId = ReadChildPageId(currentPage, childIndex);
                currentPage.Dispose();
                currentPage = _pager.ReadPage(childPageId);
            }

            if (currentPage.Header.PageType != OdsPageType.Leaf)
            {
                currentPage.Dispose();
                throw new NotSupportedException("Unsupported page type encountered during insert traversal.");
            }

            if (TryUpsertLeafInPlace(currentPage, keyBytes, valueBytes))
            {
                _pager.WritePage(currentPage);
                currentPage.Dispose();
                _pager.CommitWriteBatch();
                return;
            }

            var leafEntries = ReadLeafEntries(currentPage);
            InsertOrReplace(leafEntries, keyBytes, valueBytes);
            var rightPages = SplitLeaf(currentPage, leafEntries);
            currentPage.Dispose();

            // Insert all new right pages into parent (may be 1 normally, or more on overflow).
            var newPageList = new Queue<(byte[] Separator, int PageId)>(rightPages);
            while (newPageList.Count > 0)
            {
                var (promotedKey, newRightPageId) = newPageList.Dequeue();

                while (path.Count > 0)
                {
                    var frame = path.Pop();
                    using var parentPage = _pager.ReadPage(frame.PageId);
                    var parentEntries = ReadInternalEntries(parentPage);
                    parentEntries.Separators.Insert(frame.ChildIndex, promotedKey);
                    parentEntries.ChildPageIds.Insert(frame.ChildIndex + 1, newRightPageId);

                    if (TryWriteInternalEntries(parentPage, parentEntries))
                    {
                        _pager.WritePage(parentPage);
                        _pager.CommitWriteBatch();
                        goto nextPage;
                    }

                    SplitInternal(parentPage, parentEntries, out promotedKey, out newRightPageId);
                }

                PromoteNewRoot(rootId, promotedKey, newRightPageId);
                rootId = _pager.ReadRootMetadata().RootPageId; // refresh after promote
                nextPage: ;
            }
            _pager.CommitWriteBatch();
        }
        catch
        {
            _pager.AbortWriteBatch();
            throw;
        }
    }

    /// <summary>
    /// Bulk insert/update wrapped in a single outer pager write batch.
    /// The reentrant pager collapses per-entry Begin/Commit pairs in
    /// <see cref="UpsertCore"/> into no-ops at depth&gt;0, so we get
    /// O(distinctDirtyPages) RandomAccess.Write syscalls instead of
    /// O(entries * splits). Closes the InsertBatch gap measured against SQLite.
    /// </summary>
    public void BulkUpsert(IReadOnlyList<(byte[] Key, byte[] Value)> entries)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        if (entries.Count == 0) return;

        _pager.BeginWriteBatch();
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var (k, v) = entries[i];
                if (k == null) throw new ArgumentNullException(nameof(entries), "Key must not be null.");
                if (v == null) throw new ArgumentNullException(nameof(entries), "Value must not be null.");
                if (k.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(entries));
                UpsertCore(k, v);
            }
            _pager.CommitWriteBatch();
        }
        catch
        {
            _pager.AbortWriteBatch();
            throw;
        }
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        var traversalPages = new List<OdsPage>();
        _pager.BeginWriteBatch();
        try
        {
            var path = new Stack<PathFrame>();
            var currentPage = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);
            traversalPages.Add(currentPage);

            while (currentPage.Header.PageType == OdsPageType.Internal)
            {
                var internalEntries = ReadInternalEntries(currentPage);
                var childIndex = FindChildIndex(internalEntries.Separators, key);
                path.Push(new PathFrame(currentPage, internalEntries, childIndex));
                currentPage = _pager.ReadPage(internalEntries.ChildPageIds[childIndex]);
                traversalPages.Add(currentPage);
            }

            if (currentPage.Header.PageType != OdsPageType.Leaf)
                throw new NotSupportedException("Unsupported page type encountered during delete traversal.");

            var leafEntries = ReadLeafEntries(currentPage);
            var removedAt = -1;
            for (var i = 0; i < leafEntries.Count; i++)
            {
                if (CompareKeys(leafEntries[i].Key, key) != 0) continue;
                removedAt = i;
                break;
            }

            if (removedAt < 0)
            {
                _pager.CommitWriteBatch();
                return false;
            }

            leafEntries.RemoveAt(removedAt);
            WriteLeafEntries(currentPage, leafEntries);
            _pager.WritePage(currentPage);

            var nodePageId = currentPage.PageId;

            while (path.Count > 0)
            {
                var parentFrame = path.Pop();
                var parentEntries = ReadInternalEntries(parentFrame.Page);
                var childIndex = parentEntries.ChildPageIds.IndexOf(nodePageId);
                if (childIndex < 0)
                    throw new InvalidOperationException("Delete rebalance lost parent-child linkage.");

                using var nodePage = _pager.ReadPage(nodePageId);

                if (!HasUnderflow(nodePage, isRoot: false))
                {
                    var separatorChanged = UpdateSeparatorForChild(parentEntries, childIndex);
                    if (separatorChanged)
                    {
                        if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                            throw new InvalidOperationException("Unable to persist parent separator update.");
                        _pager.WritePage(parentFrame.Page);
                    }

                    NormalizeRoot();
                    _pager.CommitWriteBatch();
                    return true;
                }

                if (TryBorrowFromLeft(parentEntries, childIndex, nodePage) ||
                    TryBorrowFromRight(parentEntries, childIndex, nodePage))
                {
                    if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                        throw new InvalidOperationException("Unable to persist parent after borrow rebalance.");
                    _pager.WritePage(parentFrame.Page);
                    NormalizeRoot();
                    _pager.CommitWriteBatch();
                    return true;
                }

                if (childIndex > 0)
                {
                    using var leftPage = _pager.ReadPage(parentEntries.ChildPageIds[childIndex - 1]);
                    MergeChildrenIntoLeft(parentEntries, childIndex - 1, leftPage, nodePage);
                    if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                        throw new InvalidOperationException("Unable to persist parent after left-merge.");
                    _pager.WritePage(parentFrame.Page);
                    nodePageId = parentFrame.Page.PageId;
                    continue;
                }

                if (parentEntries.ChildPageIds.Count <= 1)
                    throw new InvalidOperationException("Invalid internal node state during delete rebalance.");

                using var rightPage = _pager.ReadPage(parentEntries.ChildPageIds[1]);
                MergeRightIntoLeft(parentEntries, 0, nodePage, rightPage);
                if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                    throw new InvalidOperationException("Unable to persist parent after right-merge.");
                _pager.WritePage(parentFrame.Page);
                nodePageId = parentFrame.Page.PageId;
            }

            NormalizeRoot();
            _pager.CommitWriteBatch();
            return true;
        }
        catch
        {
            _pager.AbortWriteBatch();
            throw;
        }
        finally
        {
            foreach (var p in traversalPages)
                p.Dispose();
        }
    }

    public void Flush() => _pager.Flush();

    public void Dispose() => _pager.Dispose();

    public IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateEntries()
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        while (stack.Count > 0)
        {
            using var page = _pager.ReadPage(stack.Pop());
            if (page.Header.PageType == OdsPageType.Leaf)
            {
                var leafEntries = ReadLeafEntries(page);
                foreach (var entry in leafEntries)
                    yield return new KeyValuePair<byte[], byte[]>(entry.Key, entry.Value);
                continue;
            }

            if (page.Header.PageType != OdsPageType.Internal)
                throw new NotSupportedException("Unsupported page type encountered during enumeration traversal.");

            var internalEntries = ReadInternalEntries(page);
            for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
                stack.Push(internalEntries.ChildPageIds[i]);
        }
    }

    public IReadOnlyList<KeyValuePair<byte[], byte[]>> SnapshotEntries()
    {
        var result = new List<KeyValuePair<byte[], byte[]>>();
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        while (stack.Count > 0)
        {
            using var page = _pager.ReadPage(stack.Pop());
            if (page.Header.PageType == OdsPageType.Leaf)
            {
                var leafEntries = ReadLeafEntries(page);
                foreach (var entry in leafEntries)
                    result.Add(new KeyValuePair<byte[], byte[]>(entry.Key, entry.Value));
                continue;
            }

            if (page.Header.PageType != OdsPageType.Internal)
                throw new NotSupportedException("Unsupported page type encountered during snapshot traversal.");

            var internalEntries = ReadInternalEntries(page);
            for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
                stack.Push(internalEntries.ChildPageIds[i]);
        }

        return result;
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateRange(
        byte[]? fromInclusive = null,
        byte[]? toExclusive = null)
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        while (stack.Count > 0)
        {
            using var page = _pager.ReadPage(stack.Pop());

            if (page.Header.PageType == OdsPageType.Leaf)
            {
                var leafEntries = ReadLeafEntries(page);
                foreach (var entry in leafEntries)
                {
                    if (fromInclusive != null && CompareKeys(entry.Key, fromInclusive) < 0)
                        continue;
                    if (toExclusive != null && CompareKeys(entry.Key, toExclusive) >= 0)
                        break;
                    yield return new KeyValuePair<byte[], byte[]>(entry.Key, entry.Value);
                }
                continue;
            }

            if (page.Header.PageType != OdsPageType.Internal)
                throw new NotSupportedException("Unsupported page type during range traversal.");

            var internalEntries = ReadInternalEntries(page);
            for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
            {
                if (fromInclusive != null && i < internalEntries.Separators.Count &&
                    CompareKeys(fromInclusive, internalEntries.Separators[i]) >= 0)
                    continue;

                if (toExclusive != null && i > 0 &&
                    CompareKeys(toExclusive, internalEntries.Separators[i - 1]) <= 0)
                    continue;

                stack.Push(internalEntries.ChildPageIds[i]);
            }
        }
    }

    /// <summary>
    /// Zero-allocation value scan. Calls <paramref name="action"/> with
    /// (pageBuffer, valueOffset, valueLength) for each value in range.
    /// Return false to stop scanning. The page buffer is valid only during
    /// the callback — no byte[] copies.
    /// </summary>
    public void EnumerateValuesSpan(
        byte[]? fromInclusive,
        byte[]? toExclusive,
        Func<byte[], int, int, bool> action)
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        // Reuse a single pooled page buffer for all reads — avoids per-page ArrayPool allocations.
        // Create pooled page directly to avoid the OdsPager page cache returning non-pooled pages.
        var pooledBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(_pager.PageSize);
        var reusablePage = new OdsPage(-1, pooledBuf, _pager.PageSize, pooled: true);
        try
        {
            while (stack.Count > 0)
            {
                _pager.TryReadPageInto(stack.Pop(), reusablePage);

                var page = reusablePage;

                if (page.Header.PageType == OdsPageType.Leaf)
                {
                    var body = page.Body;
                    int offset = 0;
                    int itemCount = page.Header.ItemCount;

                    for (int i = 0; i < itemCount; i++)
                    {
                        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset));
                        offset += sizeof(int);
                        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset));
                        offset += sizeof(int);

                        if (keyLength == 0) // skip continuation entries
                            continue;

                        int keyStart = offset;
                        offset += keyLength;
                        int valueStart = offset;
                        offset += valueLength;

                        if (fromInclusive != null &&
                            body.Slice(keyStart, keyLength).SequenceCompareTo(fromInclusive) < 0)
                            continue;
                        if (toExclusive != null &&
                            body.Slice(keyStart, keyLength).SequenceCompareTo(toExclusive) >= 0)
                            break;

                        // Check for overflow continuation (last entry may continue in sibling pages).
                        if (i == itemCount - 1 && page.Header.RightSiblingPageId >= 0)
                        {
                            byte[]? fullValue = TryReadOverflowValue(
                                page.Buffer, OdsPageHeader.SizeInBytes + valueStart, valueLength, page.Header.RightSiblingPageId);
                            if (fullValue != null)
                            {
                                if (!action(fullValue, 0, fullValue.Length))
                                    return;
                                continue;
                            }
                        }

                        if (!action(page.Buffer, OdsPageHeader.SizeInBytes + valueStart, valueLength))
                            return;
                    }
                    continue;
                }

                if (page.Header.PageType != OdsPageType.Internal)
                    throw new NotSupportedException("Unsupported page type during range traversal.");

                var internalEntries = ReadInternalEntries(page);
                for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
                {
                    if (fromInclusive != null && i < internalEntries.Separators.Count &&
                        CompareKeys(fromInclusive, internalEntries.Separators[i]) >= 0)
                        continue;

                    if (toExclusive != null && i > 0 &&
                        CompareKeys(toExclusive, internalEntries.Separators[i - 1]) <= 0)
                        continue;

                    stack.Push(internalEntries.ChildPageIds[i]);
                }
            }
        }
        finally
        {
            reusablePage?.Dispose();
        }
    }

    /// <summary>
    /// Zero-allocation entry scan. Calls <paramref name="action"/> with
    /// (pageBuffer, keyOffset, keyLength, valueOffset, valueLength) for each entry in range.
    /// Return false to stop scanning. The page buffer is valid only during
    /// the callback — no byte[] copies.
    /// </summary>
    public void EnumerateEntriesSpan(
        byte[]? fromInclusive,
        byte[]? toExclusive,
        Func<byte[], int, int, int, int, bool> action)
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        var pooledBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(_pager.PageSize);
        var reusablePage = new OdsPage(-1, pooledBuf, _pager.PageSize, pooled: true);
        try
        {
            while (stack.Count > 0)
            {
                _pager.TryReadPageInto(stack.Pop(), reusablePage);

                var page = reusablePage;

                if (page.Header.PageType == OdsPageType.Leaf)
                {
                    var body = page.Body;
                    int offset = 0;
                    int itemCount = page.Header.ItemCount;

                    for (int i = 0; i < itemCount; i++)
                    {
                        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset));
                        offset += sizeof(int);
                        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset));
                        offset += sizeof(int);

                        if (keyLength == 0) // skip continuation entries
                            continue;

                        int keyStart = offset;
                        offset += keyLength;
                        int valueStart = offset;
                        offset += valueLength;

                        if (fromInclusive != null &&
                            body.Slice(keyStart, keyLength).SequenceCompareTo(fromInclusive) < 0)
                            continue;
                        if (toExclusive != null &&
                            body.Slice(keyStart, keyLength).SequenceCompareTo(toExclusive) >= 0)
                            break;

                        // Check for overflow continuation (last entry may continue in sibling pages).
                        if (i == itemCount - 1 && page.Header.RightSiblingPageId >= 0)
                        {
                            byte[]? fullValue = TryReadOverflowValue(
                                page.Buffer, OdsPageHeader.SizeInBytes + valueStart, valueLength, page.Header.RightSiblingPageId);
                            if (fullValue != null)
                            {
                                // Allocate combined [key][value] buffer so the callback sees both in one span.
                                var combined = new byte[keyLength + fullValue.Length];
                                page.Buffer.AsSpan(OdsPageHeader.SizeInBytes + keyStart, keyLength).CopyTo(combined);
                                fullValue.CopyTo(combined.AsSpan(keyLength));
                                if (!action(combined, 0, keyLength, keyLength, fullValue.Length))
                                    return;
                                continue;
                            }
                        }

                        if (!action(page.Buffer, OdsPageHeader.SizeInBytes + keyStart, keyLength, OdsPageHeader.SizeInBytes + valueStart, valueLength))
                            return;
                    }
                    continue;
                }

                if (page.Header.PageType != OdsPageType.Internal)
                    throw new NotSupportedException("Unsupported page type during range traversal.");

                var internalEntries = ReadInternalEntries(page);
                for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
                {
                    if (fromInclusive != null && i < internalEntries.Separators.Count &&
                        CompareKeys(fromInclusive, internalEntries.Separators[i]) >= 0)
                        continue;

                    if (toExclusive != null && i > 0 &&
                        CompareKeys(toExclusive, internalEntries.Separators[i - 1]) <= 0)
                        continue;

                    stack.Push(internalEntries.ChildPageIds[i]);
                }
            }
        }
        finally
        {
            reusablePage?.Dispose();
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private OdsPage ReadLeafForKey(ReadOnlySpan<byte> key)
    {
        var page = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);

        while (page.Header.PageType == OdsPageType.Internal)
        {
            var childIndex = FindChildIndexNonAlloc(page, key);
            var childPageId = ReadChildPageId(page, childIndex);
            page.Dispose();
            page = _pager.ReadPage(childPageId);
        }

        if (page.Header.PageType != OdsPageType.Leaf)
        {
            page.Dispose();
            throw new NotSupportedException("Unsupported page type encountered during read traversal.");
        }

        return page;
    }

    /// <summary>
    /// Split a full leaf page into the existing page (left) and one or more new right pages.
    /// Returns a list of (separatorKey, pageId) pairs for all new right-side pages that
    /// must be inserted into the parent.
    /// </summary>
    private List<(byte[] Separator, int PageId)> SplitLeaf(OdsPage page, List<LeafEntry> entries)
    {
        var bodySize = page.Body.Length;

        // Pre-calculate entry sizes.
        var entrySizes = new int[entries.Count];
        long totalBytes = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            entrySizes[i] = (sizeof(int) * 2) + entries[i].Key.Length + entries[i].Value.Length;
            totalBytes += entrySizes[i];
        }

        // Try balanced split first.
        var splitIndex = FindLeafSplitIndex(entrySizes, bodySize);

        // If midpoint doesn't actually work, fall back to greedy packing.
        if (splitIndex == entries.Count / 2)
        {
            long leftAtMid = 0;
            for (int i = 0; i < splitIndex; i++) leftAtMid += entrySizes[i];
            if (leftAtMid > bodySize || (totalBytes - leftAtMid) > bodySize)
            {
                splitIndex = GreedyPackFromStart(entrySizes, bodySize);
                if (splitIndex == 0) splitIndex = 1;
                if (splitIndex >= entries.Count) splitIndex = entries.Count - 1;
            }
        }

        var originalRightSiblingId = page.Header.RightSiblingPageId;
        var result = new List<(byte[], int)>();
        var rightPages = new List<OdsPage>();
        try
        {
            var firstRight = _pager.AllocatePage(OdsPageType.Leaf, -1);
            rightPages.Add(firstRight);

            // Track the last-written page's ID so the sibling chain can be threaded.
            int lastWrittenPageId = -1;

            // Write left page.
            if (!TryWriteLeafEntriesRange(page, entries, 0, splitIndex))
            {
                // Entries don't fit in the left page. If it's a single entry, overflow it.
                if (splitIndex != 1)
                    ThrowLeafSplitFailed(bodySize, entries.Count, totalBytes, entrySizes, splitIndex, "left");

                lastWrittenPageId = WriteLargeEntry(page, entries[0]);
                // The primary page is already persisted; chain the last overflow page to firstRight.
                UpdateRightSibling(lastWrittenPageId, firstRight.PageId);
            }
            else
            {
                page.Header = page.Header with { RightSiblingPageId = firstRight.PageId };
                _pager.WritePage(page);
                lastWrittenPageId = page.PageId;
            }

            // Pack remaining entries into right-side pages.
            var rightEntriesStart = splitIndex;
            var remaining = entries.Count - splitIndex;
            var pageIdx = 0;

            while (remaining > 0)
            {
                OdsPage currentRight;
                if (pageIdx < rightPages.Count)
                    currentRight = rightPages[pageIdx];
                else
                {
                    currentRight = _pager.AllocatePage(OdsPageType.Leaf, -1);
                    rightPages.Add(currentRight);
                }

                var packed = GreedyPackFromStart(entrySizes, bodySize, rightEntriesStart, remaining);

                if (packed == 0)
                {
                    var largeEntry = entries[rightEntriesStart];
                    int overflowLastId = WriteLargeEntry(currentRight, largeEntry);

                    UpdateRightSibling(lastWrittenPageId, currentRight.PageId);

                    result.Add((largeEntry.Key, currentRight.PageId));
                    rightEntriesStart++;
                    remaining--;
                    lastWrittenPageId = overflowLastId;
                    pageIdx++;
                    continue;
                }

                if (!TryWriteLeafEntriesRange(currentRight, entries, rightEntriesStart, packed))
                    ThrowLeafSplitFailed(bodySize, entries.Count, totalBytes, entrySizes, packed, "right");

                UpdateRightSibling(lastWrittenPageId, currentRight.PageId);

                _pager.WritePage(currentRight);
                result.Add((entries[rightEntriesStart].Key, currentRight.PageId));

                lastWrittenPageId = currentRight.PageId;
                rightEntriesStart += packed;
                remaining -= packed;
                pageIdx++;
            }

            // Set final page's RightSibling to the original sibling.
            UpdateRightSibling(lastWrittenPageId, originalRightSiblingId);

            return result;
        }
        finally
        {
            foreach (var rp in rightPages)
                rp.Dispose();
        }
    }

    /// <summary>
    /// Update <paramref name="pageId"/>'s RightSibling to <paramref name="siblingId"/>.
    /// No-op when pageId is -1.
    /// </summary>
    private void UpdateRightSibling(int pageId, int siblingId)
    {
        if (pageId < 0) return;
        using var p = _pager.ReadPage(pageId);
        p.Header = p.Header with { RightSiblingPageId = siblingId };
        _pager.WritePage(p);
    }

    /// <summary>
    /// Store an entry whose value exceeds the page body across an overflow page chain.
    /// The first page holds key + first value chunk (normal entry format).
    /// Continuation pages hold value chunks with keyLen=0 as the continuation marker,
    /// chained via RightSibling.
    /// Returns the page ID of the last page in the overflow chain.
    /// </summary>
    private int WriteLargeEntry(OdsPage page, LeafEntry entry)
    {
        var bodySize = page.Body.Length;
        int headerSize = sizeof(int) * 2; // keyLen + valueLen
        int maxFirstChunk = bodySize - headerSize - entry.Key.Length;

        if (maxFirstChunk <= 0)
            throw new InvalidOperationException(
                $"Key too large for page: key={entry.Key.Length} bytes, body={bodySize} bytes.");

        // Write first chunk into primary page.
        var body = page.Body;
        body.Clear();
        int offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entry.Key.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], maxFirstChunk);
        offset += sizeof(int);
        entry.Key.CopyTo(body[offset..]);
        offset += entry.Key.Length;
        entry.Value.AsSpan(0, maxFirstChunk).CopyTo(body[offset..]);
        page.Header = page.Header with { PageType = OdsPageType.Leaf, ItemCount = 1, RightSiblingPageId = -1 };

        int lastPageId = page.PageId;
        int valueOffset = maxFirstChunk;
        int remaining = entry.Value.Length - maxFirstChunk;

        while (remaining > 0)
        {
            using var contPage = _pager.AllocatePage(OdsPageType.Leaf, -1);
            int chunkSize = Math.Min(remaining, bodySize - headerSize);

            var contBody = contPage.Body;
            contBody.Clear();
            int contOff = 0;
            BinaryPrimitives.WriteInt32LittleEndian(contBody[contOff..], 0); // keyLen=0 = continuation
            contOff += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(contBody[contOff..], chunkSize);
            contOff += sizeof(int);
            entry.Value.AsSpan(valueOffset, chunkSize).CopyTo(contBody[contOff..]);
            contPage.Header = contPage.Header with { PageType = OdsPageType.Leaf, ItemCount = 1, RightSiblingPageId = -1 };

            // Link from previous page to this continuation.
            UpdateRightSibling(lastPageId, contPage.PageId);

            _pager.WritePage(contPage);
            lastPageId = contPage.PageId;

            valueOffset += chunkSize;
            remaining -= chunkSize;
        }

        return lastPageId;
    }

    private static void ThrowLeafSplitFailed(int bodySize, int entryCount, long totalBytes,
        int[] entrySizes, int chunk, string side)
    {
        int maxEntry = 0;
        for (int i = 0; i < entrySizes.Length; i++)
        { if (entrySizes[i] > maxEntry) maxEntry = entrySizes[i]; }
        throw new InvalidOperationException(
            $"Leaf split failed: {side} chunk ({chunk} entries) does not fit in page " +
            $"(body={bodySize} bytes, totalEntries={entryCount}, totalBytes={totalBytes}, maxEntry={maxEntry}). " +
            $"Increase the page size.");
    }

    /// <summary>
    /// Pack as many entries as fit starting from <paramref name="start"/>, up to <paramref name="count"/> entries.
    /// Returns the number of entries that fit in <paramref name="bodySize"/> bytes.
    /// </summary>
    private static int GreedyPackFromStart(int[] entrySizes, int bodySize, int start = 0, int count = -1)
    {
        if (count < 0) count = entrySizes.Length - start;
        var end = start + count;
        long used = 0;
        int packed = 0;
        for (int i = start; i < end; i++)
        {
            if (used + entrySizes[i] > bodySize)
                break;
            used += entrySizes[i];
            packed++;
        }
        return packed;
    }

    /// <summary>
    /// Find a split index where both halves fit within <paramref name="bodySize"/> bytes.
    /// Starts at the midpoint and scans outward. Returns n/2 if no balanced split exists.
    /// </summary>
    private static int FindLeafSplitIndex(int[] entrySizes, int bodySize)
    {
        int n = entrySizes.Length;
        long total = 0;
        for (int i = 0; i < n; i++)
            total += entrySizes[i];

        int mid = n / 2;
        for (int offset = 0; offset < n; offset++)
        {
            int candidate = mid + (offset % 2 == 0 ? offset / 2 : -(offset / 2 + 1));
            if (candidate <= 0 || candidate >= n)
                continue;

            long leftSize = 0;
            for (int i = 0; i < candidate; i++)
                leftSize += entrySizes[i];

            if (leftSize <= bodySize && (total - leftSize) <= bodySize)
                return candidate;
        }

        return n / 2;
    }

    private void SplitInternal(OdsPage page, InternalEntries entries, out byte[] promotedKey, out int newRightPageId)
    {
        var separatorMid = entries.Separators.Count / 2;
        promotedKey = entries.Separators[separatorMid];

        var leftSeparators = entries.Separators.GetRange(0, separatorMid);
        var rightSeparators = entries.Separators.GetRange(separatorMid + 1, entries.Separators.Count - separatorMid - 1);
        var leftChildren = entries.ChildPageIds.GetRange(0, separatorMid + 1);
        var rightChildren = entries.ChildPageIds.GetRange(separatorMid + 1, entries.ChildPageIds.Count - (separatorMid + 1));

        var left = new InternalEntries(leftChildren, leftSeparators);
        if (!TryWriteInternalEntries(page, left))
            throw new InvalidOperationException("Unable to write left internal split page.");
        _pager.WritePage(page);

        using var rightPage = _pager.AllocatePage(OdsPageType.Internal, -1);
        var right = new InternalEntries(rightChildren, rightSeparators);
        if (!TryWriteInternalEntries(rightPage, right))
            throw new InvalidOperationException("Unable to write right internal split page.");
        _pager.WritePage(rightPage);

        newRightPageId = rightPage.PageId;
    }

    private void PromoteNewRoot(int leftChildPageId, byte[] promotedKey, int rightChildPageId)
    {
        using var newRoot = _pager.AllocatePage(OdsPageType.Internal, -1);
        var internalEntries = new InternalEntries(
            new List<int> { leftChildPageId, rightChildPageId },
            new List<byte[]> { promotedKey });

        if (!TryWriteInternalEntries(newRoot, internalEntries))
            throw new InvalidOperationException("Unable to write promoted root page.");

        _pager.WritePage(newRoot);

        var metadata = _pager.ReadRootMetadata();
        _pager.WriteRootMetadata(metadata.WithRootPageId(newRoot.PageId));
    }

    private int FindChildIndex(IReadOnlyList<byte[]> separators, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < separators.Count; i++)
        {
            if (CompareKeys(key, separators[i]) < 0)
                return i;
        }
        return separators.Count;
    }

    private void InsertOrReplace(List<LeafEntry> entries, byte[] key, byte[] value)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var cmp = CompareKeys(entries[i].Key, key);
            if (cmp == 0)
            {
                entries[i] = new LeafEntry(key, value);
                return;
            }
            if (cmp > 0)
            {
                entries.Insert(i, new LeafEntry(key, value));
                return;
            }
        }
        entries.Add(new LeafEntry(key, value));
    }

    private static int CompareKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return ByteLexicographicalComparer.Compare(left, right);
    }

    private int FindChildIndexNonAlloc(OdsPage page, ReadOnlySpan<byte> key)
    {
        var body = page.Body;
        var offset = 0;
        if (body.Length < sizeof(int))
            throw new InvalidOperationException("Internal page payload is corrupted.");

        var childCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);
        if (childCount <= 0)
            throw new InvalidOperationException("Internal page has invalid child count.");

        var childrenBytes = childCount * sizeof(int);
        if (offset + childrenBytes + sizeof(int) > body.Length)
            throw new InvalidOperationException("Internal page children are truncated.");

        offset += childrenBytes;
        var separatorCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);
        if (separatorCount != childCount - 1)
            throw new InvalidOperationException("Internal page child/separator relationship is invalid.");

        for (var i = 0; i < separatorCount; i++)
        {
            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("Internal page separator length is truncated.");

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);
            if (keyLength <= 0 || offset + keyLength > body.Length)
                throw new InvalidOperationException("Internal page separator payload is corrupted.");

            var separator = body.Slice(offset, keyLength);
            if (CompareKeys(key, separator) < 0)
                return i;

            offset += keyLength;
        }

        return separatorCount;
    }

    private static int ReadChildPageId(OdsPage page, int childIndex)
    {
        var body = page.Body;
        var childCount = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (childIndex < 0 || childIndex >= childCount)
            throw new InvalidOperationException("Child index out of range.");

        var childOffset = sizeof(int) + (childIndex * sizeof(int));
        if (childOffset + sizeof(int) > body.Length)
            throw new InvalidOperationException("Internal page child pointer is truncated.");

        return BinaryPrimitives.ReadInt32LittleEndian(body[childOffset..]);
    }

    private bool TryUpsertLeafInPlace(OdsPage page, byte[] key, byte[] value)
    {
        var body = page.Body;
        var header = page.Header;
        var itemCount = header.ItemCount;

        var entryStarts = ArrayPool<int>.Shared.Rent(Math.Max(1, itemCount));
        var entrySizes = ArrayPool<int>.Shared.Rent(Math.Max(1, itemCount));
        try
        {
            var offset = 0;
            var insertAt = itemCount;
            var replaceAt = -1;
            var oldEntrySize = 0;
            var dataBytes = 0;

            for (var i = 0; i < itemCount; i++)
            {
                if (offset + (sizeof(int) * 2) > body.Length)
                    throw new InvalidOperationException("Leaf page payload is corrupted.");

                var start = offset;
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += sizeof(int);
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += sizeof(int);
                if (keyLength <= 0 || valueLength < 0 || offset + keyLength + valueLength > body.Length)
                    throw new InvalidOperationException("Leaf page payload is corrupted.");

                var existingKey = body.Slice(offset, keyLength);
                var cmp = CompareKeys(existingKey, key);
                if (replaceAt < 0 && cmp == 0)
                {
                    replaceAt = i;
                    oldEntrySize = (sizeof(int) * 2) + keyLength + valueLength;
                }
                else if (insertAt == itemCount && cmp > 0)
                {
                    insertAt = i;
                }

                offset += keyLength + valueLength;
                var entrySize = offset - start;
                entryStarts[i] = start;
                entrySizes[i] = entrySize;
                dataBytes += entrySize;
            }

            var newEntrySize = (sizeof(int) * 2) + key.Length + value.Length;
            var newItemCount = replaceAt >= 0 ? itemCount : itemCount + 1;
            var newDataSize = replaceAt >= 0 ? dataBytes - oldEntrySize + newEntrySize : dataBytes + newEntrySize;
            if (newDataSize > body.Length)
                return false;

            var scratch = ArrayPool<byte>.Shared.Rent(body.Length);
            try
            {
                var writeOffset = 0;
                var readIndex = 0;
                for (var targetIndex = 0; targetIndex < newItemCount; targetIndex++)
                {
                    if (replaceAt >= 0 && targetIndex == replaceAt)
                    {
                        WriteLeafEntry(scratch, ref writeOffset, key, value);
                        readIndex++;
                        continue;
                    }
                    if (replaceAt < 0 && targetIndex == insertAt)
                    {
                        WriteLeafEntry(scratch, ref writeOffset, key, value);
                        continue;
                    }

                    var sourceStart = entryStarts[readIndex];
                    var sourceSize = entrySizes[readIndex];
                    body.Slice(sourceStart, sourceSize).CopyTo(scratch.AsSpan(writeOffset));
                    writeOffset += sourceSize;
                    readIndex++;
                }

                body.Clear();
                scratch.AsSpan(0, writeOffset).CopyTo(body);
                page.Header = header with { PageType = OdsPageType.Leaf, ItemCount = newItemCount };
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(entryStarts);
            ArrayPool<int>.Shared.Return(entrySizes);
        }
    }

    private static void WriteLeafEntry(byte[] buffer, ref int offset, byte[] key, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), key.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), value.Length);
        offset += sizeof(int);
        key.CopyTo(buffer.AsSpan(offset));
        offset += key.Length;
        value.CopyTo(buffer.AsSpan(offset));
        offset += value.Length;
    }

    private bool TryBorrowFromLeft(InternalEntries parentEntries, int childIndex, OdsPage nodePage)
    {
        if (childIndex <= 0) return false;

        using var leftPage = _pager.ReadPage(parentEntries.ChildPageIds[childIndex - 1]);

        if (nodePage.Header.PageType == OdsPageType.Leaf && leftPage.Header.PageType == OdsPageType.Leaf)
        {
            var leftEntries = ReadLeafEntries(leftPage);
            if (leftEntries.Count <= 1) return false;

            var nodeEntries = ReadLeafEntries(nodePage);
            var borrowed = leftEntries[^1];
            leftEntries.RemoveAt(leftEntries.Count - 1);
            nodeEntries.Insert(0, borrowed);

            WriteLeafEntries(leftPage, leftEntries);
            WriteLeafEntries(nodePage, nodeEntries);
            _pager.WritePage(leftPage);
            _pager.WritePage(nodePage);
            parentEntries.Separators[childIndex - 1] = nodeEntries[0].Key;
            _borrowLeafCount++;
            return true;
        }

        if (nodePage.Header.PageType == OdsPageType.Internal && leftPage.Header.PageType == OdsPageType.Internal)
        {
            var leftEntries = ReadInternalEntries(leftPage);
            if (leftEntries.ChildPageIds.Count <= 2) return false;

            var nodeEntries = ReadInternalEntries(nodePage);
            var borrowedChild = leftEntries.ChildPageIds[^1];
            leftEntries.ChildPageIds.RemoveAt(leftEntries.ChildPageIds.Count - 1);
            var promotedFromLeft = leftEntries.Separators[^1];
            leftEntries.Separators.RemoveAt(leftEntries.Separators.Count - 1);
            var separatorIndex = childIndex - 1;
            var parentSeparator = parentEntries.Separators[separatorIndex];

            nodeEntries.ChildPageIds.Insert(0, borrowedChild);
            nodeEntries.Separators.Insert(0, parentSeparator);
            parentEntries.Separators[separatorIndex] = promotedFromLeft;

            if (!TryWriteInternalEntries(leftPage, leftEntries) || !TryWriteInternalEntries(nodePage, nodeEntries))
                throw new InvalidOperationException("Unable to write internal pages after left borrow.");

            _pager.WritePage(leftPage);
            _pager.WritePage(nodePage);
            _borrowInternalCount++;
            return true;
        }

        return false;
    }

    private bool TryBorrowFromRight(InternalEntries parentEntries, int childIndex, OdsPage nodePage)
    {
        var rightIndex = childIndex + 1;
        if (rightIndex >= parentEntries.ChildPageIds.Count) return false;

        using var rightPage = _pager.ReadPage(parentEntries.ChildPageIds[rightIndex]);

        if (nodePage.Header.PageType == OdsPageType.Leaf && rightPage.Header.PageType == OdsPageType.Leaf)
        {
            var rightEntries = ReadLeafEntries(rightPage);
            if (rightEntries.Count <= 1) return false;

            var nodeEntries = ReadLeafEntries(nodePage);
            var borrowed = rightEntries[0];
            rightEntries.RemoveAt(0);
            nodeEntries.Add(borrowed);

            WriteLeafEntries(nodePage, nodeEntries);
            WriteLeafEntries(rightPage, rightEntries);
            _pager.WritePage(nodePage);
            _pager.WritePage(rightPage);
            parentEntries.Separators[childIndex] = rightEntries[0].Key;
            _borrowLeafCount++;
            return true;
        }

        if (nodePage.Header.PageType == OdsPageType.Internal && rightPage.Header.PageType == OdsPageType.Internal)
        {
            var rightEntries = ReadInternalEntries(rightPage);
            if (rightEntries.ChildPageIds.Count <= 2) return false;

            var nodeEntries = ReadInternalEntries(nodePage);
            var borrowedChild = rightEntries.ChildPageIds[0];
            rightEntries.ChildPageIds.RemoveAt(0);
            var promotedFromRight = rightEntries.Separators[0];
            rightEntries.Separators.RemoveAt(0);
            var parentSeparator = parentEntries.Separators[childIndex];
            nodeEntries.ChildPageIds.Add(borrowedChild);
            nodeEntries.Separators.Add(parentSeparator);
            parentEntries.Separators[childIndex] = promotedFromRight;

            if (!TryWriteInternalEntries(nodePage, nodeEntries) || !TryWriteInternalEntries(rightPage, rightEntries))
                throw new InvalidOperationException("Unable to write internal pages after right borrow.");

            _pager.WritePage(nodePage);
            _pager.WritePage(rightPage);
            _borrowInternalCount++;
            return true;
        }

        return false;
    }

    private bool HasUnderflow(OdsPage page, bool isRoot)
    {
        if (isRoot) return false;
        if (page.Header.PageType == OdsPageType.Leaf)
            return page.Header.ItemCount == 0;
        if (page.Header.PageType == OdsPageType.Internal)
        {
            var entries = ReadInternalEntries(page);
            return entries.ChildPageIds.Count < 2;
        }
        throw new NotSupportedException("Unknown page type in underflow check.");
    }

    private bool UpdateSeparatorForChild(InternalEntries parentEntries, int childIndex)
    {
        if (childIndex <= 0) return false;

        using var childPage = _pager.ReadPage(parentEntries.ChildPageIds[childIndex]);
        var newSeparator = ReadMinimumKey(childPage);
        var separatorIndex = childIndex - 1;
        if (CompareKeys(parentEntries.Separators[separatorIndex], newSeparator) == 0)
            return false;

        parentEntries.Separators[separatorIndex] = newSeparator;
        return true;
    }

    private byte[] ReadMinimumKey(OdsPage page)
    {
        if (page.Header.PageType == OdsPageType.Leaf)
        {
            var entries = ReadLeafEntries(page);
            if (entries.Count == 0)
                throw new InvalidOperationException("Cannot read minimum key from empty leaf.");
            return entries[0].Key;
        }

        if (page.Header.PageType == OdsPageType.Internal)
        {
            var internalEntries = ReadInternalEntries(page);
            using var leftMostChild = _pager.ReadPage(internalEntries.ChildPageIds[0]);
            return ReadMinimumKey(leftMostChild);
        }

        throw new NotSupportedException("Unknown page type while reading minimum key.");
    }

    private void MergeChildrenIntoLeft(InternalEntries parentEntries, int separatorIndex,
        OdsPage leftPage, OdsPage rightPage)
    {
        if (leftPage.Header.PageType == OdsPageType.Leaf && rightPage.Header.PageType == OdsPageType.Leaf)
        {
            var merged = ReadLeafEntries(leftPage);
            merged.AddRange(ReadLeafEntries(rightPage));
            WriteLeafEntries(leftPage, merged);
            _pager.WritePage(leftPage);
            parentEntries.ChildPageIds.RemoveAt(separatorIndex + 1);
            parentEntries.Separators.RemoveAt(separatorIndex);
            _mergeLeafCount++;
            return;
        }

        if (leftPage.Header.PageType == OdsPageType.Internal && rightPage.Header.PageType == OdsPageType.Internal)
        {
            var leftEntries = ReadInternalEntries(leftPage);
            var rightEntries = ReadInternalEntries(rightPage);
            var promotedSeparator = parentEntries.Separators[separatorIndex];
            leftEntries.Separators.Add(promotedSeparator);
            leftEntries.Separators.AddRange(rightEntries.Separators);
            leftEntries.ChildPageIds.AddRange(rightEntries.ChildPageIds);

            if (!TryWriteInternalEntries(leftPage, leftEntries))
                throw new NotSupportedException("Internal merge overflow during delete rebalance.");

            _pager.WritePage(leftPage);
            parentEntries.ChildPageIds.RemoveAt(separatorIndex + 1);
            parentEntries.Separators.RemoveAt(separatorIndex);
            _mergeInternalCount++;
            return;
        }

        throw new NotSupportedException("Cannot merge different page types.");
    }

    private void MergeRightIntoLeft(InternalEntries parentEntries, int separatorIndex,
        OdsPage leftPage, OdsPage rightPage)
    {
        MergeChildrenIntoLeft(parentEntries, separatorIndex, leftPage, rightPage);
    }

    private void NormalizeRoot()
    {
        var metadata = _pager.ReadRootMetadata();
        using var rootPage = _pager.ReadPage(metadata.RootPageId);
        if (rootPage.Header.PageType != OdsPageType.Internal) return;

        var rootEntries = ReadInternalEntries(rootPage);
        if (rootEntries.ChildPageIds.Count != 1) return;

        _pager.WriteRootMetadata(metadata.WithRootPageId(rootEntries.ChildPageIds[0]));
    }

    private List<LeafEntry> ReadLeafEntries(OdsPage page)
    {
        var header = page.Header;
        var body = page.Body;
        var entries = new List<LeafEntry>(header.ItemCount);

        var offset = 0;
        for (var i = 0; i < header.ItemCount; i++)
        {
            if (offset + sizeof(int) + sizeof(int) > body.Length)
                throw new InvalidOperationException("Leaf page payload is corrupted.");

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            if (keyLength < 0 || valueLength < 0)
                throw new InvalidOperationException("Leaf page contains invalid key/value lengths.");

            if (keyLength == 0)
            {
                // Continuation page reached via RightSibling — the caller should not
                // navigate here directly. Skip and let the primary-page read handle it.
                break;
            }

            if (offset + keyLength + valueLength > body.Length)
                throw new InvalidOperationException("Leaf page payload exceeds page body size.");

            var key = body.Slice(offset, keyLength).ToArray();
            offset += keyLength;

            var value = body.Slice(offset, valueLength).ToArray();
            offset += valueLength;

            entries.Add(new LeafEntry(key, value));
        }

        // Check for overflow continuation chain after the last entry.
        if (entries.Count > 0 && page.Header.RightSiblingPageId >= 0)
            AppendOverflowChains(entries, page.Header.RightSiblingPageId);

        return entries;
    }

    /// <summary>
    /// If the page at <paramref name="siblingId"/> is a continuation page (first entry has keyLen=0),
    /// reassemble the last entry in <paramref name="entries"/> by appending all continuation chunks.
    /// Returns true if the overflow was consumed so the caller can decide on sibling semantics.
    /// </summary>
    private void AppendOverflowChains(List<LeafEntry> entries, int siblingId)
    {
        var lastEntry = entries[entries.Count - 1];
        var chunks = new List<byte[]>();
        int pageId = siblingId;

        while (pageId >= 0)
        {
            using var contPage = _pager.ReadPage(pageId);
            if (contPage.Header.PageType != OdsPageType.Leaf || contPage.Header.ItemCount == 0)
                break;

            var body = contPage.Body;
            int off = 0;
            if (off + sizeof(int) + sizeof(int) > body.Length)
                break;

            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(body[off..]);
            off += sizeof(int);
            if (keyLen != 0)
                break; // not a continuation page

            int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(body[off..]);
            off += sizeof(int);
            if (chunkLen <= 0 || off + chunkLen > body.Length)
                break;

            chunks.Add(body.Slice(off, chunkLen).ToArray());
            pageId = contPage.Header.RightSiblingPageId;
        }

        if (chunks.Count == 0)
            return;

        int totalLen = lastEntry.Value.Length;
        foreach (var c in chunks) totalLen += c.Length;
        var fullValue = new byte[totalLen];
        lastEntry.Value.CopyTo(fullValue, 0);
        int destOff = lastEntry.Value.Length;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(fullValue, destOff);
            destOff += chunk.Length;
        }

        entries[entries.Count - 1] = new LeafEntry(lastEntry.Key, fullValue);
    }

    /// <summary>
    /// If <paramref name="siblingId"/> starts a continuation chain (first entry keyLen=0),
    /// read all chunks and return the full reassembled value. Returns null if not a continuation.
    /// </summary>
    private byte[]? TryReadOverflowValue(byte[] pageBuffer, int valueStart, int valueLength, int siblingId)
    {
        // Peek at the sibling's first key length to avoid an unnecessary page read.
        using var firstCont = _pager.ReadPage(siblingId);
        if (firstCont.Header.PageType != OdsPageType.Leaf || firstCont.Header.ItemCount == 0)
            return null;

        var body = firstCont.Body;
        if (body.Length < sizeof(int) + sizeof(int))
            return null;

        int keyLen = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (keyLen != 0)
            return null; // not a continuation page

        // Read all chunks: first chunk is in the page buffer, continuation pages have the rest.
        var chunks = new List<byte[]> { pageBuffer.AsSpan(valueStart, valueLength).ToArray() };
        int pageId = siblingId;

        while (pageId >= 0)
        {
            using var contPage = pageId == siblingId ? null : _pager.ReadPage(pageId);
            var cont = pageId == siblingId ? firstCont : contPage;
            if (cont == null) break;

            var contBody = cont.Body;
            int off = 0;
            int kl = BinaryPrimitives.ReadInt32LittleEndian(contBody[off..]);
            off += sizeof(int);
            if (kl != 0)
                break; // not a continuation page

            int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(contBody[off..]);
            off += sizeof(int);
            if (chunkLen <= 0 || off + chunkLen > contBody.Length)
                break;

            chunks.Add(contBody.Slice(off, chunkLen).ToArray());

            pageId = cont.Header.RightSiblingPageId;
            if (pageId == siblingId) break; // safety: prevent infinite loop
        }

        if (chunks.Count <= 1)
            return null;

        int totalLen = 0;
        foreach (var c in chunks) totalLen += c.Length;
        var fullValue = new byte[totalLen];
        int destOff = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(fullValue, destOff);
            destOff += chunk.Length;
        }

        return fullValue;
    }

    private static void WriteLeafEntries(OdsPage page, IReadOnlyList<LeafEntry> entries)
    {
        if (!TryWriteLeafEntries(page, entries))
            throw new InvalidOperationException("Leaf page is full. Node split is required.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWriteLeafEntriesRange(
        OdsPage page, IReadOnlyList<LeafEntry> entries, int start, int count)
    {
        var body = page.Body;
        var end = start + count;
        var offset = 0;

        for (var i = start; i < end; i++)
        {
            var entry = entries[i];
            var required = (sizeof(int) * 2) + entry.Key.Length + entry.Value.Length;
            if (offset + required > body.Length)
            {
                body.Clear();
                return false;
            }

            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entry.Key.Length);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entry.Value.Length);
            offset += sizeof(int);
            entry.Key.CopyTo(body[offset..]);
            offset += entry.Key.Length;
            entry.Value.CopyTo(body[offset..]);
            offset += entry.Value.Length;
        }

        body.Slice(offset).Clear();

        var header = page.Header;
        page.Header = header with { PageType = OdsPageType.Leaf, ItemCount = count };
        return true;
    }

    private static bool TryWriteLeafEntries(OdsPage page, IReadOnlyList<LeafEntry> entries)
    {
        var body = page.Body;
        body.Clear();

        var offset = 0;
        foreach (var entry in entries)
        {
            var required = sizeof(int) + sizeof(int) + entry.Key.Length + entry.Value.Length;
            if (offset + required > body.Length)
                return false;

            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entry.Key.Length);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entry.Value.Length);
            offset += sizeof(int);
            entry.Key.CopyTo(body[offset..]);
            offset += entry.Key.Length;
            entry.Value.CopyTo(body[offset..]);
            offset += entry.Value.Length;
        }

        var header = page.Header;
        page.Header = header with { PageType = OdsPageType.Leaf, ItemCount = entries.Count };
        return true;
    }

    private static InternalEntries ReadInternalEntries(OdsPage page)
    {
        var body = page.Body;
        var offset = 0;

        if (body.Length < sizeof(int))
            throw new InvalidOperationException("Internal page payload is corrupted.");

        var childCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);
        if (childCount <= 0)
            throw new InvalidOperationException("Internal page has invalid child count.");

        var children = new List<int>(childCount);
        for (var i = 0; i < childCount; i++)
        {
            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("Internal page children are truncated.");

            children.Add(BinaryPrimitives.ReadInt32LittleEndian(body[offset..]));
            offset += sizeof(int);
        }

        if (offset + sizeof(int) > body.Length)
            throw new InvalidOperationException("Internal page separator count is truncated.");

        var separatorCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);
        if (separatorCount != page.Header.ItemCount)
            throw new InvalidOperationException("Internal page header count mismatch.");

        var separators = new List<byte[]>(separatorCount);
        for (var i = 0; i < separatorCount; i++)
        {
            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("Internal page separator length is truncated.");

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);
            if (keyLength <= 0 || offset + keyLength > body.Length)
                throw new InvalidOperationException("Internal page separator payload is corrupted.");

            separators.Add(body.Slice(offset, keyLength).ToArray());
            offset += keyLength;
        }

        if (children.Count != separators.Count + 1)
            throw new InvalidOperationException("Internal page child/separator relationship is invalid.");

        return new InternalEntries(children, separators);
    }

    private static bool TryWriteInternalEntries(OdsPage page, InternalEntries entries)
    {
        var body = page.Body;
        body.Clear();

        var offset = 0;
        var requiredForCounts = sizeof(int) + sizeof(int);
        if (requiredForCounts > body.Length) return false;

        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entries.ChildPageIds.Count);
        offset += sizeof(int);

        foreach (var child in entries.ChildPageIds)
        {
            if (offset + sizeof(int) > body.Length) return false;
            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], child);
            offset += sizeof(int);
        }

        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entries.Separators.Count);
        offset += sizeof(int);

        foreach (var separator in entries.Separators)
        {
            var required = sizeof(int) + separator.Length;
            if (offset + required > body.Length) return false;
            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], separator.Length);
            offset += sizeof(int);
            separator.CopyTo(body[offset..]);
            offset += separator.Length;
        }

        var header = page.Header;
        page.Header = header with { PageType = OdsPageType.Internal, ItemCount = entries.Separators.Count };
        return true;
    }

    private readonly record struct LeafEntry(byte[] Key, byte[] Value);

    private sealed class InternalEntries
    {
        public InternalEntries(List<int> childPageIds, List<byte[]> separators)
        {
            ChildPageIds = childPageIds;
            Separators = separators;
        }

        public List<int> ChildPageIds { get; }
        public List<byte[]> Separators { get; }
    }

    private readonly record struct PathFrame(OdsPage Page, InternalEntries InternalEntries, int ChildIndex);
    private readonly record struct UpsertPathFrame(int PageId, int ChildIndex);
}
