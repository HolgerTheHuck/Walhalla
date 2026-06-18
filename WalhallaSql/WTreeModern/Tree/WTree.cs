using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;
using WTreeModern.Operations;
using WTreeModern.Storage;
using WTreeModern.Transactions;

namespace WTreeModern.Tree;

/// <summary>
/// WTree&lt;TKey, TValue&gt; – ein sortierter, persistenter Key-Value-Store
/// basierend auf dem B^ε-Baum-Algorithmus ("Waterfall Tree").
///
/// Kernidee (warum kein klassischer B+-Baum):
///   Im B+-Baum kostet jedes Insert O(log N) I/O-Operationen,
///   weil die Operation sofort bis ins Blatt propagiert wird.
///
///   Im WTree wird jede Schreiboperation zunächst im Puffer (Buffer)
///   des Root-Knotens gesammelt. Erst wenn der Puffer voll ist,
///   "fällt" er kaskadierend nach unten – wie ein Wasserfall.
///   Amortisiert ergibt das O(log_b(N) / B) I/Os pro Schreiboperation,
///   was bei batch-lastigen Workloads dramatisch besser ist.
///
/// Besonderheiten:
///   • Sortierte Iteration (Forward + Backward) via doppelt verketteter Blattliste
///   • Reines C# ohne native Dependencies
///   • Storage-agnostisch (IBlockStore: File oder Memory)
///   • Generisch: WTree&lt;TKey, TValue&gt; mit benutzerdefinierten Comparern
/// </summary>
public sealed class WTree<TKey, TValue> : IDisposable, IAsyncDisposable
    where TKey : notnull
{
    // ── Konstanten ───────────────────────────────────────────────────────────

    /// <summary>Maximale Anzahl Records pro Blattknoten.</summary>
    public int MaxLeafRecords { get; init; } = 4_096;

    /// <summary>Max. Anzahl Operationen im Puffer eines internen Knotens.</summary>
    public int InternalBufferThreshold { get; init; } = 1_024;

    /// <summary>
    /// Max. Operationen im Root-Puffer bevor Sink() ausgelöst wird.
    /// Kleinerer Wert → häufigere, kleinere Flushes.
    /// </summary>
    public int RootBufferThreshold { get; init; } = 256;

    /// <summary>Max. Anzahl Äste in einem internen Knoten.</summary>
    public int MaxBranches { get; init; } = 32;

    /// <summary>
    /// Min. Anzahl Äste in einem internen Knoten (außer Root).
    /// Default -1: automatisch MaxBranches / 2.
    /// </summary>
    public int MinBranches { get; init; } = -1;

    /// <summary>Effektive Untergrenze für Branches (mindestens 2).</summary>
    private int ActualMinBranches => MinBranches > 0 ? MinBranches : Math.Max(2, MaxBranches / 2);

    /// <summary>
    /// Min. Anzahl Records pro Blattknoten (außer wenn es das einzige Blatt ist).
    /// Default: MaxLeafRecords / 2.
    /// </summary>
    public int MinLeafRecords { get; init; } = -1;

    private int ActualMinLeafRecords => MinLeafRecords > 0 ? MinLeafRecords : MaxLeafRecords / 2;

    /// <summary>
    /// Maximale Anzahl Knoten im In-Memory-Cache.
    /// Älteste, nicht-dirty Knoten werden bei Überschreitung automatisch evicted (LRU).
    /// Default: 512.
    /// </summary>
    public int MaxCacheNodes { get; init; } = 512;

    private const long HEADER_HANDLE = 0;

    // ── Felder ───────────────────────────────────────────────────────────────

    private readonly IBlockStore _store;
    private readonly IAsyncBlockStore _asyncStore;
    private readonly IComparer<TKey> _comparer;
    private readonly Serializers<TKey, TValue> _ser;
    private readonly Diagnostics.ILogger _logger;
    private readonly Diagnostics.ITelemetry _telemetry;
    private readonly Diagnostics.ILatchDiagnostics _latchDiagnostics;
    private readonly TimeSpan _latchTimeout;

    // Geladene Knoten (Handle → InternalNode oder LeafNode) – LRU-begrenzt
    private LruNodeCache _cache = null!;

    private long _rootHandle;
    private long _firstLeafHandle;
    private long _lastLeafHandle;       // O(1)-Zugriff für Backward()
    private int  _depth; // 1 = root ist Blatt, 2 = root + Blätter, ...
    private long _rootVersion; // seqlock: even = stabil, odd = Schreiben läuft

    private int  _cachedCount = -1;     // -1 = stale; wird von Count lazy befüllt
    private bool _disposed;

    private readonly SemaphoreSlim _treeLock = new(1, 1);
    private readonly TransactionManager _txManager;

    // ── Optionaler Bloom-Filter ──────────────────────────────────────────────

    private readonly bool _useBloomFilter;
    private readonly int _bloomFilterExpectedItems;
    private readonly double _bloomFilterFpp;
    private BloomFilter? _bloomFilter;
    private long _bloomFilterHandle = -1;

    // ── Optionaler Large-Value-Sidecar ───────────────────────────────────────

    private readonly IBlockStore? _largeValueStore;
    private readonly int _largeValueThreshold;

    // ── Konstruktor & Initialisierung ────────────────────────────────────────

    /// <param name="store">Persistenz-Backend (FileBlockStore oder MemoryBlockStore).</param>
    /// <param name="serializers">Serialisierungs-Delegaten für TKey und TValue.</param>
    /// <param name="comparer">Sortierreihenfolge; null = Comparer&lt;TKey&gt;.Default.</param>
    /// <param name="logger">Optionaler Logger; Default = NoOp.</param>
    /// <param name="telemetry">Optionale Telemetrie; Default = NoOp.</param>
    /// <param name="latchDiagnostics">Optionale Latch-Diagnostik; Default = NoOp.</param>
    /// <param name="latchTimeout">Timeout für Latch-Acquisition; Default = 30s.</param>
    /// <param name="useBloomFilter">Aktiviert den optionalen Bloom-Filter für schnellere Negative-Lookups.</param>
    /// <param name="bloomFilterExpectedItems">Erwartete Anzahl Items für Bloom-Filter-Größenberechnung.</param>
    /// <param name="bloomFilterFpp">Ziel-False-Positive-Rate des Bloom-Filters (Default 1%).</param>
    /// <param name="largeValueStore">Optionaler separater Store für Werte, die den Threshold überschreiten.</param>
    /// <param name="largeValueThreshold">Werte größer als dieser Threshold (Bytes) werden in den Sidecar ausgelagert.</param>
    public WTree(IBlockStore store, Serializers<TKey, TValue> serializers,
                 IComparer<TKey>? comparer = null,
                 Diagnostics.ILogger? logger = null,
                 Diagnostics.ITelemetry? telemetry = null,
                 Diagnostics.ILatchDiagnostics? latchDiagnostics = null,
                 TimeSpan? latchTimeout = null,
                 bool useBloomFilter = false,
                 int bloomFilterExpectedItems = 100_000,
                 double bloomFilterFpp = 0.01,
                 IBlockStore? largeValueStore = null,
                 int? largeValueThreshold = null)
    {
        _store      = store;
        _asyncStore = store as IAsyncBlockStore ?? new SyncBlockStoreAdapter(store);
        _ser        = serializers;
        _comparer   = comparer ?? Comparer<TKey>.Default;
        _logger     = logger ?? Diagnostics.NoOpLogger.Instance;
        _telemetry  = telemetry ?? Diagnostics.NoOpTelemetry.Instance;
        _latchDiagnostics = latchDiagnostics ?? Diagnostics.NoOpLatchDiagnostics.Instance;
        _latchTimeout = latchTimeout ?? TimeSpan.FromSeconds(30);
        _useBloomFilter = useBloomFilter;
        _bloomFilterExpectedItems = bloomFilterExpectedItems;
        _bloomFilterFpp = bloomFilterFpp;
        _largeValueStore = largeValueStore;
        _largeValueThreshold = largeValueThreshold ?? -1;

        // Use structural equality for byte[] keys so write-conflict detection works
        // across different byte[] array instances with the same content.
        _txManager = typeof(TKey) == typeof(byte[])
            ? new TransactionManager(new ByteArrayEqualityComparer())
            : new TransactionManager();

        _cache    = new LruNodeCache(MaxCacheNodes, node => node switch
        {
            InternalNode<TKey, TValue> n => n.IsDirty,
            LeafNode<TKey, TValue>     n => n.IsDirty,
            _                           => false
        }, _telemetry);

        if (_store.Exists(HEADER_HANDLE))
            LoadHeader();
        else
            InitializeNewTree();
    }

    private void InitializeNewTree()
    {
        // Header-Handle reservieren
        long hdr = _store.AllocateHandle();
        System.Diagnostics.Debug.Assert(hdr == HEADER_HANDLE);

        // Erstes (und einziges) Blatt anlegen
        long leafHandle = _store.AllocateHandle();
        var  leaf       = new LeafNode<TKey, TValue>(_comparer) { IsDirty = true };
        StoreNode(leafHandle, leaf);

        WriteRootSnapshot(leafHandle, 1);
        _firstLeafHandle = leafHandle;
        _lastLeafHandle  = leafHandle;
        _cachedCount     = 0;

        if (_useBloomFilter)
            _bloomFilter = new BloomFilter(_bloomFilterExpectedItems, _bloomFilterFpp);
    }

    private void StoreNode(long handle, INode node)
    {
        node.Handle = handle;
        node.LatchTimeout = _latchTimeout;
        node.LatchDiagnostics = _latchDiagnostics;
        _cache.Set(handle, node);
    }

    private (long Handle, int Depth) ReadRootSnapshot()
    {
        while (true)
        {
            long v1 = Volatile.Read(ref _rootVersion);
            if ((v1 & 1L) != 0)
            {
                Thread.Yield();
                continue;
            }
            long handle = Volatile.Read(ref _rootHandle);
            int depth   = Volatile.Read(ref _depth);
            long v2     = Volatile.Read(ref _rootVersion);
            if (v1 == v2) return (handle, depth);
        }
    }

    private void WriteRootSnapshot(long newHandle, int newDepth)
    {
        Interlocked.Increment(ref _rootVersion); // ungerade → Schreiben läuft
        Volatile.Write(ref _rootHandle, newHandle);
        Volatile.Write(ref _depth, newDepth);
        Interlocked.Increment(ref _rootVersion); // gerade → stabil
    }

    // ── Transaktions-API ──────────────────────────────────────────────────────

    /// <summary>Startet eine neue Transaktion auf diesem Baum.</summary>
    public ITransaction<TKey, TValue> BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong txId = _txManager.AcquireTxId();
        ulong snapshot = _txManager.AcquireSnapshot();
        return new Transaction<TKey, TValue>(this, _txManager, txId, snapshot, level, _logger);
    }

    // ── Telemetrie-Helfer ──────────────────────────────────────────────────

    private static long ElapsedMicroseconds(long startTicks) =>
        (long)((Stopwatch.GetTimestamp() - startTicks) * 1_000_000.0 / Stopwatch.Frequency);

    // ── Öffentliche Schreib-API ──────────────────────────────────────────────

    /// <summary>Fügt einen Key-Value-Eintrag ein oder überschreibt ihn.</summary>
    public void Upsert(TKey key, TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        _treeLock.Wait();
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Upsert, key, value, seq));
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>Löscht einen Key (kein Fehler falls nicht vorhanden).</summary>
    public void Delete(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        _treeLock.Wait();
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Delete, key, sequence: seq));
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>
    /// Löscht einen Key und gibt an, ob er vorhanden war.
    /// Spült intern den Pfad zum Key (FlushPath), bevor geprüft wird.
    /// </summary>
    /// <returns>true wenn der Key existierte und gelöscht wurde; false wenn nicht vorhanden.</returns>
    public bool TryDelete(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        _treeLock.Wait();
        try
        {
            bool existed = TryGetReadOnly(key, out _);
            if (existed)
                ApplyOperation(new Operation<TKey, TValue>(OperationType.Delete, key, sequence: seq));
            return existed;
        }
        finally { _treeLock.Release(); }
    }

    internal void Upsert(TKey key, TValue value, ulong sequence)
    {
        _treeLock.Wait();
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Upsert, key, value, sequence));
        }
        finally { _treeLock.Release(); }
    }

    internal void Delete(TKey key, ulong sequence)
    {
        _treeLock.Wait();
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Delete, key, sequence: sequence));
        }
        finally { _treeLock.Release(); }
    }

    private void ApplyOperation(Operation<TKey, TValue> op)
    {
        _cachedCount = -1;
        _telemetry.IncrementCounter("write_ops");

        if (_useBloomFilter && op.Type == OperationType.Upsert)
            BloomFilterAdd(op.Key);

        if (op.Type == OperationType.Upsert && op.Value != null)
            op = new Operation<TKey, TValue>(op.Type, op.Key, MaybeInlineLargeValue(op.Value), op.Sequence);

        if (_depth == 1)
        {
            var leaf = GetLeaf(_rootHandle);
            var leafNode = (INode)leaf;
            leafNode.EnterExclusive();
            try
            {
                leaf.Apply([op]);
                if (leaf.Count > MaxLeafRecords)
                    SplitRootLeaf(leaf);
            }
            finally { leafNode.ExitLatch(); }
            return;
        }

        var root = GetInternal(_rootHandle);
        var rootNode = (INode)root;
        rootNode.EnterExclusive();
        try
        {
            root.Buffer.Add(op);
            root.IsDirty = true;

            if (root.Buffer.Count >= RootBufferThreshold)
                Sink();
        }
        finally { rootNode.ExitLatch(); }
    }

    // ── Öffentliche Lese-API ─────────────────────────────────────────────────

    /// <summary>Sucht einen Key. Gibt true zurück wenn gefunden.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        return TryGetReadOnly(key, out value);
    }

    /// <summary>Gibt true zurück wenn der Key im Baum vorhanden ist.</summary>
    public bool ContainsKey(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        return TryGetReadOnly(key, out _);
    }

    /// <summary>
    /// Liest einen Key unter einem bestimmten Snapshot (read-only Buffer-Scan).
    /// </summary>
    internal bool TryGetWithSnapshot(TKey key, ulong snapshotSeq, out TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        return TryGetWithSnapshotReadOnly(key, snapshotSeq, out value);
    }

    // ── Read-Only Buffer Scan (kein FlushPath) ────────────────────────────────

    private bool TryGetReadOnly(TKey key, out TValue value)
    {
        _telemetry.IncrementCounter("read_ops");

        if (_bloomFilter != null && !BloomFilterMayContain(key))
        {
            value = default!;
            return false;
        }

        var (handle, _) = ReadRootSnapshot();

        INode current = (INode)LoadNode(handle);
        current.EnterShared();
        current.Pin();

        try
        {
            while (true)
            {
                if (current is LeafNode<TKey, TValue> leaf)
                {
                    bool found = leaf.TryGetValue(key, out value!);
                    if (found)
                        value = MaybeResolveLargeValue(value);
                    current.ExitLatch();
                    current.Unpin();
                    return found;
                }

                var internalNode = (InternalNode<TKey, TValue>)current;

                if (internalNode.Buffer.TryScanForKey(key, out var op))
                {
                    current.ExitLatch();
                    current.Unpin();
                    if (op.Type == OperationType.Upsert)
                    {
                        value = MaybeResolveLargeValue(op.Value!);
                        return true;
                    }
                    value = default!;
                    return false; // Delete
                }

                int idx = internalNode.FindBranchIndex(key, _comparer);
                long childHandle = internalNode.Branches[idx].ChildHandle;

                INode child;
                try
                {
                    child = (INode)LoadNode(childHandle);
                }
                catch
                {
                    current.ExitLatch();
                    current.Unpin();
                    throw;
                }

                child.EnterShared();
                child.Pin();

                current.ExitLatch();
                current.Unpin();

                current = child;
            }
        }
        catch
        {
            try { current.ExitLatch(); current.Unpin(); } catch { }
            throw;
        }
    }

    private bool TryGetWithSnapshotReadOnly(TKey key, ulong snapshotSeq, out TValue value)
    {
        _telemetry.IncrementCounter("read_ops");
        var (handle, _) = ReadRootSnapshot();

        INode current = (INode)LoadNode(handle);
        current.EnterShared();
        current.Pin();

        Operation<TKey, TValue> bestOp = default;
        bool hasBest = false;

        try
        {
            while (true)
            {
                if (current is LeafNode<TKey, TValue> leaf)
                {
                    if (hasBest)
                    {
                        current.ExitLatch();
                        current.Unpin();
                        if (bestOp.Type == OperationType.Upsert)
                        {
                            value = MaybeResolveLargeValue(bestOp.Value!);
                            return true;
                        }
                        value = default!;
                        return false; // Delete
                    }

                    bool found = leaf.TryGetValue(key, snapshotSeq, out value!);
                    if (found)
                        value = MaybeResolveLargeValue(value);
                    current.ExitLatch();
                    current.Unpin();
                    return found;
                }

                var internalNode = (InternalNode<TKey, TValue>)current;

                if (internalNode.Buffer.TryScanForKey(key, out var op) && op.Sequence <= snapshotSeq)
                {
                    if (!hasBest || op.Sequence > bestOp.Sequence)
                    {
                        bestOp = op;
                        hasBest = true;
                    }
                }

                int idx = internalNode.FindBranchIndex(key, _comparer);
                long childHandle = internalNode.Branches[idx].ChildHandle;

                INode child;
                try
                {
                    child = (INode)LoadNode(childHandle);
                }
                catch
                {
                    current.ExitLatch();
                    current.Unpin();
                    throw;
                }

                child.EnterShared();
                child.Pin();

                current.ExitLatch();
                current.Unpin();

                current = child;
            }
        }
        catch
        {
            try { current.ExitLatch(); current.Unpin(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Gibt die Anzahl der Key-Value-Paare im Baum zurück.
    /// Gecacht nach dem ersten Aufruf; wird durch jede Schreiboperation invalidiert.
    /// Beim ersten Aufruf nach einer Schreiboperation: O(N) Blattlisten-Walk + FlushAll.
    /// </summary>
    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
            _treeLock.Wait();
            try
            {
                if (_cachedCount >= 0) return _cachedCount;

                _telemetry.IncrementCounter("count_stale");
                FlushAll();
                int count  = 0;
                long handle = _firstLeafHandle;
                while (handle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
                {
                    var leaf = GetLeaf(handle);
                    count  += leaf.VisibleCount;
                    handle  = leaf.NextHandle;
                }
                _cachedCount = count;
                return _cachedCount;
            }
            finally { _treeLock.Release(); }
        }
    }

    /// <summary>
    /// Entfernt alle Einträge. Der Store behält bestehende Blöcke (Garbage bleibt
    /// im Store zurück, aber das ist für jetzt akzeptabel – kein Compaction).
    /// Nach Clear ist der Baum wieder im Ausgangszustand (Tiefe 1, ein leeres Blatt).
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            _cache.Clear();

            long leafHandle = _store.AllocateHandle();
            var  leaf       = new LeafNode<TKey, TValue>(_comparer) { IsDirty = true };
            _cache.Set(leafHandle, leaf);

            WriteRootSnapshot(leafHandle, 1);
            _firstLeafHandle = leafHandle;
            _lastLeafHandle  = leafHandle;
            _cachedCount     = 0;

            if (_bloomFilter != null)
            {
                _bloomFilter.Reset();
                _bloomFilterHandle = -1;
            }

            SaveHeader();
            _store.Commit();
        }
        finally { _treeLock.Release(); }
    }

    // ── Forward ──────────────────────────────────────────────────────────────

    /// <summary>Iteriert alle Key-Value-Paare in aufsteigender Reihenfolge.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Forward()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesForward(_firstLeafHandle);
            return ForwardYield(handles, default!, default!, hasFrom: false, hasTo: false);
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>Iteriert Key-Value-Paare aufsteigend ab <paramref name="from"/> (inklusiv).</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Forward(TKey from)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesForward(_firstLeafHandle);
            return ForwardYield(handles, from, default!, hasFrom: true, hasTo: false);
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>
    /// Iteriert Key-Value-Paare aufsteigend von <paramref name="from"/> bis
    /// <paramref name="to"/> (beide inklusiv).
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Forward(TKey from, TKey to)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesForward(_firstLeafHandle);
            return ForwardYield(handles, from, to, hasFrom: true, hasTo: true);
        }
        finally { _treeLock.Release(); }
    }

    // ── Backward ─────────────────────────────────────────────────────────────

    /// <summary>Iteriert alle Key-Value-Paare in absteigender Reihenfolge.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Backward()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesBackward(_lastLeafHandle);
            return BackwardYield(handles, default!, default!, hasFrom: false, hasTo: false);
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>
    /// Iteriert Key-Value-Paare absteigend ab <paramref name="from"/> (obere Grenze, inklusiv).
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Backward(TKey from)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesBackward(_lastLeafHandle);
            return BackwardYield(handles, from, default!, hasFrom: true, hasTo: false);
        }
        finally { _treeLock.Release(); }
    }

    /// <summary>
    /// Iteriert Key-Value-Paare absteigend von <paramref name="from"/> (obere Grenze) bis
    /// <paramref name="to"/> (untere Grenze, beide inklusiv).
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Backward(TKey from, TKey to)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            FlushAll();
            var handles = CollectLeafHandlesBackward(_lastLeafHandle);
            return BackwardYield(handles, from, to, hasFrom: true, hasTo: true);
        }
        finally { _treeLock.Release(); }
    }

    // ── Async API ───────────────────────────────────────────────────────────

    public async Task UpsertAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        await _treeLock.WaitAsync(ct);
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Upsert, key, value, seq));
        }
        finally { _treeLock.Release(); }
    }

    public async Task DeleteAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        await _treeLock.WaitAsync(ct);
        try
        {
            ApplyOperation(new Operation<TKey, TValue>(OperationType.Delete, key, sequence: seq));
        }
        finally { _treeLock.Release(); }
    }

    public async Task<bool> TryDeleteAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ulong seq = _txManager.AcquireCommitSequence();
        await _treeLock.WaitAsync(ct);
        try
        {
            bool existed = TryGetReadOnly(key, out _);
            if (existed)
                ApplyOperation(new Operation<TKey, TValue>(OperationType.Delete, key, sequence: seq));
            return existed;
        }
        finally { _treeLock.Release(); }
    }

    public Task<(bool Found, TValue Value)> TryGetAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ct.ThrowIfCancellationRequested();
        bool found = TryGetReadOnly(key, out var value);
        return Task.FromResult((found, value!));
    }

    public Task<bool> ContainsKeyAsync(TKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryGetReadOnly(key, out _));
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        await _treeLock.WaitAsync(ct);
        try
        {
            if (_cachedCount >= 0) return _cachedCount;
            FlushAll();
            int count = 0;
            long handle = _firstLeafHandle;
            while (handle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
            {
                var leaf = GetLeaf(handle);
                count += leaf.VisibleCount;
                handle = leaf.NextHandle;
            }
            _cachedCount = count;
            return _cachedCount;
        }
        finally { _treeLock.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        await _treeLock.WaitAsync(ct);
        try
        {
            _cache.Clear();
            long leafHandle = await _asyncStore.AllocateHandleAsync(ct);
            var leaf = new LeafNode<TKey, TValue>(_comparer) { IsDirty = true };
            _cache.Set(leafHandle, leaf);
            WriteRootSnapshot(leafHandle, 1);
            _firstLeafHandle = leafHandle;
            _lastLeafHandle = leafHandle;
            _cachedCount = 0;

            if (_bloomFilter != null)
            {
                _bloomFilter.Reset();
                _bloomFilterHandle = -1;
            }

            await SaveHeaderAsync(ct);
            if (_largeValueStore is IAsyncBlockStore asyncLargeValue)
                await asyncLargeValue.CommitAsync(ct);
            else
                _largeValueStore?.Commit();
            await _asyncStore.CommitAsync(ct);
        }
        finally { _treeLock.Release(); }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ForwardAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesForward(_firstLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in ForwardYield(handles, default!, default!, hasFrom: false, hasTo: false))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ForwardAsync(TKey from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesForward(_firstLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in ForwardYield(handles, from, default!, hasFrom: true, hasTo: false))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ForwardAsync(TKey from, TKey to,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesForward(_firstLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in ForwardYield(handles, from, to, hasFrom: true, hasTo: true))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> BackwardAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesBackward(_lastLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in BackwardYield(handles, default!, default!, hasFrom: false, hasTo: false))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> BackwardAsync(TKey from,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesBackward(_lastLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in BackwardYield(handles, from, default!, hasFrom: true, hasTo: false))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> BackwardAsync(TKey from, TKey to,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<long> handles;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAll();
            handles = CollectLeafHandlesBackward(_lastLeafHandle);
        }
        finally { _treeLock.Release(); }

        foreach (var kv in BackwardYield(handles, from, to, hasFrom: true, hasTo: true))
        {
            ct.ThrowIfCancellationRequested();
            yield return kv;
        }
    }

    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        await _treeLock.WaitAsync(ct);
        try { await CommitCoreAsync(ct); }
        finally { _treeLock.Release(); }
    }

    public async ValueTask FlushAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        if (_depth == 1) return;
        await _treeLock.WaitAsync(ct);
        try
        {
            FlushAllAt(_rootHandle, _depth);
            TryCollapseRoot();
        }
        finally { _treeLock.Release(); }
    }

    public async ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (!_disposed)
        {
            await _treeLock.WaitAsync(ct);
            try
            {
                try
                {
                    FlushAll();
                    await CommitCoreAsync(ct);
                }
                finally
                {
                    _backgroundGc?.Dispose();
                    _disposed = true;
                    await _asyncStore.CloseAsync(ct);
                }
            }
            finally { _treeLock.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        GC.SuppressFinalize(this);
    }

    // ── Commit & Dispose ─────────────────────────────────────────────────────

    /// <summary>
    /// Schreibt alle ausstehenden Änderungen atomar auf den Store.
    /// Der Puffer bleibt bestehen (kein implizites Flush).
    /// </summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        _treeLock.Wait();
        try
        {
            CommitCore();
        }
        finally { _treeLock.Release(); }
    }

    private void CommitCore()
    {
        long start = Stopwatch.GetTimestamp();
        SaveDirtyNodes();
        if (_bloomFilter?.IsDirty == true)
            SaveBloomFilter();
        SaveHeader();
        _largeValueStore?.Commit();
        _store.Commit();
        _telemetry.RecordTimer("commit_latency", ElapsedMicroseconds(start));
    }

    private async Task CommitCoreAsync(CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        await SaveDirtyNodesAsync(ct);
        if (_bloomFilter?.IsDirty == true)
            SaveBloomFilter();
        await SaveHeaderAsync(ct);
        if (_largeValueStore is IAsyncBlockStore asyncLargeValue)
            await asyncLargeValue.CommitAsync(ct);
        else
            _largeValueStore?.Commit();
        await _asyncStore.CommitAsync(ct);
        _telemetry.RecordTimer("commit_latency", ElapsedMicroseconds(start));
    }

    /// <summary>Leert alle Puffer, speichert und schließt.</summary>
    public void Close()
    {
        if (!_disposed)
        {
            _treeLock.Wait();
            try
            {
                try
                {
                    FlushAll();
                    CommitCore();
                }
                finally
                {
                    _backgroundGc?.Dispose();
                    _disposed = true;
                    _store.Close();
                }
            }
            finally { _treeLock.Release(); }
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    // ── Waterfall Sink ───────────────────────────────────────────────────────

    /// <summary>
    /// Kernmechanismus: Propagiert den Root-Puffer nach unten.
    /// Nur übervolle Kind-Puffer werden weiter kaskadiert ("Wasserfall").
    /// Wird von ApplyOperation aufgerufen, während der Root exklusiv gelatcht ist.
    /// </summary>
    private void Sink()
    {
        long start = Stopwatch.GetTimestamp();
        var root = GetInternal(_rootHandle);
        // Root ist bereits exklusiv gelatcht durch den Aufrufer (ApplyOperation).
        DistributeBuffer(root, _rootHandle, _depth - 1);

        // Root-Überlauf → neuer Root
        if (root.Branches.Count > MaxBranches)
            SplitRootInternal(root);

        // Root-Unterlauf (Kinder durch Merges verschwunden) → Tiefe reduzieren
        TryCollapseRoot();

        _telemetry.IncrementCounter("sink_ops");
        _telemetry.RecordTimer("sink_latency", ElapsedMicroseconds(start));
    }

    /// <summary>
    /// Verteilt den Puffer von <paramref name="node"/> auf seine Kinder.
    /// Kaskadiert rekursiv, wenn ein Kind-Puffer übervoll wird.
    /// Der Aufrufer muss <paramref name="node"/> exklusiv gelatcht halten.
    /// </summary>
    private void DistributeBuffer(InternalNode<TKey, TValue> node, long nodeHandle, int depth)
    {
        if (node.Buffer.Count == 0)
            return;

        var ops = node.Buffer.All.ToList();
        node.Buffer.Clear();
        node.IsDirty = true;

        var groups = GroupOpsByBranchHandle(ops, node);

        foreach (var (origHandle, groupOps) in groups)
        {
            if (depth == 1)
            {
                long childHandle = origHandle;
                if (!node.Branches.Any(b => b.ChildHandle == childHandle))
                {
                    if (groupOps.Count == 0) continue;
                    int redirectIdx = node.FindBranchIndex(groupOps[0].Key, _comparer);
                    childHandle = node.Branches[redirectIdx].ChildHandle;
                }

                var leaf = GetLeaf(childHandle);
                var leafNode = (INode)leaf;
                leafNode.EnterExclusive();
                try
                {
                    leaf.Apply(groupOps);

                    int curIdx = node.Branches.FindIndex(b => b.ChildHandle == childHandle);
                    if (curIdx < 0) continue;

                    if (leaf.Count > MaxLeafRecords)
                        SplitLeaf(childHandle, leaf, node, curIdx);
                    else if (leaf.Count < ActualMinLeafRecords && node.Branches.Count > 1)
                        RebalanceLeafUnderflow(childHandle, leaf, node, curIdx);
                }
                finally { leafNode.ExitLatch(); }
            }
            else
            {
                long childHandle = origHandle;
                if (!node.Branches.Any(b => b.ChildHandle == childHandle))
                {
                    if (groupOps.Count == 0) continue;
                    int redirectIdx = node.FindBranchIndex(groupOps[0].Key, _comparer);
                    childHandle = node.Branches[redirectIdx].ChildHandle;
                }

                var child = GetInternal(childHandle);
                var childNode = (INode)child;
                childNode.EnterExclusive();
                try
                {
                    child.Buffer.AddRange(groupOps);
                    child.IsDirty = true;

                    if (child.Buffer.Count >= InternalBufferThreshold)
                        DistributeBuffer(child, childHandle, depth - 1);

                    int curIdx = node.Branches.FindIndex(b => b.ChildHandle == childHandle);
                    if (curIdx < 0) continue;

                    if (child.Branches.Count > MaxBranches)
                        SplitInternalChild(childHandle, child, node, curIdx);
                    else if (child.Branches.Count < ActualMinBranches && node.Branches.Count > 1)
                        RebalanceInternalUnderflow(childHandle, child, node, curIdx);
                }
                finally { childNode.ExitLatch(); }
            }
        }
    }

    /// <summary>
    /// Gruppert Operationen nach dem Handle des zuständigen Kindknotens.
    /// Gibt geordnete Liste von (ChildHandle, Ops) zurück.
    /// Im Gegensatz zu einer Index-basierten Gruppierung bleibt das Handle
    /// auch dann korrekt, wenn Splits zwischendurch Branches verschieben.
    /// </summary>
    private List<(long ChildHandle, List<Operation<TKey, TValue>> Ops)> GroupOpsByBranchHandle(
        List<Operation<TKey, TValue>> ops,
        InternalNode<TKey, TValue> node)
    {
        var dict  = new Dictionary<long, List<Operation<TKey, TValue>>>();
        var order = new List<long>();

        foreach (var op in ops)
        {
            int  idx    = node.FindBranchIndex(op.Key, _comparer);
            long handle = node.Branches[idx].ChildHandle;
            if (!dict.TryGetValue(handle, out var list))
            {
                dict[handle] = list = [];
                order.Add(handle);
            }
            list.Add(op);
        }

        return order.Select(h => (h, dict[h])).ToList();
    }

    // ── Splits ───────────────────────────────────────────────────────────────

    /// <summary>Root war ein Blatt und ist übergelaufen → neuen Root anlegen.</summary>
    private void SplitRootLeaf(LeafNode<TKey, TValue> oldRoot)
    {
        _telemetry.IncrementCounter("split_ops");
        long rightHandle = _store.AllocateHandle();
        var  rightLeaf   = oldRoot.Split(rightHandle, out TKey splitKey);

        long newRootHandle = _store.AllocateHandle();
        var  newRoot       = new InternalNode<TKey, TValue>(_comparer) { IsDirty = true };

        newRoot.Branches.Add(new InternalNode<TKey, TValue>.Branch(default, _rootHandle));
        newRoot.Branches.Add(new InternalNode<TKey, TValue>.Branch(splitKey, rightHandle));

        rightLeaf.PrevHandle = _rootHandle;

        _cache.Set(rightHandle, rightLeaf);
        _cache.Set(newRootHandle, newRoot);

        WriteRootSnapshot(newRootHandle, 2);
        _lastLeafHandle = rightHandle;
    }

    /// <summary>Ein Blatt ist übergelaufen; aktualisiert den Elternknoten.</summary>
    private void SplitLeaf(long leafHandle, LeafNode<TKey, TValue> leaf,
                           InternalNode<TKey, TValue> parent, int branchIdx)
    {
        _telemetry.IncrementCounter("split_ops");
        long rightHandle = _store.AllocateHandle();
        var  rightLeaf   = leaf.Split(rightHandle, out TKey splitKey);

        rightLeaf.PrevHandle = leafHandle;

        if (rightLeaf.NextHandle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
        {
            var nextLeaf = GetLeaf(rightLeaf.NextHandle);
            var nextNode = (INode)nextLeaf;
            nextNode.EnterExclusive();
            try { nextLeaf.PrevHandle = rightHandle; }
            finally { nextNode.ExitLatch(); }
        }

        if (rightLeaf.NextHandle == LeafNode<TKey, TValue>.NO_NEIGHBOUR)
            _lastLeafHandle = rightHandle;
        _cache.Set(rightHandle, rightLeaf);

        parent.Branches.Insert(branchIdx + 1,
            new InternalNode<TKey, TValue>.Branch(splitKey, rightHandle));
        parent.IsDirty = true;
    }

    /// <summary>Ein interner Kind-Knoten ist übergelaufen; splittet und aktualisiert Parent.</summary>
    private void SplitInternalChild(long childHandle, InternalNode<TKey, TValue> child,
                                    InternalNode<TKey, TValue> parent, int branchIdx)
    {
        _telemetry.IncrementCounter("split_ops");
        long rightHandle = _store.AllocateHandle();
        var  rightNode   = new InternalNode<TKey, TValue>(_comparer) { IsDirty = true };

        int  half      = child.Branches.Count / 2;
        TKey splitKey  = child.Branches[half].SplitKey!;

        // Rechte Hälfte umhängen
        rightNode.Branches.AddRange(child.Branches.Skip(half));
        child.Branches.RemoveRange(half, child.Branches.Count - half);
        child.IsDirty = true;

        _cache.Set(rightHandle, rightNode);

        parent.Branches.Insert(branchIdx + 1,
            new InternalNode<TKey, TValue>.Branch(splitKey, rightHandle));
        parent.IsDirty = true;
    }

    /// <summary>Root-interner-Knoten ist übergelaufen → Tiefe erhöhen.</summary>
    private void SplitRootInternal(InternalNode<TKey, TValue> oldRoot)
    {
        _telemetry.IncrementCounter("split_ops");
        long oldRootHandle = _rootHandle;

        long leftHandle  = _store.AllocateHandle();
        long rightHandle = _store.AllocateHandle();

        var leftNode  = new InternalNode<TKey, TValue>(_comparer) { IsDirty = true };
        var rightNode = new InternalNode<TKey, TValue>(_comparer) { IsDirty = true };

        int  half     = oldRoot.Branches.Count / 2;
        TKey splitKey = oldRoot.Branches[half].SplitKey!;

        leftNode.Branches.AddRange(oldRoot.Branches.Take(half));
        rightNode.Branches.AddRange(oldRoot.Branches.Skip(half));

        _cache.Set(leftHandle, leftNode);
        _cache.Set(rightHandle, rightNode);

        oldRoot.Branches.Clear();
        oldRoot.Branches.Add(new InternalNode<TKey, TValue>.Branch(default, leftHandle));
        oldRoot.Branches.Add(new InternalNode<TKey, TValue>.Branch(splitKey, rightHandle));
        oldRoot.Buffer.Clear();
        oldRoot.IsDirty = true;

        WriteRootSnapshot(oldRootHandle, _depth + 1);
    }

    // ── Merge & Rebalance ────────────────────────────────────────────────────

    /// <summary>
    /// Behandelt Blatt-Unterlauf nach dem Anwenden von Operationen in DistributeBuffer.
    /// Während der Puffer-Verteilung werden ausschließlich Merges durchgeführt, da
    /// gestohlene Keys noch ausstehende Ops in der laufenden Batch haben könnten.
    /// Nach vollständigem Flush (FlushAll) können Steals sicher durchgeführt werden.
    /// </summary>
    private void RebalanceLeafUnderflow(long leafHandle, LeafNode<TKey, TValue> leaf,
                                        InternalNode<TKey, TValue> parent, int branchIdx)
    {
        if (branchIdx + 1 < parent.Branches.Count)
        {
            long rightHandle = parent.Branches[branchIdx + 1].ChildHandle;
            var  right       = GetLeaf(rightHandle);
            var  rightNode   = (INode)right;
            rightNode.EnterExclusive();
            try
            {
                MergeLeaves(leafHandle, leaf, rightHandle, right, parent, branchIdx + 1);
                if (leaf.Count > MaxLeafRecords)
                {
                    int leftIdx = parent.Branches.FindIndex(b => b.ChildHandle == leafHandle);
                    if (leftIdx >= 0) SplitLeaf(leafHandle, leaf, parent, leftIdx);
                }
            }
            finally { rightNode.ExitLatch(); }
            return;
        }

        if (branchIdx > 0)
        {
            long leftHandle = parent.Branches[branchIdx - 1].ChildHandle;
            var  left       = GetLeaf(leftHandle);
            var  leftNode   = (INode)left;
            leftNode.EnterExclusive();
            try
            {
                MergeLeaves(leftHandle, left, leafHandle, leaf, parent, branchIdx);
                if (left.Count > MaxLeafRecords)
                {
                    int leftIdx = parent.Branches.FindIndex(b => b.ChildHandle == leftHandle);
                    if (leftIdx >= 0) SplitLeaf(leftHandle, left, parent, leftIdx);
                }
            }
            finally { leftNode.ExitLatch(); }
        }
    }

    // ── Steal-Operationen (nur nach vollständigem FlushAll korrekt) ──────────
    //
    // Stealing während DistributeBuffer ist unsicher: gestohlene Keys können
    // ausstehende Delete-Ops in der laufenden Batch des Partner-Asts haben,
    // die dann am falschen Knoten angewendet werden.
    // TODO: Stealing in einem separaten Post-FlushAll-Rebalance-Pass implementieren.

    /// <summary>Verschiebt (surplus/2) Keys vom rechten Geschwister ans Ende von <paramref name="leaf"/>.</summary>
    private void StealFromRightLeaf(LeafNode<TKey, TValue> leaf, long leafHandle,
                                    LeafNode<TKey, TValue> right, long rightHandle,
                                    InternalNode<TKey, TValue> parent, int rightBranchIdx)
    {
        int steal = Math.Max(1, (right.Count - ActualMinLeafRecords) / 2);

        for (int i = 0; i < steal; i++)
        {
            leaf.Data.Add(right.Data.Keys[0], right.Data.Values[0]);
            right.Data.RemoveAt(0);
        }

        parent.Branches[rightBranchIdx] = parent.Branches[rightBranchIdx] with
            { SplitKey = right.Data.Keys[0] };

        leaf.IsDirty   = true;
        right.IsDirty  = true;
        parent.IsDirty = true;
    }

    /// <summary>Verschiebt (surplus/2) Keys vom linken Geschwister an den Anfang von <paramref name="leaf"/>.</summary>
    private void StealFromLeftLeaf(LeafNode<TKey, TValue> leaf, long leafHandle,
                                   LeafNode<TKey, TValue> left, long leftHandle,
                                   InternalNode<TKey, TValue> parent, int branchIdx)
    {
        int steal    = Math.Max(1, (left.Count - ActualMinLeafRecords) / 2);
        int startIdx = left.Data.Count - steal;

        var stolen = new (TKey K, VersionedValue<TValue> V)[steal];
        for (int i = 0; i < steal; i++)
            stolen[i] = (left.Data.Keys[startIdx + i], left.Data.Values[startIdx + i]);
        for (int i = left.Data.Count - 1; i >= startIdx; i--)
            left.Data.RemoveAt(i);

        foreach (var (k, v) in stolen)
            leaf.Data.Add(k, v);

        parent.Branches[branchIdx] = parent.Branches[branchIdx] with
            { SplitKey = leaf.Data.Keys[0] };

        leaf.IsDirty   = true;
        left.IsDirty   = true;
        parent.IsDirty = true;
    }

    /// <summary>
    /// Führt <paramref name="right"/> in <paramref name="left"/> zusammen.
    /// Entfernt den right-Ast aus <paramref name="parent"/> und bereinigt die verkettete Liste.
    /// </summary>
    private void MergeLeaves(long leftHandle,  LeafNode<TKey, TValue> left,
                             long rightHandle, LeafNode<TKey, TValue> right,
                             InternalNode<TKey, TValue> parent, int rightBranchIdx)
    {
        _telemetry.IncrementCounter("merge_ops");
        left.MergeFrom(right);

        if (right.NextHandle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
        {
            var afterRight = GetLeaf(right.NextHandle);
            var afterNode  = (INode)afterRight;
            afterNode.EnterExclusive();
            try { afterRight.PrevHandle = leftHandle; }
            finally { afterNode.ExitLatch(); }
        }
        else
        {
            _lastLeafHandle = leftHandle;
        }

        _cache.Remove(rightHandle);
        parent.Branches.RemoveAt(rightBranchIdx);
        parent.IsDirty = true;
    }

    /// <summary>
    /// Behandelt Unterlauf eines internen Knotens: merge-only (kein Steal),
    /// aus denselben Gründen wie bei RebalanceLeafUnderflow.
    /// </summary>
    private void RebalanceInternalUnderflow(long nodeHandle, InternalNode<TKey, TValue> node,
                                            InternalNode<TKey, TValue> parent, int branchIdx)
    {
        if (branchIdx + 1 < parent.Branches.Count)
        {
            long rightHandle = parent.Branches[branchIdx + 1].ChildHandle;
            var  right       = GetInternal(rightHandle);
            var  rightNode   = (INode)right;
            rightNode.EnterExclusive();
            try
            {
                MergeInternalNodes(nodeHandle, node, rightHandle, right, parent, branchIdx + 1);
                if (node.Branches.Count > MaxBranches)
                {
                    int leftIdx = parent.Branches.FindIndex(b => b.ChildHandle == nodeHandle);
                    if (leftIdx >= 0) SplitInternalChild(nodeHandle, node, parent, leftIdx);
                }
            }
            finally { rightNode.ExitLatch(); }
            return;
        }

        if (branchIdx > 0)
        {
            long leftHandle = parent.Branches[branchIdx - 1].ChildHandle;
            var  left       = GetInternal(leftHandle);
            var  leftNode   = (INode)left;
            leftNode.EnterExclusive();
            try
            {
                MergeInternalNodes(leftHandle, left, nodeHandle, node, parent, branchIdx);
                if (left.Branches.Count > MaxBranches)
                {
                    int leftIdx = parent.Branches.FindIndex(b => b.ChildHandle == leftHandle);
                    if (leftIdx >= 0) SplitInternalChild(leftHandle, left, parent, leftIdx);
                }
            }
            finally { leftNode.ExitLatch(); }
        }
    }

    /// <summary>
    /// Stiehlt den linkesten Ast von <paramref name="right"/> und hängt ihn als
    /// rechtesten Ast an <paramref name="node"/> an.
    /// Invariante der Parent-SplitKeys wird korrigiert.
    /// </summary>
    private void StealFromRightInternal(InternalNode<TKey, TValue> node,  long nodeHandle,
                                        InternalNode<TKey, TValue> right, long rightHandle,
                                        InternalNode<TKey, TValue> parent, int rightBranchIdx)
    {
        // Der Parent-SplitKey für right = Trennschlüssel zwischen node und right
        TKey parentSplitRight = parent.Branches[rightBranchIdx].SplitKey!;

        // Neuer Parent-SplitKey für right = ehemals right.Branches[1].SplitKey
        TKey newParentSplitRight = right.Branches[1].SplitKey!;

        // right.Branches[0] (kein SplitKey) zu node hinzufügen: bekommt parentSplitRight als Grenze
        node.Branches.Add(new InternalNode<TKey, TValue>.Branch(parentSplitRight,
                                                                  right.Branches[0].ChildHandle));

        // right.Branches[0] entfernen; ehemaliger [1] wird neuer [0] (SplitKey → null)
        right.Branches.RemoveAt(0);
        right.Branches[0] = right.Branches[0] with { SplitKey = default };

        // Parent-SplitKey für right aktualisieren
        parent.Branches[rightBranchIdx] = parent.Branches[rightBranchIdx] with
            { SplitKey = newParentSplitRight };

        node.IsDirty   = true;
        right.IsDirty  = true;
        parent.IsDirty = true;
    }

    /// <summary>
    /// Stiehlt den rechtesten Ast von <paramref name="left"/> und hängt ihn als
    /// linkesten Ast an <paramref name="node"/> an.
    /// </summary>
    private void StealFromLeftInternal(InternalNode<TKey, TValue> node,  long nodeHandle,
                                       InternalNode<TKey, TValue> left,  long leftHandle,
                                       InternalNode<TKey, TValue> parent, int branchIdx)
    {
        // Aktueller Parent-SplitKey für node (= untere Grenze von node's Subtree)
        TKey oldNodeSplitKey = parent.Branches[branchIdx].SplitKey!;

        // Der gestohlene Ast (= linkster Child von node nach Steal)
        var stolen = left.Branches[^1];

        // node.Branches[0] bekommt oldNodeSplitKey als Trennschlüssel
        node.Branches[0] = node.Branches[0] with { SplitKey = oldNodeSplitKey };
        // Gestohlener Ast vorne einfügen (ohne SplitKey = linkster)
        node.Branches.Insert(0, stolen with { SplitKey = default });

        // Von left entfernen
        left.Branches.RemoveAt(left.Branches.Count - 1);

        // Parent-SplitKey für node = SplitKey des gestohlenen Asts
        parent.Branches[branchIdx] = parent.Branches[branchIdx] with
            { SplitKey = stolen.SplitKey };

        node.IsDirty   = true;
        left.IsDirty   = true;
        parent.IsDirty = true;
    }

    /// <summary>
    /// Führt <paramref name="right"/> in <paramref name="left"/> zusammen.
    /// Der Parent-SplitKey für right wird als Trennschlüssel für right.Branches[0] verwendet.
    /// Puffer-Operationen aus right werden in left übernommen.
    /// </summary>
    private void MergeInternalNodes(long leftHandle,  InternalNode<TKey, TValue> left,
                                    long rightHandle, InternalNode<TKey, TValue> right,
                                    InternalNode<TKey, TValue> parent, int rightBranchIdx)
    {
        _telemetry.IncrementCounter("merge_ops");
        // Trennschlüssel im Parent (trennt left von right)
        TKey separator = parent.Branches[rightBranchIdx].SplitKey!;

        // right.Branches[0] hatte SplitKey=null; bekommt nun den Separator
        right.Branches[0] = right.Branches[0] with { SplitKey = separator };

        // Puffer-Ops von right in left übernehmen, damit nichts verloren geht
        left.Buffer.AddRange(right.Buffer.All);

        // Alle Äste von right an left anhängen
        left.Branches.AddRange(right.Branches);
        left.IsDirty = true;

        // right aus Cache entfernen
        _cache.Remove(rightHandle);

        // Ast für right aus Parent entfernen
        parent.Branches.RemoveAt(rightBranchIdx);
        parent.IsDirty = true;
    }

    /// <summary>
    /// Prüft, ob der Root-Knoten auf ein einziges Kind geschrumpft ist,
    /// und kollabiert die Tiefe des Baums entsprechend.
    /// Wird rekursiv aufgerufen bis Root mindestens 2 Kinder hat oder Root ein Blatt ist.
    /// </summary>
    private void TryCollapseRoot()
    {
        _telemetry.IncrementCounter("merge_ops");
        while (true)
        {
            long handle = Volatile.Read(ref _rootHandle);
            int  depth  = Volatile.Read(ref _depth);
            if (depth <= 1) break;

            var root = GetInternal(handle);
            var rootNode = (INode)root;
            rootNode.EnterExclusive();
            try
            {
                if (root.Branches.Count != 1) break;

                long newRootHandle = root.Branches[0].ChildHandle;
                _cache.Remove(handle);
                WriteRootSnapshot(newRootHandle, depth - 1);
            }
            finally { rootNode.ExitLatch(); }
        }
    }

    // ── Path-Flush für TryGet ─────────────────────────────────────────────────
    /// Leert nur den Pfad von Root zum Blatt für einen bestimmten Key.
    /// Erheblich billiger als FlushAll() für Einzelabfragen.
    /// </summary>
    private void FlushPath(TKey key)
    {
        if (_depth == 1) return;
        long start = Stopwatch.GetTimestamp();
        FlushPathAt(_rootHandle, key, _depth);
        _telemetry.IncrementCounter("flushpath_ops");
        _telemetry.RecordTimer("flushpath_latency", ElapsedMicroseconds(start));
    }

    private void FlushPathAt(long handle, TKey key, int depth)
    {
        if (depth == 0) return;

        var node = LoadNode(handle);
        if (node is not InternalNode<TKey, TValue> iNode) return;

        var inode = (INode)iNode;
        inode.EnterExclusive();
        try
        {
            var ops = iNode.Buffer.ExtractRange(key, key);
            if (ops.Count > 0)
            {
                iNode.IsDirty = true;
                int childIdx = iNode.FindBranchIndex(key, _comparer);
                long childHandle = iNode.Branches[childIdx].ChildHandle;
                var childNode = LoadNode(childHandle);

                if (childNode is LeafNode<TKey, TValue> leaf)
                {
                    var leafNode = (INode)leaf;
                    leafNode.EnterExclusive();
                    try { leaf.Apply(ops); }
                    finally { leafNode.ExitLatch(); }
                }
                else
                {
                    var childInode = (INode)childNode;
                    childInode.EnterExclusive();
                    try { ((InternalNode<TKey, TValue>)childNode).Buffer.AddRange(ops); }
                    finally { childInode.ExitLatch(); }
                }
            }

            int idx = iNode.FindBranchIndex(key, _comparer);
            FlushPathAt(iNode.Branches[idx].ChildHandle, key, depth - 1);
        }
        finally { inode.ExitLatch(); }
    }

    // ── FlushAll ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Leert alle Puffer im gesamten Baum (Top-Down).
    /// Wird vor range-Iterationen aufgerufen.
    /// </summary>
    public void FlushAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        if (_depth == 1) return;
        long start = Stopwatch.GetTimestamp();
        FlushAllAt(_rootHandle, _depth);
        TryCollapseRoot();
        _telemetry.IncrementCounter("flushall_ops");
        _telemetry.RecordTimer("flushall_latency", ElapsedMicroseconds(start));
    }

    private void FlushAllAt(long handle, int depth)
    {
        var node = LoadNode(handle);
        if (node is not InternalNode<TKey, TValue> iNode) return;

        var inode = (INode)iNode;
        inode.EnterExclusive();
        try
        {
            DistributeBuffer(iNode, handle, depth - 1);
            foreach (var branch in iNode.Branches.ToList())
                FlushAllAt(branch.ChildHandle, depth - 1);
        }
        finally { inode.ExitLatch(); }
    }

    // ── Snapshot-Helfer ─────────────────────────────────────────────────────

    private List<long> CollectLeafHandlesForward(long startHandle)
    {
        var handles = new List<long>();
        long handle = startHandle;
        while (handle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
        {
            handles.Add(handle);
            var leaf = GetLeaf(handle);
            handle = leaf.NextHandle;
        }
        return handles;
    }

    private List<long> CollectLeafHandlesBackward(long startHandle)
    {
        var handles = new List<long>();
        long handle = startHandle;
        while (handle != LeafNode<TKey, TValue>.NO_NEIGHBOUR)
        {
            handles.Add(handle);
            var leaf = GetLeaf(handle);
            handle = leaf.PrevHandle;
        }
        return handles;
    }

    // ── Iteratoren (lazy yield, kein _treeLock während der Enumeration) ────

    private IEnumerable<KeyValuePair<TKey, TValue>> ForwardYield(
        List<long> handles, TKey from, TKey to, bool hasFrom, bool hasTo)
    {
        foreach (var handle in handles)
        {
            List<KeyValuePair<TKey, TValue>> batch = [];
            bool stop = false;
            var leaf = GetLeaf(handle);
            var leafNode = (INode)leaf;
            leafNode.EnterShared();
            try
            {
                foreach (var kv in leaf.Data)
                {
                    if (hasFrom && _comparer.Compare(kv.Key, from) < 0)
                        continue;
                    if (hasTo && _comparer.Compare(kv.Key, to) > 0)
                    {
                        stop = true;
                        break;
                    }

                    if (!kv.Value.TryGetLatest(out var val))
                        continue; // Tombstone

                    batch.Add(new KeyValuePair<TKey, TValue>(kv.Key, val));
                }
            }
            finally { leafNode.ExitLatch(); }

            foreach (var kv in batch)
                yield return new KeyValuePair<TKey, TValue>(kv.Key, MaybeResolveLargeValue(kv.Value));

            if (stop)
                yield break;
        }
    }

    private IEnumerable<KeyValuePair<TKey, TValue>> BackwardYield(
        List<long> handles, TKey from, TKey to, bool hasFrom, bool hasTo)
    {
        foreach (var handle in handles)
        {
            List<KeyValuePair<TKey, TValue>> batch = [];
            bool stop = false;
            var leaf = GetLeaf(handle);
            var leafNode = (INode)leaf;
            leafNode.EnterShared();
            try
            {
                for (int i = leaf.Data.Count - 1; i >= 0; i--)
                {
                    var key = leaf.Data.Keys[i];
                    var vv  = leaf.Data.Values[i];

                    if (!vv.TryGetLatest(out var val))
                        continue; // Tombstone

                    if (hasFrom && _comparer.Compare(key, from) > 0)
                        continue;
                    if (hasTo && _comparer.Compare(key, to) < 0)
                    {
                        stop = true;
                        break;
                    }

                    batch.Add(new KeyValuePair<TKey, TValue>(key, val));
                }
            }
            finally { leafNode.ExitLatch(); }

            foreach (var kv in batch)
                yield return new KeyValuePair<TKey, TValue>(kv.Key, MaybeResolveLargeValue(kv.Value));

            if (stop)
                yield break;
        }
    }

    // ── Blatt-Suche ───────────────────────────────────────────────────────────

    private LeafNode<TKey, TValue> FindLeaf(TKey key)
    {
        long handle = _rootHandle;
        while (true)
        {
            var node = LoadNode(handle);
            if (node is LeafNode<TKey, TValue> leaf)
                return leaf;

            var iNode = (InternalNode<TKey, TValue>)node;
            int idx   = iNode.FindBranchIndex(key, _comparer);
            handle    = iNode.Branches[idx].ChildHandle;
        }
    }

    // ── Node-Laden & Speichern ────────────────────────────────────────────────

    private object LoadNode(long handle)
    {
        if (_cache.TryGetValue(handle, out var cached))
            return cached!;

        var data   = _store.Read(handle);
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms);

        var type = (NodeType)r.ReadByte();
        object node = type switch
        {
            NodeType.Internal => InternalNode<TKey, TValue>.Deserialize(r, _comparer, _ser.ReadKey, _ser.ReadValue),
            NodeType.Leaf     => LeafNode<TKey, TValue>.Deserialize(r, _comparer, _ser.ReadKey, _ser.ReadValue),
            _                 => throw new InvalidDataException($"Unbekannter NodeType: {type}")
        };

        StoreNode(handle, (INode)node);
        return node;
    }

    private async Task<object> LoadNodeAsync(long handle, CancellationToken ct)
    {
        if (_cache.TryGetValue(handle, out var cached))
            return cached!;

        var data = await _asyncStore.ReadAsync(handle, ct);
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms);

        var type = (NodeType)r.ReadByte();
        object node = type switch
        {
            NodeType.Internal => InternalNode<TKey, TValue>.Deserialize(r, _comparer, _ser.ReadKey, _ser.ReadValue),
            NodeType.Leaf     => LeafNode<TKey, TValue>.Deserialize(r, _comparer, _ser.ReadKey, _ser.ReadValue),
            _                 => throw new InvalidDataException($"Unbekannter NodeType: {type}")
        };

        StoreNode(handle, (INode)node);
        return node;
    }

    private async Task SaveDirtyNodesAsync(CancellationToken ct)
    {
        foreach (var (handle, node) in _cache.AllNodes())
        {
            bool dirty = node switch
            {
                InternalNode<TKey, TValue> n => n.IsDirty,
                LeafNode<TKey, TValue>     n => n.IsDirty,
                _                           => false
            };

            if (!dirty) continue;

            var inode = (INode)node;
            inode.EnterShared();
            try
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(4096);
                int len;
                try
                {
                    using var ms = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
                    using var w = new BinaryWriter(ms, Encoding.Default, leaveOpen: true);

                    switch (node)
                    {
                        case InternalNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                        case LeafNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                    }

                    w.Flush();
                    len = (int)ms.Position;
                    await _asyncStore.WriteAsync(handle, rented, 0, len, ct);
                }
                catch (NotSupportedException)
                {
                    using var ms = new MemoryStream();
                    using var w = new BinaryWriter(ms);
                    switch (node)
                    {
                        case InternalNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                        case LeafNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                    }
                    w.Flush();
                    len = (int)ms.Length;
                    await _asyncStore.WriteAsync(handle, ms.GetBuffer(), 0, len, ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            finally { inode.ExitLatch(); }
        }
    }

    private async Task SaveHeaderAsync(CancellationToken ct)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            using var ms = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
            using var w = new BinaryWriter(ms, Encoding.Default, leaveOpen: true);
            w.Write(_rootHandle);
            w.Write(_firstLeafHandle);
            w.Write(_depth);
            w.Write(_lastLeafHandle);       // v2
            w.Write(_bloomFilterHandle);    // v3
            w.Flush();
            int len = (int)ms.Position;
            await _asyncStore.WriteAsync(HEADER_HANDLE, rented, 0, len, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void SaveDirtyNodes()
    {
        int persisted = 0;
        foreach (var (handle, node) in _cache.AllNodes())
        {
            bool dirty = node switch
            {
                InternalNode<TKey, TValue> n => n.IsDirty,
                LeafNode<TKey, TValue>     n => n.IsDirty,
                _                           => false
            };

            if (!dirty) continue;

            var inode = (INode)node;
            inode.EnterShared();
            try
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    using var ms = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
                    using var w = new BinaryWriter(ms, Encoding.Default, leaveOpen: true);

                    switch (node)
                    {
                        case InternalNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                        case LeafNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                    }

                    w.Flush();
                    int len = (int)ms.Position;
                    _store.Write(handle, rented, 0, len);
                    persisted++;
                }
                catch (NotSupportedException)
                {
                    using var ms = new MemoryStream();
                    using var w = new BinaryWriter(ms);
                    switch (node)
                    {
                        case InternalNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                        case LeafNode<TKey, TValue> n:
                            n.Serialize(w, _ser.WriteKey, _ser.WriteValue);
                            break;
                    }
                    w.Flush();
                    _store.Write(handle, ms.GetBuffer(), 0, (int)ms.Length);
                    persisted++;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            finally { inode.ExitLatch(); }
        }

        if (persisted > 0)
            _telemetry.IncrementCounter("nodes_persisted", persisted);
        _telemetry.RecordGauge("cache_size", _cache.Count);
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void SaveHeader()
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            using var ms = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
            using var w = new BinaryWriter(ms, Encoding.Default, leaveOpen: true);
            w.Write(_rootHandle);
            w.Write(_firstLeafHandle);
            w.Write(_depth);
            w.Write(_lastLeafHandle);       // v2
            w.Write(_bloomFilterHandle);    // v3
            w.Flush();
            int len = (int)ms.Position;
            _store.Write(HEADER_HANDLE, rented, 0, len);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void LoadHeader()
    {
        var data = _store.Read(HEADER_HANDLE);
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms);
        long rootHandle  = r.ReadInt64();
        _firstLeafHandle = r.ReadInt64();
        int depth        = r.ReadInt32();
        _lastLeafHandle  = ms.Position < ms.Length
            ? r.ReadInt64()
            : FindLastLeafHandle();
        _cachedCount     = -1;

        _bloomFilterHandle = ms.Position < ms.Length
            ? r.ReadInt64()
            : -1;

        if (_useBloomFilter && _bloomFilterHandle >= 0 && _store.Exists(_bloomFilterHandle))
        {
            LoadBloomFilter();
        }
        else if (_useBloomFilter)
        {
            _bloomFilter = new BloomFilter(_bloomFilterExpectedItems, _bloomFilterFpp);
        }

        WriteRootSnapshot(rootHandle, depth);
    }

    /// <summary>Einmaliger Walk zum letzten Blatt – nur beim Laden alter Header-Versionen.</summary>
    private long FindLastLeafHandle()
    {
        long h = _firstLeafHandle;
        while (true)
        {
            var leaf = GetLeaf(h);
            if (leaf.NextHandle == LeafNode<TKey, TValue>.NO_NEIGHBOUR) return h;
            h = leaf.NextHandle;
        }
    }

    // ── Bloom-Filter Hilfsmethoden ───────────────────────────────────────────

    private void LoadBloomFilter()
    {
        try
        {
            var data = _store.Read(_bloomFilterHandle);
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            int bitCount = r.ReadInt32();
            int hashCount = r.ReadInt32();
            int byteLen = r.ReadInt32();
            byte[] bits = r.ReadBytes(byteLen);
            _bloomFilter = new BloomFilter(bits, bitCount, hashCount);
        }
        catch
        {
            _logger.Log(Diagnostics.LogLevel.Warning, "Bloom-Filter korrupt – wird neu initialisiert.");
            _bloomFilter = new BloomFilter(_bloomFilterExpectedItems, _bloomFilterFpp);
            _bloomFilterHandle = -1;
        }
    }

    private void SaveBloomFilter()
    {
        if (_bloomFilter == null) return;

        if (_bloomFilterHandle < 0)
            _bloomFilterHandle = _store.AllocateHandle();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(_bloomFilter.BitCount);
        w.Write(_bloomFilter.HashCount);
        byte[] bits = _bloomFilter.GetBits();
        w.Write(bits.Length);
        w.Write(bits);
        w.Flush();
        var buf = ms.GetBuffer();
        _store.Write(_bloomFilterHandle, buf, 0, (int)ms.Length);
        _bloomFilter.MarkClean();
    }

    private void BloomFilterAdd(TKey key)
    {
        if (_bloomFilter == null) return;
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms);
        _ser.WriteKey(w, key);
        w.Flush();
        _bloomFilter.Add(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private bool BloomFilterMayContain(TKey key)
    {
        if (_bloomFilter == null) return true; // ohne Filter → Baum-Traversal nötig
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms);
        _ser.WriteKey(w, key);
        w.Flush();
        return _bloomFilter.MayContain(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    // ── Large-Value-Sidecar Hilfsmethoden ────────────────────────────────────

    private TValue MaybeInlineLargeValue(TValue value)
    {
        if (_largeValueStore == null || _ser.CreateLargeValueHandle == null) return value;

        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms);
        _ser.WriteValue(w, value);
        w.Flush();

        if (ms.Length > _largeValueThreshold)
        {
            long handle = _largeValueStore.AllocateHandle();
            var buf = ms.GetBuffer();
            _largeValueStore.Write(handle, buf, 0, (int)ms.Length);
            return _ser.CreateLargeValueHandle(handle);
        }

        return value;
    }

    private TValue MaybeResolveLargeValue(TValue value)
    {
        if (_largeValueStore == null || _ser.TryGetLargeValueHandle == null) return value;

        var handle = _ser.TryGetLargeValueHandle(value);
        if (!handle.HasValue) return value;

        var data = _largeValueStore.Read(handle.Value);
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        return _ser.ReadValue(r);
    }

    // ── Background GC ────────────────────────────────────────────────────────

    private BackgroundGC<TKey, TValue>? _backgroundGc;

    /// <summary>
    /// Optional callback invoked for every value pruned from a version chain.
    /// Receives (key, value).  Useful for sidecar GC (e.g. collecting orphaned BlobRefs).
    /// </summary>
    public Action<TKey, TValue>? OnValuePruned { get; set; }

    /// <summary>Startet den Hintergrund-GC-Thread (falls noch nicht aktiv).</summary>
    public void StartBackgroundGC(TimeSpan? interval = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(WTree<TKey, TValue>));
        if (_backgroundGc != null) return;
        _backgroundGc = new BackgroundGC<TKey, TValue>(this, _logger)
        {
            Interval = interval ?? TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Räumt alle gecachten Blattknoten auf (manueller Aufruf).</summary>
    public void PruneAllCachedLeaves()
    {
        ulong oldest = _txManager.OldestActiveSnapshot;
        var onPruned = OnValuePruned;
        foreach (var (_, node) in _cache.AllNodes())
        {
            if (node is not LeafNode<TKey, TValue> leaf) continue;

            var inode = (INode)leaf;
            inode.EnterExclusive();
            try { leaf.PruneOldVersions(oldest, onPruned); }
            finally { inode.ExitLatch(); }
        }
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────────────────

    private InternalNode<TKey, TValue> GetInternal(long handle) =>
        (InternalNode<TKey, TValue>)LoadNode(handle);

    private LeafNode<TKey, TValue> GetLeaf(long handle) =>
        (LeafNode<TKey, TValue>)LoadNode(handle);

    private async Task<InternalNode<TKey, TValue>> GetInternalAsync(long handle, CancellationToken ct) =>
        (InternalNode<TKey, TValue>)await LoadNodeAsync(handle, ct);

    private async Task<LeafNode<TKey, TValue>> GetLeafAsync(long handle, CancellationToken ct) =>
        (LeafNode<TKey, TValue>)await LoadNodeAsync(handle, ct);

    private async Task<LeafNode<TKey, TValue>> FindLeafAsync(TKey key, CancellationToken ct)
    {
        long handle = _rootHandle;
        int currentDepth = _depth;

        while (currentDepth > 1)
        {
            var node = await GetInternalAsync(handle, ct);
            int idx = node.FindBranchIndex(key, _comparer);
            handle = node.Branches[idx].ChildHandle;
            currentDepth--;
        }

        return await GetLeafAsync(handle, ct);
    }

    private async Task FlushPathAsync(TKey key, CancellationToken ct)
    {
        if (_depth == 1) return;
        await FlushPathAtAsync(_rootHandle, key, _depth, ct);
    }

    private async Task FlushPathAtAsync(long handle, TKey key, int depth, CancellationToken ct)
    {
        var node = await LoadNodeAsync(handle, ct);
        if (node is not InternalNode<TKey, TValue> iNode) return;

        var inode = (INode)iNode;
        inode.EnterExclusive();
        try
        {
            var ops = iNode.Buffer.ExtractRange(key, key);
            if (ops.Count > 0)
            {
                iNode.IsDirty = true;
                int childIdx = iNode.FindBranchIndex(key, _comparer);
                long childHandle = iNode.Branches[childIdx].ChildHandle;
                var childNode = await LoadNodeAsync(childHandle, ct);

                if (childNode is LeafNode<TKey, TValue> leaf)
                {
                    var leafNode = (INode)leaf;
                    leafNode.EnterExclusive();
                    try { leaf.Apply(ops); }
                    finally { leafNode.ExitLatch(); }
                }
                else
                {
                    var childInode = (INode)childNode;
                    childInode.EnterExclusive();
                    try { ((InternalNode<TKey, TValue>)childNode).Buffer.AddRange(ops); }
                    finally { childInode.ExitLatch(); }
                }
            }

            int idx = iNode.FindBranchIndex(key, _comparer);
            await FlushPathAtAsync(iNode.Branches[idx].ChildHandle, key, depth - 1, ct);
        }
        finally { inode.ExitLatch(); }
    }

    // ── Diagnose ──────────────────────────────────────────────────────────────

    /// <summary>Gibt einen kurzen Status-String zurück (für Debugging).</summary>
    public string GetStats()
    {
        var (handle, depth) = ReadRootSnapshot();
        return $"Depth={depth}, CachedNodes={_cache.Count}, " +
               $"RootHandle={handle}, FirstLeaf={_firstLeafHandle}, LastLeaf={_lastLeafHandle}, " +
               $"CachedCount={(_cachedCount >= 0 ? _cachedCount.ToString() : "stale")}";
    }
}
