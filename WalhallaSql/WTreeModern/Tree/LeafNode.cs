using System.Threading;
using WTreeModern.Diagnostics;
using WTreeModern.Operations;

namespace WTreeModern.Tree;

/// <summary>
/// Blattknoten des WTree (MVCC-fähig).
///
/// Speichert pro Key eine VersionedValue-Kette.
/// Format v1 = plain SortedList (wird als Chain mit Sequence=0 gelesen).
/// Format v2 = VersionedValue-Chains.
/// </summary>
internal sealed class LeafNode<TKey, TValue> : INode
    where TKey : notnull
{
    public const byte FORMAT_VERSION = 2;

    // Handle -1 = kein Nachbar (erstes bzw. letztes Blatt)
    public const long NO_NEIGHBOUR = -1L;

    // ── Zustand ─────────────────────────────────────────────────────────────

    public readonly SortedList<TKey, VersionedValue<TValue>> Data;

    public long NextHandle { get; set; } = NO_NEIGHBOUR;
    public long PrevHandle { get; set; } = NO_NEIGHBOUR;

    public long Handle { get; set; } = -1;
    public bool IsDirty { get; set; }

    public int Count => Data.Count;

    /// <summary>Anzahl der sichtbaren (nicht gelöschten) Keys.</summary>
    public int VisibleCount
    {
        get
        {
            int visible = 0;
            foreach (var vv in Data.Values)
                if (!vv.IsTombstone) visible++;
            return visible;
        }
    }

    // ── Latching & Pinning ─────────────────────────────────────────────────

    public readonly ReaderWriterLockSlim Latch = new(LockRecursionPolicy.SupportsRecursion);
    private int _pinCount;

    public TimeSpan LatchTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public ILatchDiagnostics? LatchDiagnostics { get; set; }

    public void EnterShared()
    {
        var diag = LatchDiagnostics;
        var timeout = LatchTimeout;
        diag?.OnWaitStart(Handle, exclusive: false);
        try
        {
            if (!Latch.TryEnterReadLock(timeout))
                throw new WTreeDeadlockException(Handle, exclusive: false, timeout);
            diag?.OnAcquired(Handle, exclusive: false);
        }
        catch
        {
            diag?.OnWaitEnd(Handle);
            throw;
        }
    }

    public void EnterExclusive()
    {
        var diag = LatchDiagnostics;
        var timeout = LatchTimeout;
        diag?.OnWaitStart(Handle, exclusive: true);
        try
        {
            if (!Latch.TryEnterWriteLock(timeout))
                throw new WTreeDeadlockException(Handle, exclusive: true, timeout);
            diag?.OnAcquired(Handle, exclusive: true);
        }
        catch
        {
            diag?.OnWaitEnd(Handle);
            throw;
        }
    }

    public void ExitLatch()
    {
        bool wasRead = Latch.IsReadLockHeld;
        bool wasWrite = Latch.IsWriteLockHeld;
        if (wasRead) Latch.ExitReadLock();
        else if (wasWrite) Latch.ExitWriteLock();

        var diag = LatchDiagnostics;
        if (wasRead) diag?.OnReleased(Handle, exclusive: false);
        else if (wasWrite) diag?.OnReleased(Handle, exclusive: true);
    }

    public void Pin() => Interlocked.Increment(ref _pinCount);
    public void Unpin() => Interlocked.Decrement(ref _pinCount);
    public bool IsPinned => Interlocked.CompareExchange(ref _pinCount, 0, 0) > 0;

    // ── Konstruktor ──────────────────────────────────────────────────────────

    public LeafNode(IComparer<TKey> comparer)
    {
        Data = new SortedList<TKey, VersionedValue<TValue>>(comparer);
    }

    // ── Operationen anwenden (MVCC) ─────────────────────────────────────────

    /// <summary>
    /// Wendet Operationen unter Berücksichtigung ihrer Sequence-Nummern an.
    /// </summary>
    public void Apply(IEnumerable<Operation<TKey, TValue>> ops)
    {
        foreach (var op in ops)
        {
            switch (op.Type)
            {
                case OperationType.Upsert:
                    Data[op.Key] = VersionedValue<TValue>.Push(
                        Data.TryGetValue(op.Key, out var existing) ? existing : null,
                        op.Sequence, op.Value, isTombstone: false);
                    break;
                case OperationType.Delete:
                    Data[op.Key] = VersionedValue<TValue>.Push(
                        Data.TryGetValue(op.Key, out var existingDel) ? existingDel : null,
                        op.Sequence, default, isTombstone: true);
                    break;
            }
        }
        IsDirty = true;
    }

    // ── Split ────────────────────────────────────────────────────────────────

    public LeafNode<TKey, TValue> Split(long newHandle, out TKey splitKey)
    {
        int half = Data.Count / 2;
        var rightNode = new LeafNode<TKey, TValue>(Data.Comparer);

        for (int i = half; i < Data.Count; i++)
            rightNode.Data.Add(Data.Keys[i], Data.Values[i]);

        for (int i = Data.Count - 1; i >= half; i--)
            Data.RemoveAt(i);

        splitKey = rightNode.Data.Keys[0];

        rightNode.NextHandle = this.NextHandle;
        rightNode.PrevHandle = NO_NEIGHBOUR;
        this.NextHandle = newHandle;

        IsDirty = true;
        rightNode.IsDirty = true;

        return rightNode;
    }

    // ── Zusammenführen ───────────────────────────────────────────────────────

    public void MergeFrom(LeafNode<TKey, TValue> right)
    {
        foreach (var kv in right.Data)
            Data[kv.Key] = kv.Value;

        NextHandle = right.NextHandle;
        IsDirty = true;
    }

    // ── Suche ────────────────────────────────────────────────────────────────

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (Data.TryGetValue(key, out var vv) && vv.TryGetLatest(out value))
            return true;
        value = default!;
        return false;
    }

    public bool TryGetValue(TKey key, ulong snapshotSeq, out TValue value)
    {
        if (Data.TryGetValue(key, out var vv))
            return vv.TryGetVisible(snapshotSeq, out value);
        value = default!;
        return false;
    }

    public TKey FirstKey => Data.Keys[0];

    // ── GC ───────────────────────────────────────────────────────────────────

    public void PruneOldVersions(ulong oldestSnapshot, Action<TKey, TValue>? onPruned = null)
    {
        foreach (var key in Data.Keys.ToList())
        {
            var vv = Data[key];
            Action<TValue>? valuePruned = onPruned != null
                ? v => onPruned(key, v)
                : null;
            VersionedValue<TValue>.Prune(ref vv, oldestSnapshot, valuePruned);
            if (vv == null)
                Data.Remove(key);
            else
                Data[key] = vv;
        }
    }

    // ── Serialisierung v2 ────────────────────────────────────────────────────

    public void Serialize(BinaryWriter w,
                          Action<BinaryWriter, TKey> writeKey,
                          Action<BinaryWriter, TValue> writeValue)
    {
        w.Write((byte)NodeType.Leaf);
        w.Write(FORMAT_VERSION);
        w.Write(NextHandle);
        w.Write(PrevHandle);
        w.Write(Data.Count);
        for (int i = 0; i < Data.Count; i++)
        {
            writeKey(w, Data.Keys[i]);
            SerializeVersionChain(w, Data.Values[i], writeValue);
        }
        IsDirty = false;
    }

    private static void SerializeVersionChain(BinaryWriter w,
                                               VersionedValue<TValue> head,
                                               Action<BinaryWriter, TValue> writeValue)
    {
        int count = 0;
        var current = head;
        while (current != null) { count++; current = current.Older; }
        w.Write(count);

        current = head;
        while (current != null)
        {
            w.Write(current.Sequence);
            w.Write(current.IsTombstone);
            if (!current.IsTombstone)
                writeValue(w, current.Value!);
            current = current.Older;
        }
    }

    public static LeafNode<TKey, TValue> Deserialize(
        BinaryReader r,
        IComparer<TKey> comparer,
        Func<BinaryReader, TKey> readKey,
        Func<BinaryReader, TValue> readValue)
    {
        byte version = r.ReadByte();
        var node = new LeafNode<TKey, TValue>(comparer);
        node.NextHandle = r.ReadInt64();
        node.PrevHandle = r.ReadInt64();

        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var key = readKey(r);
            if (version == 1)
            {
                var value = readValue(r);
                node.Data.Add(key, new VersionedValue<TValue>
                {
                    Sequence = 0,
                    IsTombstone = false,
                    Value = value,
                    Older = null
                });
            }
            else if (version == 2)
            {
                node.Data.Add(key, DeserializeVersionChain(r, readValue));
            }
            else
            {
                throw new InvalidDataException($"LeafNode: unbekannte Version {version}.");
            }
        }

        node.IsDirty = false;
        return node;
    }

    private static VersionedValue<TValue> DeserializeVersionChain(
        BinaryReader r, Func<BinaryReader, TValue> readValue)
    {
        int versionCount = r.ReadInt32();
        VersionedValue<TValue>? tail = null;
        VersionedValue<TValue>? head = null;

        for (int i = 0; i < versionCount; i++)
        {
            ulong seq = r.ReadUInt64();
            bool tomb = r.ReadBoolean();
            TValue? val = tomb ? default : readValue(r);

            var node = new VersionedValue<TValue>
            {
                Sequence = seq,
                IsTombstone = tomb,
                Value = val,
                Older = null
            };

            if (head == null)
            {
                head = node;
                tail = node;
            }
            else
            {
                tail!.Older = node;
                tail = node;
            }
        }

        return head!;
    }
}
