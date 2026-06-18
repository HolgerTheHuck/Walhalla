// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Ods.Pages;
using Walhalla.Storage.Ods.Paging;

namespace Walhalla.Storage.Ods.Tree;

internal sealed class BPlusTree : IDisposable
{
    private readonly OdsPager _pager;
    private readonly IKeyComparator _keyComparator;
    private long _borrowLeafCount;
    private long _borrowInternalCount;
    private long _mergeLeafCount;
    private long _mergeInternalCount;

    public BPlusTree(OdsPager pager, IKeyComparator? keyComparator = null)
    {
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        _keyComparator = keyComparator ?? BuiltInKeyComparators.Bytewise;
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
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        if (key.Length == 0)
            throw new ArgumentException("Key must not be empty.", nameof(key));

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
            SplitLeaf(currentPage, leafEntries, out var promotedKey, out var newRightPageId);
            currentPage.Dispose();

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
                    return;
                }

                SplitInternal(parentPage, parentEntries, out promotedKey, out newRightPageId);
            }

            PromoteNewRoot(rootId, promotedKey, newRightPageId);
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

        var traversalPages = new System.Collections.Generic.List<OdsPage>();
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
                if (CompareKeys(leafEntries[i].Key, key) != 0)
                    continue;

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

                if (TryBorrowFromLeft(parentEntries, childIndex, nodePage) || TryBorrowFromRight(parentEntries, childIndex, nodePage))
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

    /// <summary>
    /// Enumerates all entries in sorted key order without materialising the full result set.
    /// Preferred over <see cref="SnapshotEntries"/> when the caller processes entries
    /// one-by-one (e.g. delta merge during checkpoint) to avoid large allocations.
    /// </summary>
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
            // page disposed here by `using` – child page IDs already copied into stack
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
            // page disposed here – child IDs already queued
        }

        return result;
    }

    /// <summary>
    /// Enumerates all entries whose keys fall in the range
    /// [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>).
    /// Pass <see langword="null"/> for an open-ended bound.
    /// Entries are yielded in key order; internal B-tree separator keys are used to
    /// prune whole subtrees that lie completely outside the requested range.
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateRange(
        byte[]? fromInclusive = null,
        byte[]? toExclusive   = null)
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
                        break; // leaf is sorted ascending – nothing further can match
                    yield return new KeyValuePair<byte[], byte[]>(entry.Key, entry.Value);
                }
                continue;
            }

            if (page.Header.PageType != OdsPageType.Internal)
                throw new NotSupportedException("Unsupported page type during range traversal.");

            var internalEntries = ReadInternalEntries(page);
            // Push children right-to-left so they are popped (and visited) left-to-right.
            for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
            {
                // child[i] covers keys in ( sep[i-1], sep[i] ) roughly:
                //   lower bound: >= sep[i-1]  (or -∞ when i == 0)
                //   upper bound: <  sep[i]    (or +∞ when i == last child)

                // Skip child if ALL its keys are strictly below fromInclusive.
                // That is: child's max possible key < fromInclusive
                //   ⟺ fromInclusive >= sep[i]  (separator is the exclusive upper bound for child i when i < n)
                if (fromInclusive != null && i < internalEntries.Separators.Count &&
                    CompareKeys(fromInclusive, internalEntries.Separators[i]) >= 0)
                    continue;

                // Skip child if ALL its keys are at-or-above toExclusive.
                // That is: child's min possible key >= toExclusive
                //   ⟺ toExclusive <= sep[i-1]  (separator is the inclusive lower bound for child i when i > 0)
                if (toExclusive != null && i > 0 &&
                    CompareKeys(toExclusive, internalEntries.Separators[i - 1]) <= 0)
                    continue;

                stack.Push(internalEntries.ChildPageIds[i]);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Async public API  (await-friendly counterparts to all sync methods above)
    // ═══════════════════════════════════════════════════════════════════════════

    public async ValueTask<byte[]?> TryGetAsync(byte[] key, CancellationToken ct = default)
    {
        if (key == null || key.Length == 0)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        using var leafPage = await ReadLeafForKeyAsync(key, ct).ConfigureAwait(false);
        var entries = ReadLeafEntries(leafPage);
        foreach (var entry in entries)
        {
            if (CompareKeys(entry.Key, key) != 0)
                continue;
            return (byte[])entry.Value.Clone();
        }
        return null;
    }

    /// <summary>Returns <see langword="null"/> when key is not found.</summary>
    public ValueTask<byte[]?> TryGetAsync(ReadOnlySpan<byte> key, CancellationToken ct = default)
        => TryGetAsync(key.ToArray(), ct);

    public ValueTask UpsertAsync(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
                                 CancellationToken ct = default)
        => UpsertAsync(key.ToArray(), value.ToArray(), ct);

    public ValueTask UpsertAsync(byte[] key, byte[] value, CancellationToken ct = default)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (key.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));
        return UpsertCoreAsync(key, value, ct);
    }

    public ValueTask<bool> DeleteAsync(ReadOnlySpan<byte> key, CancellationToken ct = default)
        => DeleteAsync(key.ToArray(), ct);

    public async ValueTask<bool> DeleteAsync(byte[] key, CancellationToken ct = default)
    {
        if (key == null || key.Length == 0)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        var traversalPages = new List<OdsPage>();
        _pager.BeginWriteBatch();
        try
        {
            var path = new Stack<PathFrame>();
            var currentPage = await _pager.ReadPageAsync(_pager.ReadRootMetadata().RootPageId, ct).ConfigureAwait(false);
            traversalPages.Add(currentPage);

            while (currentPage.Header.PageType == OdsPageType.Internal)
            {
                var internalEntries = ReadInternalEntries(currentPage);
                var childIndex = FindChildIndex(internalEntries.Separators, key);
                path.Push(new PathFrame(currentPage, internalEntries, childIndex));
                currentPage = await _pager.ReadPageAsync(internalEntries.ChildPageIds[childIndex], ct).ConfigureAwait(false);
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
            await _pager.WritePageAsync(currentPage, ct).ConfigureAwait(false);

            var nodePageId = currentPage.PageId;

            while (path.Count > 0)
            {
                var parentFrame = path.Pop();
                var parentEntries = ReadInternalEntries(parentFrame.Page);
                var childIndex = parentEntries.ChildPageIds.IndexOf(nodePageId);
                if (childIndex < 0)
                    throw new InvalidOperationException("Delete rebalance lost parent-child linkage.");

                using var nodePage = await _pager.ReadPageAsync(nodePageId, ct).ConfigureAwait(false);

                if (!HasUnderflow(nodePage, isRoot: false))
                {
                    var separatorChanged = await UpdateSeparatorForChildAsync(parentEntries, childIndex, ct).ConfigureAwait(false);
                    if (separatorChanged)
                    {
                        if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                            throw new InvalidOperationException("Unable to persist parent separator update.");
                        await _pager.WritePageAsync(parentFrame.Page, ct).ConfigureAwait(false);
                    }
                    await NormalizeRootAsync(ct).ConfigureAwait(false);
                    await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
                    return true;
                }

                if (await TryBorrowFromLeftAsync(parentEntries, childIndex, nodePage, ct).ConfigureAwait(false) ||
                    await TryBorrowFromRightAsync(parentEntries, childIndex, nodePage, ct).ConfigureAwait(false))
                {
                    if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                        throw new InvalidOperationException("Unable to persist parent after borrow rebalance.");
                    await _pager.WritePageAsync(parentFrame.Page, ct).ConfigureAwait(false);
                    await NormalizeRootAsync(ct).ConfigureAwait(false);
                    await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
                    return true;
                }

                if (childIndex > 0)
                {
                    using var leftPage = await _pager.ReadPageAsync(parentEntries.ChildPageIds[childIndex - 1], ct).ConfigureAwait(false);
                    await MergeChildrenIntoLeftAsync(parentEntries, childIndex - 1, leftPage, nodePage, ct).ConfigureAwait(false);
                    if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                        throw new InvalidOperationException("Unable to persist parent after left-merge.");
                    await _pager.WritePageAsync(parentFrame.Page, ct).ConfigureAwait(false);
                    nodePageId = parentFrame.Page.PageId;
                    continue;
                }

                if (parentEntries.ChildPageIds.Count <= 1)
                    throw new InvalidOperationException("Invalid internal node state during delete rebalance.");

                using var rightPage = await _pager.ReadPageAsync(parentEntries.ChildPageIds[1], ct).ConfigureAwait(false);
                await MergeRightIntoLeftAsync(parentEntries, 0, nodePage, rightPage, ct).ConfigureAwait(false);
                if (!TryWriteInternalEntries(parentFrame.Page, parentEntries))
                    throw new InvalidOperationException("Unable to persist parent after right-merge.");
                await _pager.WritePageAsync(parentFrame.Page, ct).ConfigureAwait(false);
                nodePageId = parentFrame.Page.PageId;
            }

            await NormalizeRootAsync(ct).ConfigureAwait(false);
            await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _pager.AbortWriteBatch();
            throw;
        }
        finally
        {
            foreach (var p in traversalPages) p.Dispose();
        }
    }

    public async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> EnumerateEntriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            using var page = await _pager.ReadPageAsync(stack.Pop(), ct).ConfigureAwait(false);

            if (page.Header.PageType == OdsPageType.Leaf)
            {
                var leafEntries = ReadLeafEntries(page);
                foreach (var entry in leafEntries)
                    yield return new KeyValuePair<byte[], byte[]>(entry.Key, entry.Value);
                continue;
            }

            if (page.Header.PageType != OdsPageType.Internal)
                throw new NotSupportedException("Unsupported page type encountered during async enumeration.");

            var internalEntries = ReadInternalEntries(page);
            for (var i = internalEntries.ChildPageIds.Count - 1; i >= 0; i--)
                stack.Push(internalEntries.ChildPageIds[i]);
        }
    }

    public async Task<IReadOnlyList<KeyValuePair<byte[], byte[]>>> SnapshotEntriesAsync(CancellationToken ct = default)
    {
        var result = new List<KeyValuePair<byte[], byte[]>>();
        await foreach (var entry in EnumerateEntriesAsync(ct).ConfigureAwait(false))
            result.Add(entry);
        return result;
    }

    /// <inheritdoc cref="EnumerateRange"/>
    public async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> EnumerateRangeAsync(
        byte[]? fromInclusive = null,
        byte[]? toExclusive   = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = new Stack<int>();
        stack.Push(_pager.ReadRootMetadata().RootPageId);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            using var page = await _pager.ReadPageAsync(stack.Pop(), ct).ConfigureAwait(false);

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
                throw new NotSupportedException("Unsupported page type during async range traversal.");

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

    // ═══════════════════════════════════════════════════════════════════════════
    //  Async private helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async ValueTask UpsertCoreAsync(byte[] keyBytes, byte[] valueBytes, CancellationToken ct)
    {
        _pager.BeginWriteBatch();
        try
        {
            var path = new Stack<UpsertPathFrame>();
            var rootId = _pager.ReadRootMetadata().RootPageId;
            var currentPage = await _pager.ReadPageAsync(rootId, ct).ConfigureAwait(false);

            while (currentPage.Header.PageType == OdsPageType.Internal)
            {
                var childIndex = FindChildIndexNonAlloc(currentPage, keyBytes);
                path.Push(new UpsertPathFrame(currentPage.PageId, childIndex));
                var childPageId = ReadChildPageId(currentPage, childIndex);
                currentPage.Dispose();
                currentPage = await _pager.ReadPageAsync(childPageId, ct).ConfigureAwait(false);
            }

            if (currentPage.Header.PageType != OdsPageType.Leaf)
            {
                currentPage.Dispose();
                throw new NotSupportedException("Unsupported page type encountered during async insert traversal.");
            }

            if (TryUpsertLeafInPlace(currentPage, keyBytes, valueBytes))
            {
                await _pager.WritePageAsync(currentPage, ct).ConfigureAwait(false);
                currentPage.Dispose();
                await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
                return;
            }

            var leafEntries = ReadLeafEntries(currentPage);
            InsertOrReplace(leafEntries, keyBytes, valueBytes);
            await SplitLeafAsync(currentPage, leafEntries, ct).ConfigureAwait(false);
            var (promotedKey, newRightPageId) = _lastSplit;
            currentPage.Dispose();

            while (path.Count > 0)
            {
                var frame = path.Pop();
                using var parentPage = await _pager.ReadPageAsync(frame.PageId, ct).ConfigureAwait(false);
                var parentEntries = ReadInternalEntries(parentPage);
                parentEntries.Separators.Insert(frame.ChildIndex, promotedKey);
                parentEntries.ChildPageIds.Insert(frame.ChildIndex + 1, newRightPageId);

                if (TryWriteInternalEntries(parentPage, parentEntries))
                {
                    await _pager.WritePageAsync(parentPage, ct).ConfigureAwait(false);
                    await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
                    return;
                }

                await SplitInternalAsync(parentPage, parentEntries, ct).ConfigureAwait(false);
                (promotedKey, newRightPageId) = _lastSplit;
            }

            await PromoteNewRootAsync(rootId, promotedKey, newRightPageId, ct).ConfigureAwait(false);
            await _pager.CommitWriteBatchAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _pager.AbortWriteBatch();
            throw;
        }
    }

    // Temporary field used to pass split results out of async helper methods
    // (C# async methods cannot have `out` parameters).
    private (byte[] PromotedKey, int NewRightPageId) _lastSplit;

    private async ValueTask SplitLeafAsync(OdsPage page, List<LeafEntry> entries, CancellationToken ct)
    {
        var splitIndex = entries.Count / 2;
        var rightCount = entries.Count - splitIndex;

        // Allocate the new right page first so its ID is available for sibling-link wiring.
        using var newRight = await _pager.AllocatePageAsync(OdsPageType.Leaf, -1, ct).ConfigureAwait(false);

        // ── Sibling-link maintenance ────────────────────────────────────────────
        var originalRightSiblingId = page.Header.RightSiblingPageId;
        newRight.Header = newRight.Header with { RightSiblingPageId = originalRightSiblingId };
        page.Header     = page.Header     with { RightSiblingPageId = newRight.PageId };

        if (!TryWriteLeafEntriesRange(page, entries, 0, splitIndex))
            throw new InvalidOperationException(
                $"Leaf split failed: left half ({splitIndex} entries) does not fit in page " +
                $"(body={page.Body.Length} bytes). Increase the page size.");
        await _pager.WritePageAsync(page, ct).ConfigureAwait(false);

        if (!TryWriteLeafEntriesRange(newRight, entries, splitIndex, rightCount))
            throw new InvalidOperationException(
                $"Leaf split failed: right half ({rightCount} entries) does not fit in page " +
                $"(body={newRight.Body.Length} bytes). Increase the page size.");
        await _pager.WritePageAsync(newRight, ct).ConfigureAwait(false);

        _lastSplit = (entries[splitIndex].Key, newRight.PageId);
    }

    private async ValueTask SplitInternalAsync(OdsPage page, InternalEntries entries, CancellationToken ct)
    {
        var separatorMid = entries.Separators.Count / 2;
        var promotedKey = entries.Separators[separatorMid];

        var leftSeparators = entries.Separators.GetRange(0, separatorMid);
        var rightSeparators = entries.Separators.GetRange(separatorMid + 1, entries.Separators.Count - separatorMid - 1);
        var leftChildren = entries.ChildPageIds.GetRange(0, separatorMid + 1);
        var rightChildren = entries.ChildPageIds.GetRange(separatorMid + 1, entries.ChildPageIds.Count - (separatorMid + 1));

        var left = new InternalEntries(leftChildren, leftSeparators);
        if (!TryWriteInternalEntries(page, left))
            throw new InvalidOperationException("Unable to write left internal split page.");
        await _pager.WritePageAsync(page, ct).ConfigureAwait(false);

        using var rightPage = await _pager.AllocatePageAsync(OdsPageType.Internal, -1, ct).ConfigureAwait(false);
        var right = new InternalEntries(rightChildren, rightSeparators);
        if (!TryWriteInternalEntries(rightPage, right))
            throw new InvalidOperationException("Unable to write right internal split page.");
        await _pager.WritePageAsync(rightPage, ct).ConfigureAwait(false);

        _lastSplit = (promotedKey, rightPage.PageId);
    }

    private async ValueTask PromoteNewRootAsync(int leftChildPageId, byte[] promotedKey, int rightChildPageId,
                                                CancellationToken ct)
    {
        using var newRoot = await _pager.AllocatePageAsync(OdsPageType.Internal, -1, ct).ConfigureAwait(false);
        var internalEntries = new InternalEntries(
            new List<int> { leftChildPageId, rightChildPageId },
            new List<byte[]> { promotedKey });
        if (!TryWriteInternalEntries(newRoot, internalEntries))
            throw new InvalidOperationException("Unable to write promoted root page.");
        await _pager.WritePageAsync(newRoot, ct).ConfigureAwait(false);

        var metadata = _pager.ReadRootMetadata();
        await _pager.WriteRootMetadataAsync(new OdsRootMetadata(newRoot.PageId, metadata.LastAllocatedPageId), ct).ConfigureAwait(false);
    }

    private async ValueTask<OdsPage> ReadLeafForKeyAsync(byte[] key, CancellationToken ct)
    {
        var page = await _pager.ReadPageAsync(_pager.ReadRootMetadata().RootPageId, ct).ConfigureAwait(false);

        while (page.Header.PageType == OdsPageType.Internal)
        {
            var childIndex = FindChildIndexNonAlloc(page, key);
            var childPageId = ReadChildPageId(page, childIndex);
            page.Dispose();
            page = await _pager.ReadPageAsync(childPageId, ct).ConfigureAwait(false);
        }

        if (page.Header.PageType != OdsPageType.Leaf)
        {
            page.Dispose();
            throw new NotSupportedException("Unsupported page type encountered during async read traversal.");
        }
        return page;
    }

    private async ValueTask<bool> TryBorrowFromLeftAsync(InternalEntries parentEntries, int childIndex,
                                                          OdsPage nodePage, CancellationToken ct)
    {
        if (childIndex <= 0) return false;
        using var leftPage = await _pager.ReadPageAsync(parentEntries.ChildPageIds[childIndex - 1], ct).ConfigureAwait(false);

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
            await _pager.WritePageAsync(leftPage, ct).ConfigureAwait(false);
            await _pager.WritePageAsync(nodePage, ct).ConfigureAwait(false);
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
                throw new InvalidOperationException("Unable to write internal pages after async left borrow.");

            await _pager.WritePageAsync(leftPage, ct).ConfigureAwait(false);
            await _pager.WritePageAsync(nodePage, ct).ConfigureAwait(false);
            _borrowInternalCount++;
            return true;
        }

        return false;
    }

    private async ValueTask<bool> TryBorrowFromRightAsync(InternalEntries parentEntries, int childIndex,
                                                           OdsPage nodePage, CancellationToken ct)
    {
        var rightIndex = childIndex + 1;
        if (rightIndex >= parentEntries.ChildPageIds.Count) return false;
        using var rightPage = await _pager.ReadPageAsync(parentEntries.ChildPageIds[rightIndex], ct).ConfigureAwait(false);

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
            await _pager.WritePageAsync(nodePage, ct).ConfigureAwait(false);
            await _pager.WritePageAsync(rightPage, ct).ConfigureAwait(false);
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
                throw new InvalidOperationException("Unable to write internal pages after async right borrow.");

            await _pager.WritePageAsync(nodePage, ct).ConfigureAwait(false);
            await _pager.WritePageAsync(rightPage, ct).ConfigureAwait(false);
            _borrowInternalCount++;
            return true;
        }

        return false;
    }

    private async ValueTask<bool> UpdateSeparatorForChildAsync(InternalEntries parentEntries, int childIndex,
                                                                CancellationToken ct)
    {
        if (childIndex <= 0) return false;
        using var childPage = await _pager.ReadPageAsync(parentEntries.ChildPageIds[childIndex], ct).ConfigureAwait(false);
        var newSeparator = await ReadMinimumKeyAsync(childPage, ct).ConfigureAwait(false);
        var separatorIndex = childIndex - 1;
        if (CompareKeys(parentEntries.Separators[separatorIndex], newSeparator) == 0) return false;
        parentEntries.Separators[separatorIndex] = newSeparator;
        return true;
    }

    private async ValueTask<byte[]> ReadMinimumKeyAsync(OdsPage page, CancellationToken ct)
    {
        if (page.Header.PageType == OdsPageType.Leaf)
        {
            var entries = ReadLeafEntries(page);
            if (entries.Count == 0) throw new InvalidOperationException("Cannot read minimum key from empty leaf.");
            return entries[0].Key;
        }

        if (page.Header.PageType == OdsPageType.Internal)
        {
            var internalEntries = ReadInternalEntries(page);
            using var leftMostChild = await _pager.ReadPageAsync(internalEntries.ChildPageIds[0], ct).ConfigureAwait(false);
            return await ReadMinimumKeyAsync(leftMostChild, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException("Unknown page type while reading minimum key (async).");
    }

    private async ValueTask MergeChildrenIntoLeftAsync(InternalEntries parentEntries, int separatorIndex,
                                                        OdsPage leftPage, OdsPage rightPage, CancellationToken ct)
    {
        if (leftPage.Header.PageType == OdsPageType.Leaf && rightPage.Header.PageType == OdsPageType.Leaf)
        {
            var merged = ReadLeafEntries(leftPage);
            merged.AddRange(ReadLeafEntries(rightPage));
            WriteLeafEntries(leftPage, merged);
            await _pager.WritePageAsync(leftPage, ct).ConfigureAwait(false);
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
                throw new NotSupportedException("Internal merge overflow during async delete rebalance.");

            await _pager.WritePageAsync(leftPage, ct).ConfigureAwait(false);
            parentEntries.ChildPageIds.RemoveAt(separatorIndex + 1);
            parentEntries.Separators.RemoveAt(separatorIndex);
            _mergeInternalCount++;
            return;
        }

        throw new NotSupportedException("Cannot merge different page types (async).");
    }

    private ValueTask MergeRightIntoLeftAsync(InternalEntries parentEntries, int separatorIndex,
                                               OdsPage leftPage, OdsPage rightPage, CancellationToken ct)
        => MergeChildrenIntoLeftAsync(parentEntries, separatorIndex, leftPage, rightPage, ct);

    private async ValueTask NormalizeRootAsync(CancellationToken ct)
    {
        var metadata = _pager.ReadRootMetadata();
        using var rootPage = await _pager.ReadPageAsync(metadata.RootPageId, ct).ConfigureAwait(false);
        if (rootPage.Header.PageType != OdsPageType.Internal) return;
        var rootEntries = ReadInternalEntries(rootPage);
        if (rootEntries.ChildPageIds.Count != 1) return;
        await _pager.WriteRootMetadataAsync(new OdsRootMetadata(rootEntries.ChildPageIds[0], metadata.LastAllocatedPageId), ct).ConfigureAwait(false);
    }


    private OdsPage ReadLeafForKey(ReadOnlySpan<byte> key)
    {
        var page = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);

        while (page.Header.PageType == OdsPageType.Internal)
        {
            var childIndex = FindChildIndexNonAlloc(page, key);
            var childPageId = ReadChildPageId(page, childIndex);
            page.Dispose();         // done with this internal page
            page = _pager.ReadPage(childPageId);
        }

        if (page.Header.PageType != OdsPageType.Leaf)
        {
            page.Dispose();
            throw new NotSupportedException("Unsupported page type encountered during read traversal.");
        }

        return page;    // caller owns and must dispose
    }

    private void SplitLeaf(OdsPage page, List<LeafEntry> entries, out byte[] promotedKey, out int newRightPageId)
    {
        var splitIndex = entries.Count / 2;
        var rightCount = entries.Count - splitIndex;

        // Allocate the new right page first so its ID is available for sibling-link wiring.
        using var newRight = _pager.AllocatePage(OdsPageType.Leaf, -1);

        // ── Sibling-link maintenance ────────────────────────────────────────────
        // Before: page  →  originalRight
        // After:  page  →  newRight  →  originalRight
        var originalRightSiblingId = page.Header.RightSiblingPageId;
        newRight.Header = newRight.Header with { RightSiblingPageId = originalRightSiblingId };
        page.Header     = page.Header     with { RightSiblingPageId = newRight.PageId };

        // Write both halves without GetRange() allocations (zero-alloc range writer).
        if (!TryWriteLeafEntriesRange(page, entries, 0, splitIndex))
            throw new InvalidOperationException(
                $"Leaf split failed: left half ({splitIndex} entries) does not fit in page " +
                $"(body={page.Body.Length} bytes). Increase the page size.");
        _pager.WritePage(page);

        if (!TryWriteLeafEntriesRange(newRight, entries, splitIndex, rightCount))
            throw new InvalidOperationException(
                $"Leaf split failed: right half ({rightCount} entries) does not fit in page " +
                $"(body={newRight.Body.Length} bytes). Increase the page size.");
        _pager.WritePage(newRight);

        promotedKey    = entries[splitIndex].Key;
        newRightPageId = newRight.PageId;
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
        _pager.WriteRootMetadata(new OdsRootMetadata(newRoot.PageId, metadata.LastAllocatedPageId));
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

    private int CompareKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return _keyComparator.Compare(left, right);
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
        if (childIndex <= 0)
            return false;

        using var leftPage = _pager.ReadPage(parentEntries.ChildPageIds[childIndex - 1]);

        if (nodePage.Header.PageType == OdsPageType.Leaf && leftPage.Header.PageType == OdsPageType.Leaf)
        {
            var leftEntries = ReadLeafEntries(leftPage);
            if (leftEntries.Count <= 1)
                return false;

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
            if (leftEntries.ChildPageIds.Count <= 2)
                return false;

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
        if (rightIndex >= parentEntries.ChildPageIds.Count)
            return false;

        using var rightPage = _pager.ReadPage(parentEntries.ChildPageIds[rightIndex]);

        if (nodePage.Header.PageType == OdsPageType.Leaf && rightPage.Header.PageType == OdsPageType.Leaf)
        {
            var rightEntries = ReadLeafEntries(rightPage);
            if (rightEntries.Count <= 1)
                return false;

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
            if (rightEntries.ChildPageIds.Count <= 2)
                return false;

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
        if (isRoot)
            return false;

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
        if (childIndex <= 0)
            return false;

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

    private void MergeChildrenIntoLeft(InternalEntries parentEntries, int separatorIndex, OdsPage leftPage, OdsPage rightPage)
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

    private void MergeRightIntoLeft(InternalEntries parentEntries, int separatorIndex, OdsPage leftPage, OdsPage rightPage)
    {
        MergeChildrenIntoLeft(parentEntries, separatorIndex, leftPage, rightPage);
    }

    private void NormalizeRoot()
    {
        var metadata = _pager.ReadRootMetadata();
        using var rootPage = _pager.ReadPage(metadata.RootPageId);
        if (rootPage.Header.PageType != OdsPageType.Internal)
            return;

        var rootEntries = ReadInternalEntries(rootPage);
        if (rootEntries.ChildPageIds.Count != 1)
            return;

        _pager.WriteRootMetadata(new OdsRootMetadata(rootEntries.ChildPageIds[0], metadata.LastAllocatedPageId));
    }

    private static List<LeafEntry> ReadLeafEntries(OdsPage page)
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

            if (keyLength <= 0 || valueLength < 0)
                throw new InvalidOperationException("Leaf page contains invalid key/value lengths.");

            if (offset + keyLength + valueLength > body.Length)
                throw new InvalidOperationException("Leaf page payload exceeds page body size.");

            var key = body.Slice(offset, keyLength).ToArray();
            offset += keyLength;

            var value = body.Slice(offset, valueLength).ToArray();
            offset += valueLength;

            entries.Add(new LeafEntry(key, value));
        }

        return entries;
    }

    private static void WriteLeafEntries(OdsPage page, IReadOnlyList<LeafEntry> entries)
    {
        if (!TryWriteLeafEntries(page, entries))
            throw new InvalidOperationException("Leaf page is full. Node split is required.");
    }

    /// <summary>
    /// Zero-allocation range writer: writes <paramref name="entries"/>[<paramref name="start"/>..start+<paramref name="count"/>]
    /// directly into <paramref name="page"/> without creating an intermediate list.
    /// Returns <see langword="false"/> — leaving the page body zeroed — when the range does not fit.
    /// Used by <see cref="SplitLeaf"/> and <see cref="SplitLeafAsync"/> to avoid the two
    /// <see cref="List{T}.GetRange"/> allocations that the old implementation required per split.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWriteLeafEntriesRange(
        OdsPage page,
        IReadOnlyList<LeafEntry> entries,
        int start,
        int count)
    {
        var body   = page.Body;
        var end    = start + count;
        var offset = 0;

        for (var i = start; i < end; i++)
        {
            var entry    = entries[i];
            var required = (sizeof(int) * 2) + entry.Key.Length + entry.Value.Length;
            if (offset + required > body.Length)
            {
                body.Clear(); // leave page in a clean state on failure
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

        // Zero remaining bytes so stale content from the prior page incarnation cannot be misread.
        body.Slice(offset).Clear();

        var header  = page.Header;
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
        var header = page.Header;
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
        if (separatorCount != header.ItemCount)
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
        if (requiredForCounts > body.Length)
            return false;

        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entries.ChildPageIds.Count);
        offset += sizeof(int);

        foreach (var child in entries.ChildPageIds)
        {
            if (offset + sizeof(int) > body.Length)
                return false;

            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], child);
            offset += sizeof(int);
        }

        BinaryPrimitives.WriteInt32LittleEndian(body[offset..], entries.Separators.Count);
        offset += sizeof(int);

        foreach (var separator in entries.Separators)
        {
            var required = sizeof(int) + separator.Length;
            if (offset + required > body.Length)
                return false;

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
