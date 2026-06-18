using System.Threading;
using WTreeModern.Diagnostics;
using WTreeModern.Operations;

namespace WTreeModern.Tree;

/// <summary>
/// Innerer Knoten des WTree (kein Blatt).
///
/// Enthält:
///   • Branches – geordnete Liste von (SplitKey, ChildHandle)-Paaren
///   • Buffer   – ausstehende Operationen, die noch nicht weiter propagiert wurden
///
/// Die "Waterfall"-Eigenschaft ist, dass Schreiboperationen NICHT sofort
/// bis ins Blatt laufen, sondern im Buffer hier gesammelt werden.
/// Erst wenn der Buffer voll ist, fallen sie zum nächsten Level herunter.
/// </summary>
internal sealed class InternalNode<TKey, TValue> : INode
    where TKey : notnull
{
    public const byte FORMAT_VERSION = 1;

    /// <summary>
    /// Einzelner Ast: SplitKey gibt die untere Grenze des Kindknotens an.
    /// Index 0 hat immer SplitKey = null (fängt alle kleinsten Keys ab).
    /// </summary>
    public record Branch(TKey? SplitKey, long ChildHandle);

    // ── Zustand ─────────────────────────────────────────────────────────────

    public readonly List<Branch> Branches = [];

    /// <summary>Gepufferte Operationen – der "Wasserfall-Puffer".</summary>
    public readonly OperationBatch<TKey, TValue> Buffer;

    public long Handle { get; set; } = -1;
    public bool IsDirty { get; set; }

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

    // ── Konstruktoren ────────────────────────────────────────────────────────

    public InternalNode(IComparer<TKey> comparer)
    {
        Buffer = new OperationBatch<TKey, TValue>(comparer);
    }

    // ── Suche ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gibt den Index des Asts zurück, in dem der Key liegt.
    /// Branches[0] fängt alles ≤ Branches[1].SplitKey ab.
    /// </summary>
    public int FindBranchIndex(TKey key, IComparer<TKey> comparer)
    {
        // Binäre Suche: letzter Ast dessen SplitKey ≤ key
        int lo = 1, hi = Branches.Count - 1, result = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int cmp = comparer.Compare(Branches[mid].SplitKey!, key);
            if (cmp <= 0)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    // ── Serialisierung ───────────────────────────────────────────────────────

    public void Serialize(BinaryWriter w, Action<BinaryWriter, TKey> writeKey,
                          Action<BinaryWriter, TValue> writeValue)
    {
        w.Write((byte)NodeType.Internal);
        w.Write(FORMAT_VERSION);

        // Äste
        w.Write(Branches.Count);
        for (int i = 0; i < Branches.Count; i++)
        {
            bool hasSplit = Branches[i].SplitKey is not null;
            w.Write(hasSplit);
            if (hasSplit)
                writeKey(w, Branches[i].SplitKey!);
            w.Write(Branches[i].ChildHandle);
        }

        // Buffer-Operationen
        var ops = Buffer.All;
        w.Write(ops.Count);
        foreach (var op in ops)
        {
            w.Write((byte)op.Type);
            writeKey(w, op.Key);
            bool hasValue = op.Type == OperationType.Upsert;
            w.Write(hasValue);
            if (hasValue)
                writeValue(w, op.Value!);
        }

        IsDirty = false;
    }

    public static InternalNode<TKey, TValue> Deserialize(
        BinaryReader r,
        IComparer<TKey> comparer,
        Func<BinaryReader, TKey> readKey,
        Func<BinaryReader, TValue> readValue)
    {
        // NodeType wurde bereits vom Aufrufer gelesen
        byte version = r.ReadByte();
        if (version != FORMAT_VERSION)
            throw new InvalidDataException($"InternalNode: unbekannte Version {version}.");

        var node = new InternalNode<TKey, TValue>(comparer);

        int branchCount = r.ReadInt32();
        for (int i = 0; i < branchCount; i++)
        {
            bool hasSplit = r.ReadBoolean();
            TKey? splitKey = hasSplit ? readKey(r) : default;
            long childHandle = r.ReadInt64();
            node.Branches.Add(new Branch(splitKey, childHandle));
        }

        int opCount = r.ReadInt32();
        for (int i = 0; i < opCount; i++)
        {
            var type = (OperationType)r.ReadByte();
            var key = readKey(r);
            bool hasValue = r.ReadBoolean();
            TValue? value = hasValue ? readValue(r) : default;
            node.Buffer.Add(new Operation<TKey, TValue>(type, key, value));
        }

        node.IsDirty = false;
        return node;
    }
}
