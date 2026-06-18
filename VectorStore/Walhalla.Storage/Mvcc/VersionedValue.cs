namespace Walhalla.Storage.Mvcc;

/// <summary>Eine versionierte Wertkette für MVCC.
/// Die Kette verläuft vom neuesten (Head) zu älteren Versionen.</summary>
public sealed class VersionedValue<TValue>
{
    public ulong Sequence;
    public bool IsTombstone;
    public bool IsOverflowChain;
    public TValue? Value;
    public VersionedValue<TValue>? Older;

    /// <summary>Gibt die sichtbare Version für eine Snapshot-Sequenz zurück.</summary>
    public bool TryGetVisible(ulong snapshotSeq, out TValue value)
    {
        var current = this;
        while (current != null)
        {
            if (current.Sequence <= snapshotSeq)
            {
                if (current.IsTombstone)
                {
                    value = default!;
                    return false;
                }
                value = current.Value!;
                return true;
            }
            current = current.Older;
        }

        value = default!;
        return false;
    }

    /// <summary>Gibt den neuesten (Head) Wert zurück. false bei Tombstone.</summary>
    public bool TryGetLatest(out TValue value)
    {
        if (IsTombstone)
        {
            value = default!;
            return false;
        }
        value = Value!;
        return true;
    }

    /// <summary>Fügt eine neue Version als Head hinzu.</summary>
    public static VersionedValue<TValue> Push(
        VersionedValue<TValue>? head,
        ulong sequence,
        TValue? value,
        bool isTombstone)
    {
        return new VersionedValue<TValue>
        {
            Sequence = sequence,
            IsTombstone = isTombstone,
            Value = value,
            Older = head
        };
    }

    /// <summary>Entfernt Versionen, die älter als oldestSnapshot sind.</summary>
    /// <param name="onPruned">Optional callback invoked for each pruned non-tombstone value.</param>
    public static void Prune(ref VersionedValue<TValue>? head, ulong oldestSnapshot, Action<TValue>? onPruned = null)
    {
        if (head == null) return;

        // Alle Versionen >= oldestSnapshot bleiben erhalten (für neuere Snapshots).
        // Die neueste Version < oldestSnapshot muss auch bleiben (für den ältesten Snapshot).
        // Alles, was älter ist als diese, kann entfernt werden.
        var current = head;
        while (current != null && current.Sequence >= oldestSnapshot)
            current = current.Older;

        if (current != null)
        {
            // Notify for every pruned version in the detached chain.
            if (onPruned != null)
            {
                var pruned = current.Older;
                while (pruned != null)
                {
                    if (!pruned.IsTombstone && pruned.Value != null)
                        onPruned(pruned.Value);
                    pruned = pruned.Older;
                }
            }
            current.Older = null;
        }
    }
}
