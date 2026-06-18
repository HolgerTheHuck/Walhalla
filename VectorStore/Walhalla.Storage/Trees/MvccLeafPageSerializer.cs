// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Walhalla.Storage.Mvcc;
using Walhalla.Storage.Ods.Pages;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Serialisiert / deserialisiert MVCC-Leaf-Einträge auf eine <see cref="OdsPage"/>.
/// </summary>
/// <remarks>
/// On-disk Layout pro Eintrag:
/// <code>
/// [KeyLength:   int32]
/// [Key:         KeyLength bytes]
/// [VersionCount: int32]
/// For each version:
///   [Sequence:   uint64]
///   [Flags:      byte]   (bit 0 = IsTombstone)
///   [ValueLength: int32]  (&gt;= 0 = inline; -1 = overflow pointer, 16 bytes)
///   [Value:      ValueLength bytes  |  16 bytes OverflowPointer]
/// </code>
/// </remarks>
internal static class MvccLeafPageSerializer
{
    /// <summary>Liest alle Einträge aus einer Leaf-Page.</summary>
    public static List<MvccLeafEntry> ReadEntries(OdsPage page)
    {
        var header = page.Header;
        var body = page.Body;
        var entries = new List<MvccLeafEntry>(header.ItemCount);

        var offset = 0;
        for (var i = 0; i < header.ItemCount; i++)
        {
            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (key length missing).");

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            if (keyLength <= 0 || offset + keyLength > body.Length)
                throw new InvalidOperationException("MVCC leaf page contains invalid key length.");

            var key = body.Slice(offset, keyLength).ToArray();
            offset += keyLength;

            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (version count missing).");

            var versionCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            if (versionCount < 0)
                throw new InvalidOperationException("MVCC leaf page contains invalid version count.");

            // Versionen werden auf der Page von Head → Older gespeichert.
            // Push() fügt vorne (als neuen Head) ein. Damit die Kette nach dem
            // Lesen die gleiche Reihenfolge hat, sammeln wir zuerst und pushen
            // dann rückwärts.
            var versionBuffer = new (ulong Seq, byte[]? Value, bool IsTombstone, bool IsOverflowChain)[versionCount];
            for (var v = 0; v < versionCount; v++)
            {
                if (offset + sizeof(ulong) + 1 + sizeof(int) > body.Length)
                    throw new InvalidOperationException("MVCC leaf page payload is corrupted (version header missing).");

                var sequence = BinaryPrimitives.ReadUInt64LittleEndian(body[offset..]);
                offset += sizeof(ulong);

                var flags = body[offset++];
                var isTombstone = (flags & 0x01) != 0;
                var isOverflow = (flags & 0x02) != 0;
                var isOverflowChain = (flags & 0x04) != 0;

                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += sizeof(int);

                byte[]? value;
                if (isOverflow || isOverflowChain)
                {
                    // Overflow pointer: 16 bytes (offset: int64, length: int32, crc: uint32)
                    const int OverflowPointerSize = sizeof(long) + sizeof(int) + sizeof(uint);
                    if (offset + OverflowPointerSize > body.Length)
                        throw new InvalidOperationException("MVCC leaf page overflow pointer truncated.");
                    value = body.Slice(offset, OverflowPointerSize).ToArray();
                    offset += OverflowPointerSize;
                }
                else
                {
                    if (valueLength < 0)
                        throw new InvalidOperationException("MVCC leaf page contains negative inline value length.");

                    if (offset + valueLength > body.Length)
                        throw new InvalidOperationException("MVCC leaf page inline value truncated.");

                    value = valueLength == 0 ? Array.Empty<byte>() : body.Slice(offset, valueLength).ToArray();
                    offset += valueLength;
                }

                versionBuffer[v] = (sequence, value, isTombstone, isOverflowChain);
            }

            VersionedValue<byte[]>? versions = null;
            for (var vi = versionBuffer.Length - 1; vi >= 0; vi--)
            {
                var (seq, val, tombstone, isOverflowChain) = versionBuffer[vi];
                versions = VersionedValue<byte[]>.Push(versions, seq, val, tombstone);
                if (isOverflowChain && versions != null)
                    versions.IsOverflowChain = true;
            }

            entries.Add(new MvccLeafEntry(key, versions));
        }

        return entries;
    }

    /// <summary>
    /// Deserialisiert einen einzelnen Eintrag ab <paramref name="offset"/>.
    /// Wird vom allokationsarmen Scan-Pfad verwendet, wenn ein Eintrag
    /// komplexe Versionierung benötigt (mehrere Versionen, Tombstone, OverflowChain).
    /// </summary>
    public static MvccLeafEntry ReadEntryAt(ReadOnlySpan<byte> body, int offset, out int nextOffset)
    {
        if (offset + sizeof(int) > body.Length)
            throw new InvalidOperationException("MVCC leaf page payload is corrupted (key length missing).");

        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);

        if (keyLength <= 0 || offset + keyLength > body.Length)
            throw new InvalidOperationException("MVCC leaf page contains invalid key length.");

        var key = body.Slice(offset, keyLength).ToArray();
        offset += keyLength;

        if (offset + sizeof(int) > body.Length)
            throw new InvalidOperationException("MVCC leaf page payload is corrupted (version count missing).");

        var versionCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        offset += sizeof(int);

        if (versionCount < 0)
            throw new InvalidOperationException("MVCC leaf page contains invalid version count.");

        var versionBuffer = new (ulong Seq, byte[]? Value, bool IsTombstone, bool IsOverflowChain)[versionCount];
        for (var v = 0; v < versionCount; v++)
        {
            if (offset + sizeof(ulong) + 1 + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (version header missing).");

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(body[offset..]);
            offset += sizeof(ulong);

            var flags = body[offset++];
            var isTombstone = (flags & 0x01) != 0;
            var isOverflow = (flags & 0x02) != 0;
            var isOverflowChain = (flags & 0x04) != 0;

            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            byte[]? value;
            if (isOverflow || isOverflowChain)
            {
                const int OverflowPointerSize = sizeof(long) + sizeof(int) + sizeof(uint);
                if (offset + OverflowPointerSize > body.Length)
                    throw new InvalidOperationException("MVCC leaf page overflow pointer truncated.");
                value = body.Slice(offset, OverflowPointerSize).ToArray();
                offset += OverflowPointerSize;
            }
            else
            {
                if (valueLength < 0)
                    throw new InvalidOperationException("MVCC leaf page contains negative inline value length.");

                if (offset + valueLength > body.Length)
                    throw new InvalidOperationException("MVCC leaf page inline value truncated.");

                value = valueLength == 0 ? Array.Empty<byte>() : body.Slice(offset, valueLength).ToArray();
                offset += valueLength;
            }

            versionBuffer[v] = (sequence, value, isTombstone, isOverflowChain);
        }

        VersionedValue<byte[]>? versions = null;
        for (var vi = versionBuffer.Length - 1; vi >= 0; vi--)
        {
            var (seq, val, tombstone, isOverflowChain) = versionBuffer[vi];
            versions = VersionedValue<byte[]>.Push(versions, seq, val, tombstone);
            if (isOverflowChain && versions != null)
                versions.IsOverflowChain = true;
        }

        nextOffset = offset;
        return new MvccLeafEntry(key, versions);
    }

    /// <summary>
    /// Überspringt die Versionen eines Eintrags, dessen Key außerhalb des Scan-Bereichs liegt.
    /// </summary>
    public static int SkipEntryVersions(ReadOnlySpan<byte> body, int offset, int versionCount)
    {
        for (var v = 0; v < versionCount; v++)
        {
            if (offset + sizeof(ulong) + 1 + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (version header missing).");

            offset += sizeof(ulong) + 1;
            var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            if (valueLength == -1)
            {
                offset += sizeof(long) + sizeof(int) + sizeof(uint);
            }
            else if (valueLength < 0)
            {
                throw new InvalidOperationException("MVCC leaf page contains negative inline value length.");
            }
            else
            {
                offset += valueLength;
            }

            if (offset > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (version value truncated).");
        }

        return offset;
    }

    /// <summary>
    /// Versucht, einen neuen Schlüssel in eine Leaf-Page einzufügen, ohne alle
    /// vorhandenen Einträge deserialisieren zu müssen. Das spart List- und
    /// Zwischenspeicher-Allokationen im Bulk-Insert-Pfad.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> wenn der Schlüssel neu eingefügt wurde;
    /// <see langword="false"/> wenn die Page voll ist oder der Schlüssel bereits existiert.
    /// </returns>
    public static bool TryInsertNewKeyInPlace(OdsPage page, ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value, ulong sequence)
    {
        var body = page.Body;
        var header = page.Header;
        var itemCount = header.ItemCount;

        // Schneller Platz-Check vor dem Scan: Eintrag benötigt KeyLength + 4, VersionCount + 4,
        // Sequence 8, Flags 1, ValueLength 4, Value.
        var newEntrySize = sizeof(int) + key.Length + sizeof(int) + sizeof(ulong) + 1 + sizeof(int) + value.Length;
        if (newEntrySize > body.Length)
            return false;

        var offset = 0;
        var insertAt = itemCount;

        // Bestehende Einträge scannen, um Position und belegte Größe zu ermitteln.
        for (var i = 0; i < itemCount; i++)
        {
            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (key length missing).");

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            var entryStart = offset;
            offset += sizeof(int);

            if (keyLength <= 0 || offset + keyLength > body.Length)
                throw new InvalidOperationException("MVCC leaf page contains invalid key length.");

            var cmp = CompareKeys(body.Slice(offset, keyLength), key);
            if (cmp == 0)
                return false; // Schlüssel existiert bereits; in-place-Versionierung ist hier nicht implementiert.
            if (cmp > 0 && insertAt == itemCount)
                insertAt = i;

            offset += keyLength;

            if (offset + sizeof(int) > body.Length)
                throw new InvalidOperationException("MVCC leaf page payload is corrupted (version count missing).");

            var versionCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);

            if (versionCount < 0)
                throw new InvalidOperationException("MVCC leaf page contains invalid version count.");

            for (var v = 0; v < versionCount; v++)
            {
                if (offset + sizeof(ulong) + 1 + sizeof(int) > body.Length)
                    throw new InvalidOperationException("MVCC leaf page payload is corrupted (version header missing).");

                offset += sizeof(ulong) + 1;
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += sizeof(int);

                if (valueLength == -1)
                {
                    const int OverflowPointerSize = sizeof(long) + sizeof(int) + sizeof(uint);
                    offset += OverflowPointerSize;
                }
                else if (valueLength < 0)
                {
                    throw new InvalidOperationException("MVCC leaf page contains negative inline value length.");
                }
                else
                {
                    offset += valueLength;
                }

                if (offset > body.Length)
                    throw new InvalidOperationException("MVCC leaf page payload is corrupted (version value truncated).");
            }

            _ = entryStart;
        }

        if (offset + newEntrySize > body.Length)
            return false;

        // Einträge ab Insert-Position nach hinten verschieben.
        int writeOffset;
        if (insertAt < itemCount)
        {
            var tailStart = FindEntryOffset(body, insertAt, itemCount);
            var tailLength = offset - tailStart;
            if (tailLength > 0)
            {
                var tailSrc = body.Slice(tailStart, tailLength);
                var tailDst = body.Slice(tailStart + newEntrySize, tailLength);
                tailSrc.CopyTo(tailDst);
            }
            writeOffset = tailStart;
        }
        else
        {
            writeOffset = offset;
        }

        // Neuen Eintrag schreiben.
        var dest = body.Slice(writeOffset);
        BinaryPrimitives.WriteInt32LittleEndian(dest, key.Length);
        key.CopyTo(dest.Slice(sizeof(int)));
        var afterKey = sizeof(int) + key.Length;
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(afterKey), 1); // VersionCount
        var afterVersionCount = afterKey + sizeof(int);
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(afterVersionCount), sequence);
        dest[afterVersionCount + sizeof(ulong)] = 0; // Flags
        var afterFlags = afterVersionCount + sizeof(ulong) + 1;
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(afterFlags), value.Length);
        value.CopyTo(dest.Slice(afterFlags + sizeof(int)));

        page.Header = header with
        {
            PageType = OdsPageType.Leaf,
            ItemCount = itemCount + 1
        };

        // Restliche Bytes nullen, falls sie zuvor überschrieben wurden.
        var clearStart = offset + newEntrySize;
        if (clearStart < body.Length)
            body.Slice(clearStart).Clear();

        return true;
    }

    private static int CompareKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int FindEntryOffset(Span<byte> body, int index, int itemCount)
    {
        var offset = 0;
        for (var i = 0; i < index && i < itemCount; i++)
        {
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int) + keyLength;
            var versionCount = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
            offset += sizeof(int);
            for (var v = 0; v < versionCount; v++)
            {
                offset += sizeof(ulong) + 1;
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += sizeof(int);
                if (valueLength == -1)
                    offset += sizeof(long) + sizeof(int) + sizeof(uint);
                else
                    offset += valueLength;
            }
        }
        return offset;
    }

    /// <summary>
    /// Schreibt alle Einträge in eine Leaf-Page.
    /// Wirft <see cref="InvalidOperationException"/>, wenn die Einträge nicht passen.
    /// </summary>
    public static void WriteEntries(OdsPage page, IReadOnlyList<MvccLeafEntry> entries)
    {
        if (!TryWriteEntries(page, entries))
            throw new InvalidOperationException("MVCC leaf page is full. Node split is required.");
    }

    /// <summary>
    /// Versucht, alle Einträge in die Page zu schreiben.
    /// Gibt <see langword="false"/> zurück (und lässt den Body unverändert) wenn es nicht passt.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryWriteEntries(OdsPage page, IReadOnlyList<MvccLeafEntry> entries)
    {
        var body = page.Body;
        var originalBody = body.ToArray(); // Snapshot für Rollback bei Fehlschlag
        body.Clear();

        var offset = 0;
        foreach (var entry in entries)
        {
            var required = ComputeEntrySize(entry);
            if (offset + required > body.Length)
            {
                // Rollback: originalen Body wiederherstellen
                originalBody.CopyTo(body);
                return false;
            }

            offset += WriteEntry(body[offset..], entry);
        }

        // Zero remaining bytes so stale content cannot be misread
        body.Slice(offset).Clear();

        var header = page.Header;
        page.Header = header with
        {
            PageType = OdsPageType.Leaf,
            ItemCount = entries.Count
        };
        return true;
    }

    /// <summary>
    /// Versucht, einen Teilbereich der Einträge in die Page zu schreiben.
    /// Wird vom Split-Code verwendet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryWriteEntriesRange(OdsPage page, IReadOnlyList<MvccLeafEntry> entries, int start, int count)
    {
        var body = page.Body;
        var originalBody = body.ToArray();
        body.Clear();

        var end = start + count;
        var offset = 0;

        for (var i = start; i < end; i++)
        {
            var required = ComputeEntrySize(entries[i]);
            if (offset + required > body.Length)
            {
                originalBody.CopyTo(body);
                return false;
            }
            offset += WriteEntry(body[offset..], entries[i]);
        }

        body.Slice(offset).Clear();

        var header = page.Header;
        page.Header = header with
        {
            PageType = OdsPageType.Leaf,
            ItemCount = count
        };
        return true;
    }

    // ── Interne Helpers ───────────────────────────────────────────────────────

    /// <summary>Berechnet die Serialisierungsgröße eines Eintrags in Bytes.</summary>
    public static int ComputeEntrySize(in MvccLeafEntry entry)
    {
        var size = sizeof(int)               // KeyLength
                 + entry.Key.Length          // Key
                 + sizeof(int);              // VersionCount

        var current = entry.Versions;
        while (current != null)
        {
            size += sizeof(ulong)            // Sequence
                  + 1                        // Flags
                  + sizeof(int);            // ValueLength

            if (current.Value != null)
            {
                if (current.IsOverflowChain ||
                    (current.Value.Length == OverflowPointer.SizeInBytes &&
                     OverflowPointer.TryDecode(current.Value, out _)))
                {
                    size += OverflowPointer.SizeInBytes;
                }
                else
                {
                    size += current.Value.Length;
                }
            }

            current = current.Older;
        }

        return size;
    }

    /// <summary>
    /// Schreibt einen einzelnen Eintrag in den Buffer ab offset 0.
    /// Gibt die geschriebene Byte-Anzahl zurück.
    /// </summary>
    private static int WriteEntry(Span<byte> dest, in MvccLeafEntry entry)
    {
        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], entry.Key.Length);
        offset += sizeof(int);

        entry.Key.CopyTo(dest[offset..]);
        offset += entry.Key.Length;

        var versionCount = CountVersions(entry.Versions);
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], versionCount);
        offset += sizeof(int);

        var current = entry.Versions;
        while (current != null)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(dest[offset..], current.Sequence);
            offset += sizeof(ulong);

            byte flags = 0;
            if (current.IsTombstone) flags |= 0x01;

            bool isOverflow = current.Value != null &&
                              current.Value.Length == OverflowPointer.SizeInBytes &&
                              OverflowPointer.TryDecode(current.Value, out _);
            if (isOverflow) flags |= 0x02;
            if (current.IsOverflowChain) flags |= 0x04;
            dest[offset++] = flags;

            if (current.Value != null)
            {
                if (isOverflow || current.IsOverflowChain)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], -1);
                    offset += sizeof(int);
                    current.Value.CopyTo(dest[offset..]);
                    offset += current.Value.Length;
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], current.Value.Length);
                    offset += sizeof(int);
                    current.Value.CopyTo(dest[offset..]);
                    offset += current.Value.Length;
                }
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], 0);
                offset += sizeof(int);
            }

            current = current.Older;
        }

        return offset;
    }

    private static int CountVersions(VersionedValue<byte[]>? head)
    {
        int count = 0;
        var current = head;
        while (current != null)
        {
            count++;
            current = current.Older;
        }
        return count;
    }
}
