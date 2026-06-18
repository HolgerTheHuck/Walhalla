// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Walhalla.Storage.Mvcc;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Ein Eintrag in einem MVCC-B+Tree-Blatt.
/// Hält den Key und die <see cref="VersionedValue{T}"/>-Kette für diesen Key.
/// </summary>
internal readonly record struct MvccLeafEntry(byte[] Key, VersionedValue<byte[]>? Versions)
{
    /// <summary>Liest die neueste Version. false bei Tombstone oder keine Versionen.</summary>
    public bool TryGetLatest(out byte[] value)
    {
        if (Versions == null)
        {
            value = default!;
            return false;
        }
        return Versions.TryGetLatest(out value);
    }

    /// <summary>Liest die für <paramref name="snapshotSeq"/> sichtbare Version.</summary>
    public bool TryGetVisible(ulong snapshotSeq, out byte[] value)
    {
        if (Versions == null)
        {
            value = default!;
            return false;
        }
        return Versions.TryGetVisible(snapshotSeq, out value);
    }

    /// <summary>
    /// Fügt eine neue Version als Head hinzu. Ersetzt den Eintrag,
    /// der zurückgegeben wird (da <see cref="VersionedValue{T}"/> immutable-ähnlich ist).
    /// </summary>
    public MvccLeafEntry PushVersion(ulong sequence, byte[]? value, bool isTombstone)
    {
        var newHead = VersionedValue<byte[]>.Push(Versions, sequence, value, isTombstone);
        return new MvccLeafEntry(Key, newHead);
    }

    /// <summary>
    /// Prunet Versionen, die älter als <paramref name="oldestSnapshot"/> sind.
    /// Gibt die Anzahl der entfernten Versionen zurück.
    /// </summary>
    public int PruneVersions(ulong oldestSnapshot, Action<byte[]>? onPruned = null)
    {
        VersionedValue<byte[]>? head = Versions;
        var before = CountVersions(head);
        VersionedValue<byte[]>.Prune(ref head, oldestSnapshot, onPruned);
        var after = CountVersions(head);
        // Wir können Versions nicht direkt setzen (record ist readonly).
        // Der Aufrufer muss den Eintrag mit dem neuen Head ersetzen.
        // Daher geben wir die Differenz zurück — der Aufrufer prüft,
        // ob der Eintrag komplett wegfallen kann (head == null).
        return before - after;
    }

    /// <summary>Gibt zurück, ob nach dem Prune der Eintrag komplett entfernt werden kann.</summary>
    public bool CanRemoveAfterPrune(ulong oldestSnapshot)
    {
        if (Versions == null) return true;

        VersionedValue<byte[]>? head = Versions;
        VersionedValue<byte[]>.Prune(ref head, oldestSnapshot);

        // Wenn nach Prune nur eine Tombstone übrig bleibt, kann der Key physisch gelöscht werden
        if (head == null) return true;
        if (head.Older == null && head.IsTombstone) return true;
        return false;
    }

    /// <summary>Erzeugt einen neuen Eintrag mit gepruntem Head.</summary>
    public MvccLeafEntry WithPrunedHead(ulong oldestSnapshot, Action<byte[]>? onPruned = null)
    {
        if (Versions == null) return this;

        VersionedValue<byte[]>? head = Versions;
        VersionedValue<byte[]>.Prune(ref head, oldestSnapshot, onPruned);
        return new MvccLeafEntry(Key, head);
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
