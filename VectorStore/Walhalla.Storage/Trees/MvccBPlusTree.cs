// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Mvcc;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Pages;
using Walhalla.Storage.Ods.Paging;

namespace Walhalla.Storage.Trees;

/// <summary>
/// MVCC-nativer B+Tree (Roadmap C.8).
/// Klassische B+Tree-Struktur mit <see cref="VersionedValue{T}"/> pro Key im Blatt,
/// doubly-linked Leaf-Chain für streamende Scans und integriertem Overflow-Store.
/// </summary>
/// <remarks>
/// Entwurfsprinzipien:
/// <list type="bullet">
///   <item>Single-Writer (embedded) — ein globales Write-Lock serialisiert Schreibtransaktionen.</item>
///   <item>Readers blockieren Writer nicht — MVCC liefert Snapshot-konsistente Lesesichten.</item>
///   <item>Overflow transparent — Werte &gt; Threshold werden out-of-line; Blatt hält 12-B-Pointer.</item>
///   <item>Per-Page-Latching — interne Knoten können während Scans shared-gelatched werden.</item>
/// </list>
/// </remarks>
public sealed class MvccBPlusTree : IDisposable
{
    private readonly OdsPager _pager;
    private readonly TransactionManager _txManager;
    private readonly IKeyComparator _keyComparator;
    private readonly int _order;
    private readonly object _writeLock = new();
    private readonly OverflowStore _overflow;
    private readonly int _overflowThreshold;

    /// <summary>Maximale Anzahl inline behaltener Versionen pro Key.
    /// Ältere Versionen werden in den OverflowStore ausgelagert.</summary>
    internal const int MaxInlineVersions = 3;

    internal MvccBPlusTree(OdsPager pager, TransactionManager txManager,
        IKeyComparator? keyComparator = null, int order = 128,
        OverflowStore? overflowStore = null, int overflowThreshold = 256)
    {
        _pager = pager ?? throw new ArgumentNullException(nameof(pager));
        _txManager = txManager ?? throw new ArgumentNullException(nameof(txManager));
        _keyComparator = keyComparator ?? BuiltInKeyComparators.Bytewise;
        _order = order;
        _overflowThreshold = overflowThreshold;
        _overflow = overflowStore ?? CreateDefaultOverflowStore(pager);
    }

    // ── Öffentliche Metadaten ────────────────────────────────────────────────

    public TransactionManager TransactionManager => _txManager;
    public IKeyComparator KeyComparator => _keyComparator;
    internal OverflowStore OverflowStore => _overflow;

    // ── Point-Operations (neueste committed Version) ────────────────────────────

    public bool TryGetLatest(ReadOnlySpan<byte> key, out byte[]? value)
    {
        using var leafPage = ReadLeafForKey(key);
        var entries = MvccLeafPageSerializer.ReadEntries(leafPage);

        foreach (var entry in entries)
        {
            if (CompareKeys(entry.Key, key) != 0)
                continue;
            var resolved = ResolveChain(entry.Versions);
            var resolvedEntry = new MvccLeafEntry(entry.Key, resolved);
            if (resolvedEntry.TryGetLatest(out var raw))
            {
                value = ResolveValue(raw);
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TryGetVisible(ReadOnlySpan<byte> key, ulong snapshotSeq, out byte[]? value)
    {
        using var leafPage = ReadLeafForKey(key);
        var entries = MvccLeafPageSerializer.ReadEntries(leafPage);

        foreach (var entry in entries)
        {
            if (CompareKeys(entry.Key, key) != 0)
                continue;
            var resolved = ResolveChain(entry.Versions);
            var resolvedEntry = new MvccLeafEntry(entry.Key, resolved);
            if (resolvedEntry.TryGetVisible(snapshotSeq, out var raw))
            {
                value = ResolveValue(raw);
                return true;
            }
        }

        value = null;
        return false;
    }

    // ── Schreibpfad (innerhalb einer Transaktion) ─────────────────────────────

    public void Upsert(ulong commitSequence, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        lock (_writeLock)
        {
            _pager.BeginWriteBatch();
            try
            {
                var path = new Stack<UpsertPathFrame>();
                var rootId = _pager.ReadRootMetadata().RootPageId;
                var currentPage = _pager.ReadPage(rootId);

                while (currentPage.Header.PageType == OdsPageType.Internal)
                {
                    var childIndex = FindChildIndex(currentPage, key);
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

                // Fast path for new keys with available leaf space: avoid deserializing the
                // entire leaf into a List<MvccLeafEntry>. Falls back to the full read/write path
                // on duplicates, full pages, or overflow values (handled by StoreValue).
                var storedValue = StoreValue(value);
                if (MvccLeafPageSerializer.TryInsertNewKeyInPlace(currentPage, key, storedValue, commitSequence))
                {
                    _pager.WritePage(currentPage);
                    currentPage.Dispose();
                    _pager.CommitWriteBatch();
                    return;
                }

                var leafEntries = MvccLeafPageSerializer.ReadEntries(currentPage);

                var replaced = false;
                for (var i = 0; i < leafEntries.Count; i++)
                {
                    if (CompareKeys(leafEntries[i].Key, key) == 0)
                    {
                        leafEntries[i] = leafEntries[i].PushVersion(commitSequence, storedValue, isTombstone: false);
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    var newEntry = new MvccLeafEntry(key.ToArray(),
                        VersionedValue<byte[]>.Push(null, commitSequence, storedValue, isTombstone: false));
                    InsertSorted(leafEntries, newEntry);
                }

                if (TryWriteOrTruncateEntries(currentPage, leafEntries))
                {
                    _pager.WritePage(currentPage);
                    currentPage.Dispose();
                    _pager.CommitWriteBatch();
                    return;
                }

                // Leaf overflow → Split
                var (promotedKey, newRightPageId) = SplitLeaf(currentPage, leafEntries);
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

                    (promotedKey, newRightPageId) = SplitInternal(parentPage, parentEntries);
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
    }

    public void Delete(ulong commitSequence, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Key must not be empty.", nameof(key));

        lock (_writeLock)
        {
            _pager.BeginWriteBatch();
            try
            {
                var path = new Stack<UpsertPathFrame>();
                var rootId = _pager.ReadRootMetadata().RootPageId;
                var currentPage = _pager.ReadPage(rootId);

                while (currentPage.Header.PageType == OdsPageType.Internal)
                {
                    var childIndex = FindChildIndex(currentPage, key);
                    path.Push(new UpsertPathFrame(currentPage.PageId, childIndex));
                    var childPageId = ReadChildPageId(currentPage, childIndex);
                    currentPage.Dispose();
                    currentPage = _pager.ReadPage(childPageId);
                }

                if (currentPage.Header.PageType != OdsPageType.Leaf)
                {
                    currentPage.Dispose();
                    throw new NotSupportedException("Unsupported page type encountered during delete traversal.");
                }

                var entries = MvccLeafPageSerializer.ReadEntries(currentPage);

                var found = false;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (CompareKeys(entries[i].Key, key) == 0)
                    {
                        entries[i] = entries[i].PushVersion(commitSequence, null, isTombstone: true);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    currentPage.Dispose();
                    _pager.CommitWriteBatch();
                    return;
                }

                if (TryWriteOrTruncateEntries(currentPage, entries))
                {
                    _pager.WritePage(currentPage);
                    currentPage.Dispose();
                    _pager.CommitWriteBatch();
                    return;
                }

                // Leaf overflow during delete → Split (same logic as Upsert)
                var (promotedKey, newRightPageId) = SplitLeaf(currentPage, entries);
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

                    (promotedKey, newRightPageId) = SplitInternal(parentPage, parentEntries);
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
    }

    // ── Commit / Rollback ───────────────────────────────────────────────────

    public void OnCommitted(ulong txId)
    {
        // No-op. Wird später für Checkpoint-Trigger genutzt.
    }

    public void Rollback(ulong txId)
    {
        // Nothing persistent to undo.
    }

    // ── Snapshot-Scans (Leaf-Chain) ──────────────────────────────────────────

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanVisible(
        ulong snapshotSeq,
        byte[]? fromInclusive = null,
        byte[]? toExclusive = null)
    {
        var startLeafId = FindStartLeaf(fromInclusive);
        var currentPageId = startLeafId;

        while (currentPageId >= 0)
        {
            using var page = _pager.ReadPage(currentPageId);
            var buffer = page.Buffer;
            var bodyStart = OdsPageHeader.SizeInBytes;
            var bodyLength = buffer.Length - bodyStart - OdsPage.ChecksumSizeInBytes;
            var header = page.Header;
            var offset = 0;

            for (var i = 0; i < header.ItemCount; i++)
            {
                var entryStart = offset;

                if (offset + sizeof(int) > bodyLength)
                    throw new InvalidOperationException("MVCC leaf page payload is corrupted (key length missing).");

                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(bodyStart + offset));
                offset += sizeof(int);

                if (keyLength <= 0 || offset + keyLength > bodyLength)
                    throw new InvalidOperationException("MVCC leaf page contains invalid key length.");

                var keyStart = bodyStart + offset;
                offset += keyLength;

                if (offset + sizeof(int) > bodyLength)
                    throw new InvalidOperationException("MVCC leaf page payload is corrupted (version count missing).");

                var versionCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(bodyStart + offset));
                offset += sizeof(int);

                if (versionCount < 0)
                    throw new InvalidOperationException("MVCC leaf page contains invalid version count.");

                // Bereichsprüfung vor dem teuren Deserialisieren.
                if (fromInclusive != null && CompareKeys(new ReadOnlySpan<byte>(buffer, keyStart, keyLength), fromInclusive) < 0)
                {
                    offset = MvccLeafPageSerializer.SkipEntryVersions(buffer.AsSpan(bodyStart, bodyLength), offset, versionCount);
                    continue;
                }
                if (toExclusive != null && CompareKeys(new ReadOnlySpan<byte>(buffer, keyStart, keyLength), toExclusive) >= 0)
                    yield break;

                // Fast path: genau eine Version — der häufigste Fall in read-heavy Workloads.
                if (versionCount == 1)
                {
                    if (offset + sizeof(ulong) + 1 + sizeof(int) > bodyLength)
                        throw new InvalidOperationException("MVCC leaf page payload is corrupted (version header missing).");

                    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(bodyStart + offset));
                    offset += sizeof(ulong);

                    var flags = buffer[bodyStart + offset++];
                    var isTombstone = (flags & 0x01) != 0;
                    var isOverflow = (flags & 0x02) != 0;
                    var isOverflowChain = (flags & 0x04) != 0;

                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(bodyStart + offset));
                    offset += sizeof(int);

                    if (!isTombstone && sequence <= snapshotSeq && !isOverflowChain)
                    {
                        byte[] value;
                        if (isOverflow || valueLength == -1)
                        {
                            const int OverflowPointerSize = sizeof(long) + sizeof(int) + sizeof(uint);
                            if (offset + OverflowPointerSize > bodyLength)
                                throw new InvalidOperationException("MVCC leaf page overflow pointer truncated.");

                            var ptrSpan = new ReadOnlySpan<byte>(buffer, bodyStart + offset, OverflowPointerSize);
                            value = OverflowPointer.TryDecode(ptrSpan, out var ptr)
                                ? _overflow.ReadBlob(ptr)
                                : ptrSpan.ToArray();
                            offset += OverflowPointerSize;
                        }
                        else
                        {
                            if (valueLength < 0)
                                throw new InvalidOperationException("MVCC leaf page contains negative inline value length.");
                            if (offset + valueLength > bodyLength)
                                throw new InvalidOperationException("MVCC leaf page inline value truncated.");

                            value = valueLength == 0 ? Array.Empty<byte>() : new ReadOnlySpan<byte>(buffer, bodyStart + offset, valueLength).ToArray();
                            offset += valueLength;
                        }

                        var key = new ReadOnlySpan<byte>(buffer, keyStart, keyLength).ToArray();
                        yield return new KeyValuePair<byte[], byte[]>(key, value);
                        continue;
                    }

                    offset = entryStart; // Fallback: komplexen Pfad von vorne parsen.
                }
                else
                {
                    offset = entryStart; // Mehrere Versionen: komplexen Pfad nehmen.
                }

                // Komplexer Pfad: volle Deserialisierung nur für diesen Eintrag.
                var entry = MvccLeafPageSerializer.ReadEntryAt(buffer.AsSpan(bodyStart, bodyLength), offset, out offset);
                var resolved = ResolveChain(entry.Versions);
                var resolvedEntry = new MvccLeafEntry(entry.Key, resolved);
                if (resolvedEntry.TryGetVisible(snapshotSeq, out var raw))
                {
                    var value = ResolveValue(raw);
                    yield return new KeyValuePair<byte[], byte[]>(entry.Key, value);
                }
            }

            currentPageId = page.Header.RightSiblingPageId;
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> ScanPrefixVisible(
        ulong snapshotSeq, byte[] prefix)
    {
        if (prefix == null || prefix.Length == 0)
            throw new ArgumentException("Prefix must not be empty.", nameof(prefix));

        var toExclusive = IncrementByteArray(prefix);
        foreach (var kv in ScanVisible(snapshotSeq, prefix, toExclusive))
            yield return kv;
    }

    // ── Bulk-Pfad ─────────────────────────────────────────────────────────────

    public void BulkUpsert(ulong commitSequence,
        IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        if (entries == null)
            throw new ArgumentNullException(nameof(entries));
        if (entries.Count == 0) return;

        lock (_writeLock)
        {
            _pager.BeginWriteBatch();
            try
            {
                // Materialize and sort once. The tree order matches bytewise key order,
                // so grouping by leaf page is trivial after sorting.
                var sorted = entries.Count <= 256
                    ? GC.AllocateUninitializedArray<KeyValuePair<byte[], byte[]>>(entries.Count)
                    : new KeyValuePair<byte[], byte[]>[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                    sorted[i] = entries[i];
                Array.Sort(sorted, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

                BulkUpsertSorted(commitSequence, sorted);
                _pager.CommitWriteBatch();
            }
            catch
            {
                _pager.AbortWriteBatch();
                throw;
            }
        }
    }

    /// <summary>
    /// Bulk-Pfad: sortierte Einträge werden pro Leaf-Page gebündelt. Das vermeidet
    /// das mehrfache Deserialisieren/Schreiben derselben Seite und reduziert den
    /// managed-Heap-Overhead pro Eintrag deutlich.
    /// </summary>
    private void BulkUpsertSorted(ulong commitSequence,
        KeyValuePair<byte[], byte[]>[] sorted)
    {
        // Track the current leaf page we are filling.
        OdsPage? leafPage = null;
        List<MvccLeafEntry>? leafEntries = null;
        Stack<UpsertPathFrame>? path = null;
        int leafPageId = -1;

        for (int i = 0; i < sorted.Length; i++)
        {
            var kv = sorted[i];
            var key = kv.Key.AsSpan();
            var value = kv.Value;
            if (key.IsEmpty)
                throw new ArgumentException("Key must not be empty.");

            // Find the leaf page for this key, reusing the current leaf if possible.
            if (leafPage == null || !BelongsToLeaf(key, leafPage))
            {
                // Flush previous leaf if any.
                if (leafPage != null)
                {
                    FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
                    leafPage = null;
                    leafEntries = null;
                    leafPageId = -1;
                }

                path = new Stack<UpsertPathFrame>();
                var rootId = _pager.ReadRootMetadata().RootPageId;
                var page = _pager.ReadPage(rootId);
                while (page.Header.PageType == OdsPageType.Internal)
                {
                    var childIndex = FindChildIndex(page, key);
                    path.Push(new UpsertPathFrame(page.PageId, childIndex));
                    var childPageId = ReadChildPageId(page, childIndex);
                    page.Dispose();
                    page = _pager.ReadPage(childPageId);
                }

                if (page.Header.PageType != OdsPageType.Leaf)
                {
                    page.Dispose();
                    throw new NotSupportedException("Unsupported page type encountered during bulk insert traversal.");
                }

                leafPage = page;
                leafPageId = page.PageId;
                leafEntries = MvccLeafPageSerializer.ReadEntries(leafPage);
            }

            // Insert or replace into the current leaf entry list.
            var storedValue = StoreValue(value);
            var newEntry = new MvccLeafEntry(kv.Key,
                VersionedValue<byte[]>.Push(null, commitSequence, storedValue, isTombstone: false));

            bool replaced = false;
            for (int e = 0; e < leafEntries!.Count; e++)
            {
                if (CompareKeys(leafEntries[e].Key, key) == 0)
                {
                    leafEntries[e] = leafEntries[e].PushVersion(commitSequence, storedValue, isTombstone: false);
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
                InsertSorted(leafEntries, newEntry);

            // If the leaf cannot hold the entries, flush it now. The next key may
            // land in the newly split right page, which BelongsToLeaf will detect.
            if (!CanEntriesFit(leafPage, leafEntries))
            {
                FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
                leafPage = null;
                leafEntries = null;
                leafPageId = -1;
            }
        }

        if (leafPage != null)
            FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
    }

    /// <summary>
    /// Schreibt eine im Bulk-Pfad bearbeitete Leaf-Page zurück. Bei Überlauf wird
    /// die Seite gesplittet und die parents entsprechend aktualisiert.
    /// </summary>
    private void FlushBulkLeaf(ref OdsPage? leafPage, ref List<MvccLeafEntry>? leafEntries,
        ref Stack<UpsertPathFrame>? path)
    {
        if (leafPage == null || leafEntries == null)
            return;

        using var currentPage = leafPage;
        var entries = leafEntries;
        leafPage = null;
        leafEntries = null;

        if (TryWriteOrTruncateEntries(currentPage, entries))
        {
            _pager.WritePage(currentPage);
            return;
        }

        // Leaf overflow → split and propagate.
        var rootId = _pager.ReadRootMetadata().RootPageId;
        var (promotedKey, newRightPageId) = SplitLeaf(currentPage, entries);

        while (path!.Count > 0)
        {
            var frame = path.Pop();
            using var parentPage = _pager.ReadPage(frame.PageId);
            var parentEntries = ReadInternalEntries(parentPage);
            parentEntries.Separators.Insert(frame.ChildIndex, promotedKey);
            parentEntries.ChildPageIds.Insert(frame.ChildIndex + 1, newRightPageId);

            if (TryWriteInternalEntries(parentPage, parentEntries))
            {
                _pager.WritePage(parentPage);
                return;
            }

            (promotedKey, newRightPageId) = SplitInternal(parentPage, parentEntries);
        }

        PromoteNewRoot(rootId, promotedKey, newRightPageId);
    }

    /// <summary>
    /// Prüft, ob der Schlüssel in die gegebene Leaf-Page gehört (linke Grenze).
    /// Rechte Grenze wird durch die rechte Geschwister-Page abgedeckt.
    /// </summary>
    private bool BelongsToLeaf(ReadOnlySpan<byte> key, OdsPage leafPage)
    {
        var body = leafPage.Body;
        var count = leafPage.Header.ItemCount;
        if (count == 0) return true;

        var offset = 0;
        // first key
        var firstKeyLen = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);
        var firstKey = body.Slice(offset, firstKeyLen);
        if (_keyComparator.Compare(key, firstKey) < 0)
            return false;

        return true;
    }

    /// <summary>
    /// Variant for delete bulk path: the key must be within the actual key range
    /// stored on this leaf (first..last). Otherwise it belongs to a right sibling.
    /// Uses ReadEntries to correctly account for the MVCC leaf entry layout.
    /// </summary>
    private bool BelongsToLeafStrict(ReadOnlySpan<byte> key, OdsPage leafPage)
    {
        var count = leafPage.Header.ItemCount;
        if (count == 0) return true;

        var entries = MvccLeafPageSerializer.ReadEntries(leafPage);
        if (entries.Count == 0) return true;

        if (_keyComparator.Compare(key, entries[0].Key) < 0)
            return false;
        if (_keyComparator.Compare(key, entries[entries.Count - 1].Key) > 0)
            return false;

        return true;
    }

    /// <summary>
    /// Schneller Platz-Check ohne vollständige Serialisierung.
    /// </summary>
    private static bool CanEntriesFit(OdsPage page, List<MvccLeafEntry> entries)
    {
        int total = 0;
        foreach (var e in entries)
            total += MvccLeafPageSerializer.ComputeEntrySize(e);
        // Leave a small headroom so a following insert of a slightly larger key still fits.
        return total <= page.Body.Length - 64;
    }

    public void BulkDelete(ulong commitSequence, IReadOnlyList<byte[]> keys)
    {
        if (keys == null)
            throw new ArgumentNullException(nameof(keys));
        if (keys.Count == 0) return;

        lock (_writeLock)
        {
            _pager.BeginWriteBatch();
            try
            {
                var sorted = keys.Count <= 256
                    ? GC.AllocateUninitializedArray<byte[]>(keys.Count)
                    : new byte[keys.Count][];
                for (int i = 0; i < keys.Count; i++)
                    sorted[i] = keys[i];
                Array.Sort(sorted, (a, b) => a.AsSpan().SequenceCompareTo(b));

                BulkDeleteSorted(commitSequence, sorted);
                _pager.CommitWriteBatch();
            }
            catch
            {
                _pager.AbortWriteBatch();
                throw;
            }
        }
    }

    /// <summary>
    /// Bulk-Delete: sortierte Keys werden pro Leaf-Page gebündelt. Die Seite wird
    /// nur einmal gelesen, alle Tombstone-Versionen werden eingefügt und dann
    /// einmal zurückgeschrieben. Das vermeidet den per-Key Root-to-Leaf-Traversal
    /// und die wiederholte Deserialisierung derselben Seite.
    /// </summary>
    private void BulkDeleteSorted(ulong commitSequence, byte[][] sortedKeys)
    {
        OdsPage? leafPage = null;
        List<MvccLeafEntry>? leafEntries = null;
        Stack<UpsertPathFrame>? path = null;

        for (int i = 0; i < sortedKeys.Length; i++)
        {
            var key = sortedKeys[i].AsSpan();
            if (key.IsEmpty)
                throw new ArgumentException("Key must not be empty.");

            if (leafPage == null || !BelongsToLeafStrict(key, leafPage))
            {
                if (leafPage != null)
                {
                    FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
                    leafPage = null;
                    leafEntries = null;
                }

                path = new Stack<UpsertPathFrame>();
                var rootId = _pager.ReadRootMetadata().RootPageId;
                var page = _pager.ReadPage(rootId);
                while (page.Header.PageType == OdsPageType.Internal)
                {
                    var childIndex = FindChildIndex(page, key);
                    path.Push(new UpsertPathFrame(page.PageId, childIndex));
                    var childPageId = ReadChildPageId(page, childIndex);
                    page.Dispose();
                    page = _pager.ReadPage(childPageId);
                }

                if (page.Header.PageType != OdsPageType.Leaf)
                {
                    page.Dispose();
                    throw new NotSupportedException("Unsupported page type encountered during bulk delete traversal.");
                }

                leafPage = page;
                leafEntries = MvccLeafPageSerializer.ReadEntries(leafPage);
            }

            bool found = false;
            for (int e = 0; e < leafEntries!.Count; e++)
            {
                if (CompareKeys(leafEntries[e].Key, key) == 0)
                {
                    leafEntries[e] = leafEntries[e].PushVersion(commitSequence, null, isTombstone: true);
                    found = true;
                    break;
                }
            }
            // Missing keys are silently ignored, matching single Delete behavior.

            // Tombstones add version bytes; flush before the page overflows.
            if (found && !CanEntriesFit(leafPage, leafEntries))
            {
                FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
                leafPage = null;
                leafEntries = null;
            }
        }

        if (leafPage != null)
            FlushBulkLeaf(ref leafPage, ref leafEntries, ref path);
    }

    // ── Wartung ───────────────────────────────────────────────────────────────

    public void Checkpoint()
    {
        _pager.Flush();
    }

    /// <summary>
    /// Traversiert alle Leaf-Pages und gibt die höchste Sequence-Nummer zurück.
    /// Wird nach Recovery verwendet, um den TransactionManager zu synchronisieren.
    /// </summary>
    public ulong GetMaxSequence()
    {
        ulong maxSeq = 0;
        var currentPageId = FindLeftmostLeaf();

        while (currentPageId >= 0)
        {
            using var page = _pager.ReadPage(currentPageId);
            var entries = MvccLeafPageSerializer.ReadEntries(page);

            foreach (var entry in entries)
            {
                var resolved = ResolveChain(entry.Versions);
                var current = resolved;
                while (current != null)
                {
                    if (current.Sequence > maxSeq)
                        maxSeq = current.Sequence;
                    current = current.Older;
                }
            }

            currentPageId = page.Header.RightSiblingPageId;
        }

        return maxSeq;
    }

    public void Vacuum()
    {
        lock (_writeLock)
        {
            _pager.BeginWriteBatch();
            try
            {
                var oldestSnapshot = _txManager.OldestActiveSnapshot;
                // When no snapshots are active OldestActiveSnapshot == CurrentSequence.
                // By bumping the cutoff one past the latest sequence we allow Prune
                // to discard every version except the absolute newest one.
                var pruneCutoff = oldestSnapshot == _txManager.CurrentSequence
                    ? oldestSnapshot + 1
                    : oldestSnapshot;

                var currentPageId = FindLeftmostLeaf();

                while (currentPageId >= 0)
                {
                    using var page = _pager.ReadPage(currentPageId);
                    var entries = MvccLeafPageSerializer.ReadEntries(page);
                    var modified = false;

                    for (var i = entries.Count - 1; i >= 0; i--)
                    {
                        // Overflow-Chain auflösen, damit Prune die vollständige Kette sieht
                        var fullChain = ResolveChain(entries[i].Versions);
                        var fullEntry = new MvccLeafEntry(entries[i].Key, fullChain);

                        // Overflow-Pointer merken, falls vorhanden (für Cleanup)
                        OverflowPointer? oldOverflowPtr = null;
                        var scanChain = entries[i].Versions;
                        while (scanChain != null)
                        {
                            if (scanChain.IsOverflowChain &&
                                scanChain.Value != null &&
                                OverflowPointer.TryDecode(scanChain.Value, out var oldPtr))
                            {
                                oldOverflowPtr = oldPtr;
                                break;
                            }
                            scanChain = scanChain.Older;
                        }

                        var prunedCount = fullEntry.PruneVersions(pruneCutoff, onPruned: raw =>
                        {
                            if (raw != null && OverflowPointer.TryDecode(raw, out var ptr))
                                _overflow.FreeBlob(ptr);
                        });
                        var prunedChain = fullEntry.Versions;

                        bool removable = prunedChain == null ||
                                         (prunedChain.Older == null && prunedChain.IsTombstone);

                        if (removable)
                        {
                            // Overflow-Blob freigeben, falls vorhanden
                            if (oldOverflowPtr != null)
                                _overflow.FreeBlob(oldOverflowPtr.Value);
                            entries.RemoveAt(i);
                            modified = true;
                        }
                        else
                        {
                            // Prüfen, ob die geprunte Chain noch einen Overflow-Marker braucht
                            var prunedEntry = new MvccLeafEntry(entries[i].Key, prunedChain);
                            var prunedSize = MvccLeafPageSerializer.ComputeEntrySize(prunedEntry);

                            // MaxEntrySize basierend auf Page-Größe
                            var maxEntrySize = page.Body.Length / 2;

                            if (prunedSize > maxEntrySize)
                            {
                                // Chain ist immer noch zu groß → neu offloaden
                                var oldPtrToFree = oldOverflowPtr;
                                entries[i] = OffloadVersionChain(prunedEntry);
                                if (oldPtrToFree != null)
                                    _overflow.FreeBlob(oldPtrToFree.Value);
                                modified = true;
                            }
                            else if (oldOverflowPtr != null)
                            {
                                // Chain ist jetzt klein genug → Overflow-Marker entfernen
                                _overflow.FreeBlob(oldOverflowPtr.Value);
                                entries[i] = prunedEntry;
                                modified = true;
                            }
                            else if (prunedCount > 0)
                            {
                                // Kein Overflow, aber Versionen wurden geprunt
                                entries[i] = prunedEntry;
                                modified = true;
                            }
                        }
                    }

                    if (modified)
                    {
                        if (entries.Count > 0)
                        {
                            if (!TryWriteOrTruncateEntries(page, entries))
                            {
                                // Should not happen: we only removed data.
                                throw new InvalidOperationException(
                                    "Vacuum failed: pruned entries do not fit back into page.");
                            }
                        }
                        else
                        {
                            // Page became empty. Write a clean empty leaf header.
                            page.Body.Clear();
                            page.Header = page.Header with
                            {
                                ItemCount = 0,
                                PageType = OdsPageType.Leaf
                            };
                        }
                        _pager.WritePage(page);
                    }

                    currentPageId = page.Header.RightSiblingPageId;
                }

                _pager.CommitWriteBatch();
            }
            catch
            {
                _pager.AbortWriteBatch();
                throw;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Split / Merge / Borrow
    // ═══════════════════════════════════════════════════════════════════════════

    private (byte[] PromotedKey, int NewRightPageId) SplitLeaf(OdsPage page, List<MvccLeafEntry> entries)
    {
        var splitIndex = FindByteSplitPoint(entries);
        var rightCount = entries.Count - splitIndex;

        using var newRight = _pager.AllocatePage(OdsPageType.Leaf, -1);

        // Sibling-link maintenance
        var originalRightSiblingId = page.Header.RightSiblingPageId;
        newRight.Header = newRight.Header with { RightSiblingPageId = originalRightSiblingId };
        page.Header = page.Header with { RightSiblingPageId = newRight.PageId };

        if (!MvccLeafPageSerializer.TryWriteEntriesRange(page, entries, 0, splitIndex))
            throw new InvalidOperationException("Leaf split failed: left half does not fit.");
        _pager.WritePage(page);

        if (!MvccLeafPageSerializer.TryWriteEntriesRange(newRight, entries, splitIndex, rightCount))
            throw new InvalidOperationException("Leaf split failed: right half does not fit.");
        _pager.WritePage(newRight);

        return (entries[splitIndex].Key, newRight.PageId);
    }

    private (byte[] PromotedKey, int NewRightPageId) SplitInternal(OdsPage page, InternalEntries entries)
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
        _pager.WritePage(page);

        using var rightPage = _pager.AllocatePage(OdsPageType.Internal, -1);
        var right = new InternalEntries(rightChildren, rightSeparators);
        if (!TryWriteInternalEntries(rightPage, right))
            throw new InvalidOperationException("Unable to write right internal split page.");
        _pager.WritePage(rightPage);

        return (promotedKey, rightPage.PageId);
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

    // ═══════════════════════════════════════════════════════════════════════════
    //  Version-Chain Overflow Management
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Versucht, Einträge in eine Page zu schreiben. Wenn sie nicht passen,
    /// werden überlange Version-Chains gekürzt und in den OverflowStore ausgelagert.
    /// </summary>
    private bool TryWriteOrTruncateEntries(OdsPage page, List<MvccLeafEntry> entries)
    {
        if (MvccLeafPageSerializer.TryWriteEntries(page, entries))
            return true;

        // Ein oder mehrere Einträge sind zu groß → Version-Chains kürzen
        var maxEntrySize = page.Body.Length / 2;
        for (int i = 0; i < entries.Count; i++)
        {
            var size = MvccLeafPageSerializer.ComputeEntrySize(entries[i]);
            if (size > maxEntrySize)
                entries[i] = OffloadVersionChain(entries[i]);
        }

        return MvccLeafPageSerializer.TryWriteEntries(page, entries);
    }

    /// <summary>
    /// Lagert alte Versionen einer Chain in den OverflowStore aus.
    /// Behält die neuesten <see cref="MaxInlineVersions"/> Versionen inline.
    /// </summary>
    private MvccLeafEntry OffloadVersionChain(MvccLeafEntry entry)
    {
        var chain = entry.Versions;
        if (chain == null) return entry;

        // Zähle Versionen
        int count = 0;
        var current = chain;
        while (current != null) { count++; current = current.Older; }

        if (count <= MaxInlineVersions) return entry;

        // Chain aufteilen: neueste MaxInlineVersions bleiben inline,
        // der Rest wird serialisiert und in den OverflowStore geschrieben
        var inlineHead = chain;
        var splitPoint = inlineHead;
        for (int i = 1; i < MaxInlineVersions; i++)
            splitPoint = splitPoint!.Older;

        var overflowTail = splitPoint!.Older;
        splitPoint.Older = null; // inline-Kette abschneiden

        // Overflow-Tail serialisieren (oldest-first für einfaches Deserialisieren)
        var serialized = SerializeVersionChain(overflowTail);
        var ptr = _overflow.WriteBlob(serialized);

        // Marker-Node erstellen und an inline-Kette anhängen
        var marker = new VersionedValue<byte[]>
        {
            Sequence = 0, // irrelevant für Marker
            IsTombstone = false,
            IsOverflowChain = true,
            Value = ptr.Encode(),
            Older = null
        };
        splitPoint.Older = marker;

        return new MvccLeafEntry(entry.Key, inlineHead);
    }

    /// <summary>
    /// Serialisiert eine Version-Chain in ein Byte-Array (oldest-first).
    /// Verwendet das gleiche Binärformat wie die Leaf-Page.
    /// </summary>
    private static byte[] SerializeVersionChain(VersionedValue<byte[]>? chain)
    {
        if (chain == null) return Array.Empty<byte>();

        // Zähle Versionen und berechne Größe
        int count = 0;
        int totalSize = sizeof(int); // VersionCount
        var current = chain;
        while (current != null)
        {
            count++;
            totalSize += sizeof(ulong) + 1 + sizeof(int); // Sequence + Flags + ValueLength
            if (current.Value != null)
            {
                if (current.IsOverflowChain ||
                    (current.Value.Length == OverflowPointer.SizeInBytes &&
                     OverflowPointer.TryDecode(current.Value, out _)))
                {
                    totalSize += OverflowPointer.SizeInBytes;
                }
                else
                {
                    totalSize += current.Value.Length;
                }
            }
            current = current.Older;
        }

        var buf = new byte[totalSize];
        var offset = 0;

        // VersionCount schreiben
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), count);
        offset += sizeof(int);

        // Versionen in umgekehrter Reihenfolge sammeln (oldest-first)
        // und dann vorwärts schreiben
        var versions = new VersionedValue<byte[]>[count];
        current = chain;
        for (int i = 0; i < count; i++)
        {
            versions[i] = current!;
            current = current!.Older;
        }

        // Oldest-first schreiben
        for (int i = count - 1; i >= 0; i--)
        {
            var v = versions[i];
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(offset), v.Sequence);
            offset += sizeof(ulong);

            byte flags = 0;
            if (v.IsTombstone) flags |= 0x01;
            if (v.IsOverflowChain) flags |= 0x04;
            bool isOverflow = v.Value != null &&
                              v.Value.Length == OverflowPointer.SizeInBytes &&
                              OverflowPointer.TryDecode(v.Value, out _);
            if (isOverflow) flags |= 0x02;
            buf[offset++] = flags;

            if (v.Value != null)
            {
                if (isOverflow || v.IsOverflowChain)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), -1);
                    offset += sizeof(int);
                    v.Value.CopyTo(buf.AsSpan(offset));
                    offset += v.Value.Length;
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), v.Value.Length);
                    offset += sizeof(int);
                    v.Value.CopyTo(buf.AsSpan(offset));
                    offset += v.Value.Length;
                }
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), 0);
                offset += sizeof(int);
            }
        }

        return buf;
    }

    /// <summary>
    /// Deserialisiert eine Version-Chain aus einem Byte-Array (oldest-first).
    /// </summary>
    private static VersionedValue<byte[]>? DeserializeVersionChain(byte[] data)
    {
        if (data == null || data.Length == 0) return null;

        var offset = 0;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += sizeof(int);

        if (count <= 0) return null;

        // Oldest-first lesen → per Push rückwärts aufbauen
        var versionBuffer = new (ulong Seq, byte[]? Value, bool IsTombstone, bool IsOverflowChain)[count];
        for (int v = 0; v < count; v++)
        {
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            offset += sizeof(ulong);

            var flags = data[offset++];
            var isTombstone = (flags & 0x01) != 0;
            var isOverflow = (flags & 0x02) != 0;
            var isOverflowChain = (flags & 0x04) != 0;

            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += sizeof(int);

            byte[]? value;
            if (isOverflow || isOverflowChain)
            {
                const int OverflowPointerSize = sizeof(long) + sizeof(int) + sizeof(uint);
                value = data.AsSpan(offset, OverflowPointerSize).ToArray();
                offset += OverflowPointerSize;
            }
            else
            {
                value = valueLength == 0 ? Array.Empty<byte>() : data.AsSpan(offset, valueLength).ToArray();
                offset += valueLength;
            }

            versionBuffer[v] = (sequence, value, isTombstone, isOverflowChain);
        }

        // Blob ist oldest-first geschrieben. Push in Vorwärtsrichtung,
        // sodass ältere Versionen zuerst gepusht werden und neuere
        // als Head obenauf kommen → Resultat ist newest-first.
        VersionedValue<byte[]>? result = null;
        for (int vi = 0; vi < versionBuffer.Length; vi++)
        {
            var (seq, val, tombstone, isOverflowChain) = versionBuffer[vi];
            result = VersionedValue<byte[]>.Push(result, seq, val, tombstone);
            if (isOverflowChain && result != null)
                result.IsOverflowChain = true;
        }

        return result;
    }

    /// <summary>
    /// Löst eine Version-Chain vollständig auf, indem OverflowChain-Marker
    /// durch die tatsächlichen Versionen aus dem OverflowStore ersetzt werden.
    /// </summary>
    private VersionedValue<byte[]>? ResolveChain(VersionedValue<byte[]>? chain)
    {
        if (chain == null) return null;

        // Suche nach einem IsOverflowChain-Marker in der Kette
        var current = chain;
        VersionedValue<byte[]>? parent = null;
        while (current != null)
        {
            if (current.IsOverflowChain)
            {
                // OverflowPointer aus dem Value decodieren
                if (current.Value != null && OverflowPointer.TryDecode(current.Value, out var ptr))
                {
                    var blob = _overflow.ReadBlob(ptr);
                    var overflowChain = DeserializeVersionChain(blob);

                    if (parent != null)
                        parent.Older = overflowChain;
                    else
                        chain = overflowChain;

                    // Die deserialisierte Chain kann selbst wieder OverflowChain-Marker
                    // enthalten → rekursiv auflösen
                    if (overflowChain != null)
                        _ = ResolveChain(overflowChain);
                }
                break;
            }
            parent = current;
            current = current.Older;
        }

        return chain;
    }

    /// <summary>
    /// Findet einen Split-Punkt basierend auf Byte-Größen der Einträge,
    /// sodass beide Hälften möglichst in eine Page passen.
    /// </summary>
    private static int FindByteSplitPoint(List<MvccLeafEntry> entries)
    {
        int totalSize = 0;
        foreach (var e in entries)
            totalSize += MvccLeafPageSerializer.ComputeEntrySize(e);

        int halfSize = totalSize / 2;
        int running = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            running += MvccLeafPageSerializer.ComputeEntrySize(entries[i]);
            if (running >= halfSize)
                return Math.Max(i + 1, 1); // mindestens 1 Eintrag links
        }

        return entries.Count / 2; // Fallback
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Interne Traversal-Hilfsmethoden
    // ═══════════════════════════════════════════════════════════════════════════

    private OdsPage ReadLeafForKey(ReadOnlySpan<byte> key)
    {
        var page = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);

        while (page.Header.PageType == OdsPageType.Internal)
        {
            var childIndex = FindChildIndex(page, key);
            var childPageId = ReadChildPageId(page, childIndex);
            page.Dispose();
            page = _pager.ReadPage(childPageId);
        }

        if (page.Header.PageType != OdsPageType.Leaf)
        {
            page.Dispose();
            throw new NotSupportedException("Unsupported page type encountered during traversal.");
        }

        return page;
    }

    private int FindStartLeaf(byte[]? fromInclusive)
    {
        if (fromInclusive == null)
            return FindLeftmostLeaf();

        var page = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);
        try
        {
            while (page.Header.PageType == OdsPageType.Internal)
            {
                var internalEntries = ReadInternalEntries(page);
                var childIndex = FindChildIndexInternal(internalEntries.Separators, fromInclusive, _keyComparator);
                var childPageId = internalEntries.ChildPageIds[childIndex];
                page.Dispose();
                page = _pager.ReadPage(childPageId);
            }

            return page.PageId;
        }
        finally
        {
            page.Dispose();
        }
    }

    private int FindLeftmostLeaf()
    {
        var page = _pager.ReadPage(_pager.ReadRootMetadata().RootPageId);
        try
        {
            while (page.Header.PageType == OdsPageType.Internal)
            {
                var internalEntries = ReadInternalEntries(page);
                var childPageId = internalEntries.ChildPageIds[0];
                page.Dispose();
                page = _pager.ReadPage(childPageId);
            }

            return page.PageId;
        }
        finally
        {
            page.Dispose();
        }
    }

    // ── Interne Knoten-Hilfsmethoden ──────────────────────────────────────────

    private int FindChildIndex(OdsPage page, ReadOnlySpan<byte> key)
    {
        var entries = ReadInternalEntries(page);
        return FindChildIndexInternal(entries.Separators, key, _keyComparator);
    }

    private static int FindChildIndexInternal(List<byte[]> separators, ReadOnlySpan<byte> key,
        IKeyComparator comparator)
    {
        for (var i = 0; i < separators.Count; i++)
        {
            if (comparator.Compare(key, separators[i]) < 0)
                return i;
        }
        return separators.Count;
    }

    private static int ReadChildPageId(OdsPage page, int childIndex)
    {
        var entries = ReadInternalEntries(page);
        return entries.ChildPageIds[childIndex];
    }

    private static InternalEntries ReadInternalEntries(OdsPage page)
    {
        var body = page.Body;
        var count = page.Header.ItemCount;
        var separators = new List<byte[]>(count);
        var childPageIds = new List<int>(count + 1);

        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var sepLen = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);
            separators.Add(body.Slice(offset, sepLen).ToArray());
            offset += sepLen;
        }

        for (var i = 0; i <= count; i++)
        {
            childPageIds.Add(BinaryPrimitives.ReadInt32LittleEndian(body[offset..]));
            offset += sizeof(int);
        }

        return new InternalEntries(childPageIds, separators);
    }

    private static bool TryWriteInternalEntries(OdsPage page, InternalEntries entries)
    {
        var body = page.Body;
        body.Clear();

        var offset = 0;
        foreach (var sep in entries.Separators)
        {
            var required = sizeof(int) + sep.Length;
            if (offset + required > body.Length)
                return false;

            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], sep.Length);
            offset += sizeof(int);
            sep.CopyTo(body[offset..]);
            offset += sep.Length;
        }

        foreach (var childId in entries.ChildPageIds)
        {
            if (offset + sizeof(int) > body.Length)
                return false;

            BinaryPrimitives.WriteInt32LittleEndian(body[offset..], childId);
            offset += sizeof(int);
        }

        page.Header = page.Header with
        {
            PageType = OdsPageType.Internal,
            ItemCount = entries.Separators.Count
        };
        return true;
    }

    // ── Key-Vergleich ─────────────────────────────────────────────────────────

    private int CompareKeys(byte[] a, ReadOnlySpan<byte> b)
    {
        return _keyComparator.Compare(a, b);
    }

    private int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return _keyComparator.Compare(a, b);
    }

    private static void InsertSorted(List<MvccLeafEntry> entries, MvccLeafEntry newEntry)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (entries[mid].Key.AsSpan().SequenceCompareTo(newEntry.Key) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        entries.Insert(lo, newEntry);
    }

    private static byte[] IncrementByteArray(byte[] prefix)
    {
        var result = new byte[prefix.Length];
        prefix.CopyTo(result, 0);

        for (var i = result.Length - 1; i >= 0; i--)
        {
            if (result[i] < 0xFF)
            {
                result[i]++;
                return result;
            }
            result[i] = 0;
        }

        return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _overflow?.Dispose();
        _pager?.Dispose();
    }

    // ── Overflow-Hilfsmethoden ────────────────────────────────────────────────

    private byte[] StoreValue(ReadOnlySpan<byte> value)
    {
        if (value.Length > _overflowThreshold)
        {
            var ptr = _overflow.WriteBlob(value.ToArray());
            return ptr.Encode();
        }
        return value.ToArray();
    }

    private byte[] ResolveValue(byte[]? raw)
    {
        if (raw != null && OverflowPointer.TryDecode(raw, out var ptr))
            return _overflow.ReadBlob(ptr);
        return raw ?? Array.Empty<byte>();
    }

    private static OverflowStore CreateDefaultOverflowStore(OdsPager pager)
    {
        // Bestimme den Dateipfad aus dem ODS-Pfad
        var odsPath = pager.FilePath ?? "walhalla.ods";
        var overflowPath = Path.ChangeExtension(odsPath, ".overflow");
        return new OverflowStore(overflowPath);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Nested Types
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct UpsertPathFrame(int PageId, int ChildIndex);
    private sealed record InternalEntries(List<int> ChildPageIds, List<byte[]> Separators);
}
