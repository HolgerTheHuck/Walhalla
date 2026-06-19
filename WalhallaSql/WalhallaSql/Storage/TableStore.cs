using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Trees;
using System.Linq;
using System.Text;
using WalhallaSql.Core;
using WalhallaSql.Sql;
using WalhallaSql; // for DeferredWrite
using WalhallaSql.Statistics;

namespace WalhallaSql.Storage;

internal delegate object?[] RowDecoder(ReadOnlySpan<byte> encodedRow);

internal sealed class TableStore : IDisposable
{
    private const uint CatalogSentinel = 0xFFFFFFFF;
    private const uint IndexSentinel = 0xFFFFFFFE;
    private const uint StatsSentinel = 0xFFFFFFFD;

    private readonly WalhallaOptions _options;
    private readonly IKeyValueStore _dataStore;
    private readonly WalLog? _walLog;
    private readonly OdsPager? _odsPager;
    private readonly GroupCommitQueue? _groupCommit;
    private LruValueCache _rowCache;
    // Data lock (single tableId=0): serializes all row/index/scan operations against the
    // shared B+Tree / MemTable, which are not internally thread-safe. Step E.2 will
    // partition this when storage is sharded per table.
    internal readonly RowLockManager LockManager = new();
    // Catalog lock (Step C.0.E.1): protects in-memory _catalog dictionary reads against
    // catalog mutations (CREATE/DROP/ALTER/RENAME, NextRowId persistence). Lock ordering:
    // catalog BEFORE data. Catalog-write sites take both locks in this order; pure
    // catalog-read sites take only this lock so they never block on running DML.
    private readonly RowLockManager _catalogLockManager = new();

    private Dictionary<byte[], byte[]> _memDict;
    private List<byte[]> _memSortedKeys;
    private bool _memSortedKeysDirty;
    private readonly ConcurrentDictionary<RowKey, byte[]> _rowByKey = new();
    private const int RowByKeyMaxSize = 10_000;
    private readonly Dictionary<string, TableEntry> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private int _nextTableId = 1;
    private int _nextIndexId = 1;
    private long _nextTransactionId = 1;
    private readonly ConcurrentDictionary<int, BlobSidecarFile> _sidecars = new();
    private readonly ConcurrentQueue<(int, byte[])> _pendingOrphanRows = new();
    private readonly ConcurrentDictionary<int, List<BlobRef>> _orphanRefs = new();
    private readonly CancellationTokenSource _orphanCts = new();

    // Telemetry counters
    private long _orphanRowsProcessed;
    private long _orphanBlobsReclaimed;
    private long _orphanBytesReclaimed;
    internal bool IsInMemory => _options.StorageMode == StorageMode.InMemory;
    private bool IsMvccBPlusTree => _options.StorageMode == StorageMode.MvccBPlusTree;
    private bool UsesDirectStore => _walLog == null; // InMemory, MvccBPlusTree: no MemTable/WAL
    private bool UsesBlobSidecar => _options.EnableBlobSidecar && _options.StorageMode != StorageMode.InMemory;

    public TableStore(WalhallaOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.StorageMode == StorageMode.InMemory)
        {
            _dataStore = new InMemoryStore();
            _walLog = null;
            _odsPager = null;
        }
        else if (options.StorageMode == StorageMode.MvccBPlusTree)
        {
            _dataStore = new MvccBPlusTreeStore(options.OdsFilePath, options.OdsPageSizeBytes, options.PageCacheCapacity,
                walPath: options.WalFilePath,
                walSyncMode: (Walhalla.Storage.Core.Configuration.WalSyncMode)options.WalSyncMode);
            _odsPager = null;
            _walLog = null;
            _groupCommit = null;
        }
        else
        {
            _odsPager = new OdsPager(options.OdsFilePath, options.OdsPageSizeBytes, options.PageCacheCapacity);
            _dataStore = new BPlusTreeStore(new BPlusTree(_odsPager));
            _walLog = new WalLog(options.WalFilePath, options.WalSyncMode);
            if (options.WalSyncMode == WalSyncMode.Fsync)
                _groupCommit = new GroupCommitQueue(_walLog, coalesceMs: 0);
        }

        // Cache.
        _rowCache = new LruValueCache(options.CacheSizeBytes);

        // MemTable.
        _memDict = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
        _memSortedKeys = new List<byte[]>();

        // Recover: replay WAL → MemTable, then load catalog.
        Recover();
    }

    private void EnsureMemSortedKeys()
    {
        if (!_memSortedKeysDirty) return;
        _memSortedKeys.Clear();
        foreach (var key in _memDict.Keys)
            _memSortedKeys.Add(key);
        _memSortedKeys.Sort(ByteArrayComparer.Instance);
        _memSortedKeysDirty = false;
    }

    private int MemSortedKeysFindStart(byte[] fromInclusive)
    {
        EnsureMemSortedKeys();
        int lo = 0, hi = _memSortedKeys.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ByteArrayComparer.Instance.Compare(_memSortedKeys[mid], fromInclusive) < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo;
    }

    private void AppendToWal(long transactionId, IReadOnlyList<WalOperation> operations)
    {
        if (_groupCommit != null)
            _groupCommit.Enqueue(transactionId, operations);
        else if (_walLog != null)
            _walLog.AppendBatch(transactionId, operations);
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    public SqlTableDefinition? GetTableDefinition(string name)
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            if (_catalog.TryGetValue(name, out var entry))
                return entry.Definition;
            return null;
        }
    }

    public int GetTableId(string name)
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            return _catalog.TryGetValue(name, out var entry) ? entry.TableId : -1;
        }
    }

    internal TableEntry? GetEntry(string tableName)
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            return _catalog.TryGetValue(tableName, out var entry) ? entry : null;
        }
    }

    public long GetNextRowId(int tableId)
    {
        var entry = GetEntryForTable(tableId);
        return entry.NextRowId;
    }

    public void AdvanceNextRowId(int tableId)
    {
        // Catalog-write: mutates entry.NextRowId AND persists to catalog area of B+Tree.
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            var entry = GetEntryForTable(tableId);
            entry.NextRowId++;
            var catKey = BuildCatalogKey(entry.Definition.CollectionName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    public Dictionary<string, int> GetTableIndexIds(string tableName)
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            if (_catalog.TryGetValue(tableName, out var entry))
                return new Dictionary<string, int>(entry.IndexIds, StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public int GetIndexId(string tableName, string indexName)
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            if (_catalog.TryGetValue(tableName, out var entry)
                && entry.IndexIds.TryGetValue(indexName, out var indexId))
                return indexId;
            return -1;
        }
    }

    public IReadOnlyList<SqlTableDefinition> GetAllTables()
    {
        using (_catalogLockManager.TableReadLock(0))
        {
            return _catalog.Values.Select(e => e.Definition).ToArray();
        }
    }

    public void CreateTable(SqlTableDefinition table)
    {
        // Catalog-write: catalog lock first, then data lock (lock ordering).
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (_catalog.ContainsKey(table.CollectionName))
                throw new WalhallaException($"Table '{table.CollectionName}' already exists.");

            var tableId = AllocateTableId();
            var entry = new TableEntry
            {
                TableId = tableId,
                Definition = table,
                NextRowId = 1
            };

            // Allocate index IDs for any table-defined indexes.
            foreach (var idx in table.Indexes)
            {
                var indexId = AllocateIndexId();
                entry.IndexIds[idx.IndexName] = indexId;
            }

            _catalog[table.CollectionName] = entry;

            // Persist catalog entry to B+Tree.
            var catKey = BuildCatalogKey(table.CollectionName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);

            // Persist tableId counter.
            PersistNextTableId();
        }
    }

    public void DropTable(string name)
    {
        // Catalog-write: catalog lock first, then data lock (lock ordering).
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(name, out var entry))
                throw new WalhallaException($"Table '{name}' does not exist.");

            var tableId = entry.TableId;

            // Remove all rows from B+Tree + MemTable.
            var prefix = BuildTablePrefix((uint)tableId);
            var toExclusive = BuildTablePrefix((uint)(tableId + 1));

            // Delete from MemTable.
            var toRemove = new List<byte[]>();
            EnsureMemSortedKeys();
            foreach (var key in _memSortedKeys)
            {
                if (StartsWithPrefix(key, prefix))
                    toRemove.Add(key);
            }
            foreach (var key in toRemove)
                _memDict.Remove(key);
            _memSortedKeysDirty = true;

            // Delete from B+Tree.
            foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive).ToList())
                _dataStore.Delete(kv.Key);

            // Delete index entries for this table.
            DeleteAllIndexEntriesForTable(tableId);

            // Remove catalog entries.
            var catDelKey = BuildCatalogKey(name, CatalogProperty.Definition);
            _dataStore.Delete(catDelKey);

            _catalog.Remove(name);

            // Phase H.8: remove sidecar file for dropped table.
            if (_sidecars.TryRemove(tableId, out var sidecar))
            {
                sidecar.Dispose();
                try
                {
                    var dir = Path.GetDirectoryName(sidecar.FilePath);
                    if (dir != null && Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    public void TruncateTable(string name)
    {
        // Catalog-write: catalog lock first, then data lock (lock ordering).
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(name, out var entry))
                throw new WalhallaException($"Table '{name}' does not exist.");

            var tableId = entry.TableId;

            // Remove all rows from B+Tree + MemTable.
            var prefix = BuildTablePrefix((uint)tableId);
            var toExclusive = BuildTablePrefix((uint)(tableId + 1));

            // Delete from MemTable.
            var toRemove = new List<byte[]>();
            EnsureMemSortedKeys();
            foreach (var key in _memSortedKeys)
            {
                if (StartsWithPrefix(key, prefix))
                    toRemove.Add(key);
            }
            foreach (var key in toRemove)
                _memDict.Remove(key);
            _memSortedKeysDirty = true;

            // Delete from B+Tree.
            foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive).ToList())
                _dataStore.Delete(kv.Key);

            // Delete index entries for this table.
            DeleteAllIndexEntriesForTable(tableId);

            // Reset NextRowId.
            entry.NextRowId = 1;
            var catKey = BuildCatalogKey(name, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);

            // Phase H.8: truncate sidecar file (empty it).
            if (_sidecars.TryRemove(tableId, out var sidecar))
            {
                sidecar.Dispose();
                try
                {
                    var dir = Path.GetDirectoryName(sidecar.FilePath);
                    if (dir != null && Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    public void UpdateTableDefinition(string tableName, int tableId, SqlTableDefinition newDef)
    {
        // Catalog-write: catalog lock first, then data lock (lock ordering).
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(tableName, out var entry))
                throw new WalhallaException($"Table '{tableName}' does not exist.");
            entry.Definition = newDef;
            _catalog[tableName] = entry;

            var catKey = BuildCatalogKey(tableName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    public void RenameTable(string oldName, string newName)
    {
        // Catalog-write: catalog lock first, then data lock (lock ordering).
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(oldName, out var entry))
                throw new WalhallaException($"Table '{oldName}' does not exist.");
            if (_catalog.ContainsKey(newName))
                throw new WalhallaException($"Table '{newName}' already exists.");

            var oldDef = entry.Definition;
            entry.Definition = oldDef with { CollectionName = newName };
            _catalog[newName] = entry;

            // Remove old catalog entry
            _dataStore.Delete(BuildCatalogKey(oldName, CatalogProperty.Definition));
            _catalog.Remove(oldName);

            // Persist new catalog entry
            var catKey = BuildCatalogKey(newName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    // ── Row operations ───────────────────────────────────────────────────────

    public byte[]? GetRow(int tableId, long rowId)
    {
        // B.3: L0 struct-keyed cache — zero allocation on hit
        var rowKey = new RowKey(tableId, rowId);
        if (_rowByKey.TryGetValue(rowKey, out var hit))
            return hit;

        var key = BuildRowKey(tableId, rowId);

        using (LockManager.TableReadLock(0))
        {
            // Check cache (borrowed reference — caller decodes read-only).
            if (_rowCache.TryGetBorrowed(key, out var cached))
            {
                PopulateRowByKey(rowKey, cached!);
                return cached;
            }

            // Check MemTable.
            if (_memDict.TryGetValue(key, out var memValue))
            {
                _rowCache.Set(key, memValue);
                PopulateRowByKey(rowKey, memValue);
                return memValue;
            }

            // Check B+Tree.
            if (_dataStore.TryGet(key, out var treeValue))
            {
                _rowCache.Set(key, treeValue!);
                PopulateRowByKey(rowKey, treeValue!);
                return treeValue;
            }

            return null;
        }
    }

    public byte[]? GetRow(int tableId, long rowId, ITransaction<byte[], byte[]>? tx)
    {
        if (tx == null)
            return GetRow(tableId, rowId);

        var key = BuildRowKey(tableId, rowId);
        return tx.TryGet(key, out var value) ? value : null;
    }

    private void PopulateRowByKey(RowKey rowKey, byte[] value)
    {
        if (_rowByKey.Count >= RowByKeyMaxSize)
            _rowByKey.Clear();
        _rowByKey.TryAdd(rowKey, value);
    }

    public long InsertRow(int tableId, byte[] encodedRow)
    {
        using (LockManager.TableWriteLock(0))
        {
            var entry = GetEntryForTable(tableId);
            var rowId = entry.NextRowId++;
            var key = BuildRowKey(tableId, rowId);

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[] { new WalOperation(WalRecordType.Put, key, encodedRow) });
            }

            PutEntry(key, encodedRow);
            _rowCache.SetWeak(key, encodedRow);

            var catKey = BuildCatalogKey(entry.Definition.CollectionName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);

            return rowId;
        }
    }

    public void InsertRows(int tableId, IReadOnlyList<byte[]> encodedRows)
    {
        InsertRows(tableId, encodedRows, null);
    }

    /// <summary>Insert rows with index entries in a single WAL batch.</summary>
    public void InsertRows(int tableId, IReadOnlyList<byte[]> encodedRows,
        IReadOnlyList<(int IndexId, byte[] SortKey, int TableId, long RowId)>? indexEntries)
        => InsertRows(tableId, encodedRows, explicitRowIds: null, indexEntries);

    /// <summary>
    /// Insert rows with index entries in a single WAL batch. When <paramref name="explicitRowIds"/>
    /// is non-null, the supplied row ids are used verbatim as the storage row keys (SQLite-style
    /// INTEGER PRIMARY KEY alias semantics). Caller is responsible for ensuring values are unique
    /// across the batch; this method enforces uniqueness against already-persisted rows by
    /// throwing <see cref="WalhallaException"/> on the first collision.
    /// </summary>
    public void InsertRows(int tableId, IReadOnlyList<byte[]> encodedRows,
        long[]? explicitRowIds,
        IReadOnlyList<(int IndexId, byte[] SortKey, int TableId, long RowId)>? indexEntries)
    {
        if (encodedRows.Count == 0) return;
        if (explicitRowIds != null && explicitRowIds.Length != encodedRows.Count)
            throw new ArgumentException(
                "explicitRowIds length must match encodedRows.Count.",
                nameof(explicitRowIds));

        using (LockManager.TableWriteLock(0))
        {
            var entry = GetEntryForTable(tableId);
            var startRowId = entry.NextRowId;
            int totalEntries = encodedRows.Count + (indexEntries?.Count ?? 0);

            // Collect all (key, value) pairs for bulk insert.
            // Arrays are used instead of List<T> to avoid per-item container overhead
            // and growing backing arrays during large batches.
            var bulkEntries = new KeyValuePair<byte[], byte[]>[totalEntries];
            var rowKeys = new byte[encodedRows.Count][];
            byte[][]? indexKeys = indexEntries != null ? new byte[indexEntries.Count][] : null;
            long maxExplicitRowId = long.MinValue;
            int bulkIdx = 0;
            for (var i = 0; i < encodedRows.Count; i++)
            {
                long rowId;
                if (explicitRowIds != null)
                {
                    rowId = explicitRowIds[i];
                    if (rowId > maxExplicitRowId) maxExplicitRowId = rowId;
                }
                else
                {
                    rowId = startRowId + i;
                }
                var key = BuildRowKey(tableId, rowId);

                if (explicitRowIds != null)
                {
                    // PK collision pre-check (memtable + persisted store). The row cache may hold
                    // stale entries for deleted rows, so it is intentionally NOT consulted here.
                    if (_memDict.ContainsKey(key) || _dataStore.TryGet(key, out _))
                    {
                        throw new WalhallaConstraintException(
                            $"Duplicate primary key value for row id {rowId} in collection '{entry.Definition.CollectionName}'.", "23505");
                    }
                }

                rowKeys[i] = key;
                bulkEntries[bulkIdx++] = new KeyValuePair<byte[], byte[]>(key, encodedRows[i]);
                _rowCache.SetWeak(key, encodedRows[i]);
            }
            if (indexEntries != null)
            {
                for (int i = 0; i < indexEntries.Count; i++)
                {
                    var e = indexEntries[i];
                    var key = e.IndexId == -1
                        ? e.SortKey  // Already a full key built by IndexKeyCodec.BuildIndexEntryKey
                        : BuildIndexEntryKey(e.IndexId, e.SortKey, e.TableId, e.RowId);
                    indexKeys![i] = key;
                    bulkEntries[bulkIdx++] = new KeyValuePair<byte[], byte[]>(key, IndexEntryValue);
                }
            }

            // Single bulk insert via interface (O(1) per entry with Dictionary-backed store).
            if (UsesDirectStore)
                _dataStore.BulkUpsert(bulkEntries);
            else
                BulkMergeMemTable(bulkEntries);

            if (explicitRowIds != null)
            {
                // Advance NextRowId past the highest explicit value so future auto-rowid inserts
                // (e.g. into other tables sharing this entry, or fallback paths) do not collide.
                var advancedTo = maxExplicitRowId + 1;
                if (advancedTo > entry.NextRowId) entry.NextRowId = advancedTo;
            }
            else
            {
                entry.NextRowId = startRowId + encodedRows.Count;
            }

            // WAL logging (disk mode only). Reuses keys built above.
            if (_walLog != null)
            {
                var operations = new WalOperation[totalEntries];
                var idx = 0;
                for (var i = 0; i < rowKeys.Length; i++)
                    operations[idx++] = new WalOperation(WalRecordType.Put, rowKeys[i], encodedRows[i]);
                if (indexKeys != null)
                {
                    for (int i = 0; i < indexKeys.Length; i++)
                        operations[idx++] = new WalOperation(WalRecordType.Put, indexKeys[i], IndexEntryValue);
                }
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, operations);
            }

            // Persist NextRowId.
            var catKey = BuildCatalogKey(entry.Definition.CollectionName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    public void UpdateRow(int tableId, long rowId, byte[] encodedRow)
    {
        using (LockManager.TableWriteLock(0))
        {
            var key = BuildRowKey(tableId, rowId);

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[] { new WalOperation(WalRecordType.Put, key, encodedRow) });
            }

            PutEntry(key, encodedRow);
            _rowCache.SetWeak(key, encodedRow);
            _rowByKey.TryRemove(new RowKey(tableId, rowId), out _);
        }
    }

    public void DeleteRow(int tableId, long rowId)
    {
        using (LockManager.TableWriteLock(0))
        {
            var key = BuildRowKey(tableId, rowId);

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[] { new WalOperation(WalRecordType.Delete, key, null) });
            }

            if (UsesDirectStore)
                _dataStore.Delete(key);
            else
            {
                _memDict.Remove(key);
                _memSortedKeysDirty = true;
            }
            _rowCache.Remove(key);
            _rowByKey.TryRemove(new RowKey(tableId, rowId), out _);
        }
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    /// <summary>Range scan on data rows bounded by rowId values. For PK range queries.</summary>
    public void ScanRowKeyRange(
        int tableId,
        long minRowId, long maxRowId,
        RowDecoder decodeRow,
        Func<object?[], bool>? predicate,
        List<object?[]>? results = null,
        int limit = int.MaxValue,
        List<long>? rowIds = null,
        Action<object?[]>? onRow = null)
    {
        if (results == null && onRow == null)
            throw new ArgumentException("Either results or onRow must be provided.");

        var fromInclusive = (minRowId == long.MinValue)
            ? BuildTablePrefix((uint)tableId)
            : BuildRowKey(tableId, minRowId);
        var toExclusive = (maxRowId == long.MaxValue)
            ? BuildTablePrefix((uint)(tableId + 1))
            : BuildRowKey(tableId, maxRowId + 1);

        int yielded = 0;

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                // Binary search for start key.
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), fromInclusive) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                // Scan from start position to end of range.
                for (int i = lo; i < count; i++)
                {
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(i), toExclusive) >= 0)
                        break;
                    if (yielded >= limit)
                        break;

                    var row = decodeRow(store.GetValueAt(i));
                    if (predicate == null || predicate(row))
                    {
                        yielded++;
                        results?.Add(row);
                        onRow?.Invoke(row);
                        rowIds?.Add(ParseRowKey(store.GetKeyAt(i)).RowId);
                    }
                }
                // In-memory mode has no separate MemTable; _dataStore IS the only
                // storage. Skip the disk-mode MemTable/B+Tree merge below to avoid
                // double-counting via the EnumerateRange pass.
                return;
            }
            // Disk mode — collect MemTable candidates in [fromInclusive, toExclusive).
            // seenKeys wird lazy initialisiert: nach einem Checkpoint ist die MemTable
            // leer, dann sparen wir die HashSet-Allokation und die Contains-Prüfung
            // pro Disk-Eintrag.
            HashSet<byte[]>? seenKeys = null;

            // Fast path: small bounded int rowId range → direct hash probes,
            // avoids a full O(N log N) re-sort of _memSortedKeys when the MemTable
            // was just dirtied by preceding inserts. Threshold tuned to clearly beat
            // a linear MemDict scan (which would be O(memCount)).
            bool boundedRange = minRowId != long.MinValue && maxRowId != long.MaxValue;
            long rangeSize = boundedRange ? (maxRowId - minRowId + 1) : long.MaxValue;
            bool fullyCoveredByMem = false;
            if (boundedRange && rangeSize > 0 && rangeSize <= 256
                && (_memSortedKeysDirty || rangeSize * 4 < _memDict.Count))
            {
                long memHits = 0;
                for (long rid = minRowId; rid <= maxRowId; rid++)
                {
                    if (yielded >= limit) return;
                    var probeKey = BuildRowKey(tableId, rid);
                    if (_memDict.TryGetValue(probeKey, out var memValue))
                    {
                        memHits++;
                        var row = decodeRow(memValue);
                        if (predicate == null || predicate(row))
                        {
                            yielded++;
                            results?.Add(row);
                            onRow?.Invoke(row);
                            rowIds?.Add(rid);
                            seenKeys ??= new HashSet<byte[]>(ByteArrayContentComparer.Instance);
                            seenKeys.Add(probeKey);
                        }
                    }
                }
                // Every rid in the bounded range was probed. If MemTable contained
                // all of them, PK uniqueness guarantees the disk store cannot hold
                // any additional rid in this range → skip the B+Tree EnumerateRange
                // entirely (was 5.5% CPU dominant hotspot post round-1).
                if (memHits == rangeSize)
                    fullyCoveredByMem = true;
            }
            else
            {
                int memStartIdx = MemSortedKeysFindStart(fromInclusive);
                int memSortedCount = _memSortedKeys.Count;
                for (int i = memStartIdx; i < memSortedCount; i++)
                {
                    var key = _memSortedKeys[i];
                    if (ByteArrayComparer.Instance.Compare(key, toExclusive) >= 0)
                        break;
                    if (yielded >= limit) return;

                    var row = decodeRow(_memDict[key]);
                    if (predicate == null || predicate(row))
                    {
                        yielded++;
                        results?.Add(row);
                        onRow?.Invoke(row);
                        rowIds?.Add(ParseRowKey(key).RowId);
                        seenKeys ??= new HashSet<byte[]>(ByteArrayContentComparer.Instance);
                        seenKeys.Add(key);
                    }
                }
            }

            if (fullyCoveredByMem)
                return;

            if (seenKeys == null)
            {
                // Keine MemTable-Einträge im Bereich → direkter Scan ohne Deduplizierung.
                foreach (var kv in _dataStore.EnumerateRange(fromInclusive, toExclusive))
                {
                    if (yielded >= limit) return;

                    var row = decodeRow(kv.Value);
                    if (predicate == null || predicate(row))
                    {
                        yielded++;
                        results?.Add(row);
                        onRow?.Invoke(row);
                        rowIds?.Add(ParseRowKey(kv.Key).RowId);
                    }
                }
            }
            else
            {
                foreach (var kv in _dataStore.EnumerateRange(fromInclusive, toExclusive))
                {
                    if (seenKeys.Contains(kv.Key)) continue;
                    if (yielded >= limit) return;

                    var row = decodeRow(kv.Value);
                    if (predicate == null || predicate(row))
                    {
                        yielded++;
                        results?.Add(row);
                        onRow?.Invoke(row);
                        rowIds?.Add(ParseRowKey(kv.Key).RowId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Liefert nur die Row-IDs im gegebenen PK-Bereich, ohne die Rows zu decodieren.
    /// Wird vom DELETE-Pfad verwendet, um Decodierungs- und Boxing-Allokationen
    /// zu vermeiden, wenn keine WHERE-Prädikate auf Nicht-PK-Spalten nötig sind.
    /// </summary>
    public void ScanRowKeyRangeRowIdsOnly(
        int tableId,
        long minRowId, long maxRowId,
        List<long> rowIds)
    {
        var fromInclusive = (minRowId == long.MinValue)
            ? BuildTablePrefix((uint)tableId)
            : BuildRowKey(tableId, minRowId);
        var toExclusive = (maxRowId == long.MaxValue)
            ? BuildTablePrefix((uint)(tableId + 1))
            : BuildRowKey(tableId, maxRowId + 1);

        using (LockManager.TableReadLock(0))
        {
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);

            bool boundedRange = minRowId != long.MinValue && maxRowId != long.MaxValue;
            long rangeSize = boundedRange ? (maxRowId - minRowId + 1) : long.MaxValue;
            bool fullyCoveredByMem = false;
            if (boundedRange && rangeSize > 0 && rangeSize <= 256
                && (_memSortedKeysDirty || rangeSize * 4 < _memDict.Count))
            {
                long memHits = 0;
                for (long rid = minRowId; rid <= maxRowId; rid++)
                {
                    var probeKey = BuildRowKey(tableId, rid);
                    if (_memDict.ContainsKey(probeKey))
                    {
                        memHits++;
                        rowIds.Add(rid);
                        seenKeys.Add(probeKey);
                    }
                }
                if (memHits == rangeSize)
                    fullyCoveredByMem = true;
            }
            else
            {
                int memStartIdx = MemSortedKeysFindStart(fromInclusive);
                int memSortedCount = _memSortedKeys.Count;
                for (int i = memStartIdx; i < memSortedCount; i++)
                {
                    var key = _memSortedKeys[i];
                    if (ByteArrayComparer.Instance.Compare(key, toExclusive) >= 0)
                        break;

                    rowIds.Add(ParseRowKey(key).RowId);
                    seenKeys.Add(key);
                }
            }

            if (fullyCoveredByMem)
                return;

            foreach (var kv in _dataStore.EnumerateRange(fromInclusive, toExclusive))
            {
                if (seenKeys.Contains(kv.Key)) continue;
                rowIds.Add(ParseRowKey(kv.Key).RowId);
            }
        }
    }

    public void ScanWithPredicate(
        int tableId,
        RowDecoder decodeRow,
        Func<object?[], bool>? predicate,
        List<object?[]> results,
        int limit = int.MaxValue,
        List<long>? rowIds = null)
    {
        var prefix = BuildTablePrefix((uint)tableId);
        var toExclusive = BuildTablePrefix((uint)(tableId + 1));

        using (LockManager.TableReadLock(0))
        {
            // InMemory: data lives in dataStore; memTable is always empty.
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                // Binary search for start of table range.
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), prefix) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                for (int i = lo; i < count; i++)
                {
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(i), toExclusive) >= 0)
                        break;
                    if (results.Count >= limit)
                        break;

                    var row = decodeRow(store.GetValueAt(i));
                    if (predicate == null || predicate(row))
                    {
                        results.Add(row);
                        rowIds?.Add(ParseRowKey(store.GetKeyAt(i)).RowId);
                    }
                }
                return;
            }
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            int memStartIdx = MemSortedKeysFindStart(prefix);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, prefix))
                    break;
                if (results.Count >= limit)
                    return;

                var row = decodeRow(_memDict[key]);
                if (predicate == null || predicate(row))
                {
                    results.Add(row);
                    rowIds?.Add(ParseRowKey(key).RowId);
                    seenKeys.Add(key);
                }
            }

            foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive))
            {
                if (seenKeys.Contains(kv.Key))
                    continue;
                if (results.Count >= limit)
                    return;

                var row = decodeRow(kv.Value);
                if (predicate == null || predicate(row))
                {
                    results.Add(row);
                    rowIds?.Add(ParseRowKey(kv.Key).RowId);
                }
            }
        }
    }

    /// <summary>
    /// Scan with predicate-first optimization: only columns referenced by the
    /// predicate are decoded into a reused buffer. Full row decode happens only
    /// when the predicate matches.
    /// </summary>
    public void ScanWithPredicateFirst(
        int tableId,
        SqlTableDefinition tableDef,
        int[] predicateColumnIndices,
        RowDecoder decodeRow,
        Func<object?[], bool>? predicate,
        List<object?[]> results,
        int limit = int.MaxValue,
        List<long>? rowIds = null)
    {
        // Fall back to normal scan if we can't do partial decode.
        if (predicate == null || predicateColumnIndices.Length == 0)
        {
            ScanWithPredicate(tableId, decodeRow, predicate, results, limit, rowIds);
            return;
        }

        var prefix = BuildTablePrefix((uint)tableId);
        var toExclusive = BuildTablePrefix((uint)(tableId + 1));
        var colIndexMap = RowCodec.BuildColumnIndexMap(tableDef, predicateColumnIndices);
        var partialRow = new object?[tableDef.Columns.Count];

        // Pre-populate RawStringRef wrappers for string-type predicate columns
        foreach (var colIdx in predicateColumnIndices)
        {
            if (colIdx >= 0 && colIdx < tableDef.Columns.Count
                && tableDef.Columns[colIdx].Type == SqlScalarType.String)
            {
                partialRow[colIdx] = new RawStringRef();
            }
        }

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), prefix) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                for (int i = lo; i < count; i++)
                {
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(i), toExclusive) >= 0)
                        break;
                    if (results.Count >= limit)
                        break;

                    var encoded = store.GetValueAt(i);
                    RowCodec.DecodePredicateColumns(encoded, tableDef, partialRow, colIndexMap);
                    if (predicate(partialRow))
                    {
                        results.Add(decodeRow(encoded));
                        rowIds?.Add(ParseRowKey(store.GetKeyAt(i)).RowId);
                    }
                }
                return;
            }
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            int memStartIdx = MemSortedKeysFindStart(prefix);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, prefix))
                    break;
                if (results.Count >= limit)
                    return;

                var encoded = _memDict[key];
                RowCodec.DecodePredicateColumns(encoded, tableDef, partialRow, colIndexMap);
                if (predicate(partialRow))
                {
                    results.Add(decodeRow(encoded));
                    rowIds?.Add(ParseRowKey(key).RowId);
                    seenKeys.Add(key);
                }
            }

            // Zero-alloc fast path when memDict is empty (common after checkpoint).
            // BPlusTree.ScanValues reads from page buffers directly — no per-row byte[] copy.
            // Wenn RowIds benötigt werden, müssen wir den langsameren EnumerateRange-Pfad
            // nehmen, weil ScanValues den Schlüssel nicht an den Callback weitergibt.
            if (memSortedCount == 0 && rowIds == null)
            {
                _dataStore.ScanValues(prefix, toExclusive, (buffer, valueOff, valueLen) =>
                {
                    if (results.Count >= limit) return false;
                    RowCodec.DecodePredicateColumns(buffer, valueOff, valueLen,
                        tableDef, partialRow, colIndexMap);
                    if (predicate(partialRow))
                        results.Add(decodeRow(buffer.AsSpan(valueOff, valueLen).ToArray()));
                    return true;
                });
            }
            else
            {
                foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive))
                {
                    if (seenKeys.Contains(kv.Key))
                        continue;
                    if (results.Count >= limit)
                        return;

                    RowCodec.DecodePredicateColumns(kv.Value, tableDef, partialRow, colIndexMap);
                    if (predicate(partialRow))
                    {
                        results.Add(decodeRow(kv.Value));
                        rowIds?.Add(ParseRowKey(kv.Key).RowId);
                    }
                }
            }
        }
    }

    // ── Lazy (streaming) scan methods ─────────────────────────────────────────

    /// <summary>Decodes a row into a pre-allocated buffer instead of allocating a new array.</summary>
    internal delegate void RowDecoderIntoBuffer(ReadOnlySpan<byte> encodedRow, object?[] buffer);

    /// <summary>
    /// Lazy full-table scan. Yields decoded rows one at a time from a reused buffer.
    /// Consumers MUST NOT hold references to the yielded array across MoveNext() calls
    /// — each yield overwrites the same buffer.
    /// </summary>
    public IEnumerable<object?[]> ScanWithPredicateLazy(
        int tableId,
        SqlTableDefinition tableDef,
        Func<object?[], bool>? predicate)
    {
        var prefix = BuildTablePrefix((uint)tableId);
        var toExclusive = BuildTablePrefix((uint)(tableId + 1));
        var buffer = new object?[tableDef.Columns.Count];

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), prefix) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                for (int i = lo; i < count; i++)
                {
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(i), toExclusive) >= 0)
                        yield break;
                    RowCodec.DecodeToBuffer(store.GetValueAt(i), tableDef, buffer);
                    if (predicate == null || predicate(buffer))
                        yield return buffer;
                }
                yield break;
            }
            // Disk mode: merge MemTable + BPlusTree with dedup.
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            int memStartIdx = MemSortedKeysFindStart(prefix);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, prefix))
                    break;
                var encoded = _memDict[key];
                RowCodec.DecodeToBuffer(encoded, tableDef, buffer);
                if (predicate == null || predicate(buffer))
                {
                    yield return buffer;
                    seenKeys.Add(key);
                }
            }

            foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive))
            {
                if (seenKeys.Contains(kv.Key))
                    continue;
                RowCodec.DecodeToBuffer(kv.Value, tableDef, buffer);
                if (predicate == null || predicate(buffer))
                    yield return buffer;
            }
        }
    }

    /// <summary>
    /// Lazy PK range scan. Yields decoded rows within [minRowId, maxRowId] one at a time
    /// from a reused buffer. Same buffer-reuse contract as ScanWithPredicateLazy.
    /// </summary>
    public IEnumerable<object?[]> ScanRowKeyRangeLazy(
        int tableId,
        long minRowId, long maxRowId,
        SqlTableDefinition tableDef,
        Func<object?[], bool>? predicate)
    {
        var fromInclusive = (minRowId == long.MinValue)
            ? BuildTablePrefix((uint)tableId)
            : BuildRowKey(tableId, minRowId);
        var toExclusive = (maxRowId == long.MaxValue)
            ? BuildTablePrefix((uint)(tableId + 1))
            : BuildRowKey(tableId, maxRowId + 1);
        var buffer = new object?[tableDef.Columns.Count];

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), fromInclusive) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                for (int i = lo; i < count; i++)
                {
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(i), toExclusive) >= 0)
                        yield break;
                    RowCodec.DecodeToBuffer(store.GetValueAt(i), tableDef, buffer);
                    if (predicate == null || predicate(buffer))
                        yield return buffer;
                }
                yield break;
            }
            // Disk mode: merge MemTable + BPlusTree with dedup.
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            int memStartIdx = MemSortedKeysFindStart(fromInclusive);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (ByteArrayComparer.Instance.Compare(key, toExclusive) >= 0)
                    break;
                var encoded = _memDict[key];
                RowCodec.DecodeToBuffer(encoded, tableDef, buffer);
                if (predicate == null || predicate(buffer))
                {
                    yield return buffer;
                    seenKeys.Add(key);
                }
            }

            foreach (var kv in _dataStore.EnumerateRange(fromInclusive, toExclusive))
            {
                if (seenKeys.Contains(kv.Key))
                    continue;
                RowCodec.DecodeToBuffer(kv.Value, tableDef, buffer);
                if (predicate == null || predicate(buffer))
                    yield return buffer;
            }
        }
    }

    /// <summary>Scans all rows for a table, providing the stored rowId along with the decoded row.</summary>
    public void ScanWithRowIds(
        int tableId,
        RowDecoder decodeRow,
        Func<long, object?[], bool>? predicate,
        List<(long RowId, object?[] Row)> results,
        int limit = int.MaxValue)
    {
        var prefix = BuildTablePrefix((uint)tableId);
        var toExclusive = BuildTablePrefix((uint)(tableId + 1));

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;

                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), prefix) < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }

                for (int i = lo; i < count; i++)
                {
                    var key = store.GetKeyAt(i);
                    if (ByteArrayComparer.Instance.Compare(key, toExclusive) >= 0)
                        break;
                    if (results.Count >= limit)
                        return;

                    var rowId = ParseRowKey(key).RowId;
                    var row = decodeRow(store.GetValueAt(i));
                    if (predicate == null || predicate(rowId, row))
                        results.Add((rowId, row));
                }
                return;
            }
            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            int memStartIdx = MemSortedKeysFindStart(prefix);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, prefix))
                    break;
                if (results.Count >= limit)
                    return;

                var rowId = ParseRowKey(key).RowId;
                var row = decodeRow(_memDict[key]);
                if (predicate == null || predicate(rowId, row))
                {
                    results.Add((rowId, row));
                    seenKeys.Add(key);
                }
            }

            // Merge from data store (skip keys already seen in mem table).
            foreach (var kv in _dataStore.EnumerateRange(prefix, toExclusive))
            {
                if (seenKeys.Contains(kv.Key))
                    continue;
                if (results.Count >= limit)
                    return;

                var rowId = ParseRowKey(kv.Key).RowId;
                var row = decodeRow(kv.Value);
                if (predicate == null || predicate(rowId, row))
                    results.Add((rowId, row));
            }
        }
    }

    public long CountRows(int tableId)
    {
        var prefix = BuildTablePrefix((uint)tableId);
        var toExclusive = BuildTablePrefix((uint)(tableId + 1));

        using (LockManager.TableReadLock(0))
        {
            if (UsesDirectStore)
            {
                long count = 0;
                foreach (var _ in _dataStore.EnumerateRange(prefix, toExclusive))
                    count++;
                return count;
            }

            var seenKeys = new HashSet<byte[]>(ByteArrayContentComparer.Instance);
            EnsureMemSortedKeys();
            foreach (var key in _memSortedKeys)
            {
                if (StartsWithPrefix(key, prefix))
                    seenKeys.Add(key);
            }

            long diskCount = seenKeys.Count;
            foreach (var _ in _dataStore.EnumerateRange(prefix, toExclusive))
                diskCount++;

            return diskCount;
        }
    }

    // ── Index operations ──────────────────────────────────────────────────

    public void InsertIndexEntry(int indexId, byte[] sortKey, int tableId, long rowId)
    {
        var key = BuildIndexEntryKey(indexId, sortKey, tableId, rowId);

        using (LockManager.TableWriteLock(0))
        {
            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[] { new WalOperation(WalRecordType.Put, key, IndexEntryValue) });
            }
            PutEntry(key, IndexEntryValue);
        }
    }

    public void InsertIndexEntries(int indexId, IReadOnlyList<(byte[] SortKey, int TableId, long RowId)> entries)
    {
        if (entries.Count == 0) return;

        using (LockManager.TableWriteLock(0))
        {
            var operations = new WalOperation[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var key = BuildIndexEntryKey(indexId, entries[i].SortKey, entries[i].TableId, entries[i].RowId);
                operations[i] = new WalOperation(WalRecordType.Put, key, IndexEntryValue);
                PutEntry(key, IndexEntryValue);
            }
            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, operations);
            }
        }
    }

    /// <summary>Batch insert across multiple indexes — single WAL flush.</summary>
    public void InsertIndexEntries(IReadOnlyList<(int IndexId, byte[] SortKey, int TableId, long RowId)> entries)
    {
        if (entries.Count == 0) return;

        using (LockManager.TableWriteLock(0))
        {
            var operations = new WalOperation[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var key = BuildIndexEntryKey(e.IndexId, e.SortKey, e.TableId, e.RowId);
                operations[i] = new WalOperation(WalRecordType.Put, key, IndexEntryValue);
                PutEntry(key, IndexEntryValue);
            }
            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, operations);
            }
        }
    }

    /// <summary>Batch delete index entries across multiple indexes — single WAL flush.</summary>
    public void DeleteIndexEntries(IReadOnlyList<(int IndexId, byte[] SortKey, int TableId, long RowId)> entries)
    {
        if (entries.Count == 0) return;

        using (LockManager.TableWriteLock(0))
        {
            var operations = new WalOperation[entries.Count];
            var keys = new List<byte[]>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var key = BuildIndexEntryKey(e.IndexId, e.SortKey, e.TableId, e.RowId);
                keys.Add(key);
                operations[i] = new WalOperation(WalRecordType.Delete, key, null);
            }

            if (UsesDirectStore)
                _dataStore.BulkDelete(keys);
            else
                BulkDeleteMemTable(keys);

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, operations);
            }
        }
    }

    /// <summary>Batch delete rows — single WAL flush.</summary>
    public void DeleteRows(int tableId, IReadOnlyList<long> rowIds)
    {
        if (rowIds.Count == 0) return;

        using (LockManager.TableWriteLock(0))
        {
            var operations = new WalOperation[rowIds.Count];
            var keys = new List<byte[]>(rowIds.Count);
            for (int i = 0; i < rowIds.Count; i++)
            {
                var key = BuildRowKey(tableId, rowIds[i]);
                keys.Add(key);
                operations[i] = new WalOperation(WalRecordType.Delete, key, null);
                _rowCache.Remove(key);
                _rowByKey.TryRemove(new RowKey(tableId, rowIds[i]), out _);
            }

            if (UsesDirectStore)
            {
                _dataStore.BulkDelete(keys);
            }
            else
            {
                // WAL/MemTable buffer: remove from MemTable, but also delete from the
                // underlying B+Tree in case the rows were already checkpointed. Without this,
                // a later INSERT of the same PK would see a duplicate in _dataStore.TryGet.
                BulkDeleteMemTable(keys);
                _dataStore.BulkDelete(keys);
            }

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, operations);
            }
        }
    }

    // ── Transaction batch apply ────────────────────────────────────────────

    public void ApplyBatch(IReadOnlyList<DeferredWrite> writes)
    {
        if (writes.Count == 0) return;

        using (LockManager.TableWriteLock(0))
        {
            var walOps = new List<WalOperation>(writes.Count);
            var keysToDelete = new List<byte[]>();
            var entriesToUpsert = new List<KeyValuePair<byte[], byte[]>>();

            foreach (var w in writes)
            {
                var key = BuildRowKey(w.TableId, w.RowId);
                switch (w.Op)
                {
                    case WriteOp.Insert:
                    case WriteOp.Update:
                        if (w.EncodedRow == null) continue;
                        walOps.Add(new WalOperation(WalRecordType.Put, key, w.EncodedRow));
                        entriesToUpsert.Add(new KeyValuePair<byte[], byte[]>(key, w.EncodedRow));
                        _rowCache.SetWeak(key, w.EncodedRow);
                        break;
                    case WriteOp.Delete:
                        walOps.Add(new WalOperation(WalRecordType.Delete, key, null));
                        keysToDelete.Add(key);
                        _rowCache.Remove(key);
                        break;
                }
            }

            // Apply to in-memory or MemTable
            if (UsesDirectStore)
            {
                if (keysToDelete.Count > 0)
                    _dataStore.BulkDelete(keysToDelete);
                if (entriesToUpsert.Count > 0)
                    _dataStore.BulkUpsert(entriesToUpsert);
            }
            else
            {
                if (keysToDelete.Count > 0)
                    BulkDeleteMemTable(keysToDelete);
                if (entriesToUpsert.Count > 0)
                    BulkMergeMemTable(entriesToUpsert);
            }

            // Single WAL transaction for all writes
            if (_walLog != null && walOps.Count > 0)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, walOps);
            }
        }
    }

    public void ApplyBatch(IReadOnlyList<DeferredWrite> writes, ITransaction<byte[], byte[]> tx)
    {
        if (writes.Count == 0) return;
        foreach (var w in writes)
        {
            var key = BuildRowKey(w.TableId, w.RowId);
            switch (w.Op)
            {
                case WriteOp.Insert:
                case WriteOp.Update:
                    if (w.EncodedRow == null) continue;
                    tx.Upsert(key, w.EncodedRow);
                    _rowCache.Remove(key);
                    _rowByKey.TryRemove(new RowKey(w.TableId, w.RowId), out _);
                    break;
                case WriteOp.Delete:
                    tx.Delete(key);
                    _rowCache.Remove(key);
                    _rowByKey.TryRemove(new RowKey(w.TableId, w.RowId), out _);
                    break;
            }
        }
    }

    public void ApplyIndexBatch(IReadOnlyList<DeferredIndexOp> ops)
    {
        if (ops.Count == 0) return;
        using (LockManager.TableWriteLock(0))
        {
            foreach (var op in ops)
            {
                var key = BuildIndexEntryKey(op.IndexId, op.SortKey, op.TableId, op.RowId);
                switch (op.Op)
                {
                    case IndexOpType.Insert:
                        _dataStore.Upsert(key, IndexEntryValue);
                        break;
                    case IndexOpType.Delete:
                        _dataStore.Delete(key);
                        break;
                }
            }
        }
    }

    public void ApplyIndexBatch(IReadOnlyList<DeferredIndexOp> ops, ITransaction<byte[], byte[]> tx)
    {
        if (ops.Count == 0) return;
        foreach (var op in ops)
        {
            var key = BuildIndexEntryKey(op.IndexId, op.SortKey, op.TableId, op.RowId);
            switch (op.Op)
            {
                case IndexOpType.Insert:
                    tx.Upsert(key, IndexEntryValue);
                    break;
                case IndexOpType.Delete:
                    tx.Delete(key);
                    break;
            }
        }
    }

    public ITransaction<byte[], byte[]>? CreateTransaction(
        IsolationLevel level = IsolationLevel.Snapshot)
    {
        if (_dataStore is MvccBPlusTreeStore store)
        {
            var tx = store.BeginTransaction(level);
            return new StorageTransactionAdapter(tx);
        }
        return null;
    }

    public void DeleteIndexEntry(int indexId, byte[] sortKey, int tableId, long rowId)
    {
        var key = BuildIndexEntryKey(indexId, sortKey, tableId, rowId);

        using (LockManager.TableWriteLock(0))
        {
            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[] { new WalOperation(WalRecordType.Delete, key, null) });
            }
            if (UsesDirectStore)
                _dataStore.Delete(key);
            else
                _memDict.Remove(key);
                _memSortedKeysDirty = true;
        }
    }

    public void UpdateIndexEntry(int indexId, byte[] oldSortKey, byte[] newSortKey, int tableId, long rowId)
    {
        using (LockManager.TableWriteLock(0))
        {
            var oldKey = BuildIndexEntryKey(indexId, oldSortKey, tableId, rowId);
            var newKey = BuildIndexEntryKey(indexId, newSortKey, tableId, rowId);

            if (_walLog != null)
            {
                var txId = InterlockedIncrement(ref _nextTransactionId);
                AppendToWal(txId, new[]
                {
                    new WalOperation(WalRecordType.Delete, oldKey, null),
                    new WalOperation(WalRecordType.Put, newKey, IndexEntryValue)
                });
            }
            if (UsesDirectStore)
            {
                _dataStore.Delete(oldKey);
                _dataStore.Upsert(newKey, IndexEntryValue);
            }
            else
            {
                _memDict.Remove(oldKey);
                _memDict[newKey] = IndexEntryValue;
                _memSortedKeysDirty = true;
            }
        }
    }

    public bool IndexEntryExists(int indexId, byte[] sortKey, int? excludeTableId = null, long? excludeRowId = null)
    {
        var prefix = BuildIndexPrefixWithKey(indexId, sortKey);
        var endKey = BuildIndexPrefix(indexId + 1);

        using (LockManager.TableReadLock(0))
        {
            int memStartIdx = MemSortedKeysFindStart(prefix);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memStartIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, prefix))
                    break;
                if (excludeTableId.HasValue && excludeRowId.HasValue)
                {
                    var (fIdx, tid, rid) = ParseIndexEntryKey(key);
                    if (tid == excludeTableId.Value && rid == excludeRowId.Value)
                        continue;
                }
                return true;
            }

            foreach (var kv in _dataStore.EnumerateRange(prefix, endKey))
            {
                if (!StartsWithPrefix(kv.Key, prefix))
                    break; // Schlüssel außerhalb des gesuchten Präfix — keine weiteren Treffer möglich
                if (excludeTableId.HasValue && excludeRowId.HasValue)
                {
                    var (_, tid, rid) = ParseIndexEntryKey(kv.Key);
                    if (tid == excludeTableId.Value && rid == excludeRowId.Value)
                        continue;
                }
                return true;
            }
        }
        return false;
    }

    public List<(int TableId, long RowId)> ScanIndex(
        int indexId,
        byte[] startSortKey, byte[] endSortKey,
        bool startInclusive, bool endInclusive)
    {
        var results = new List<(int TableId, long RowId)>();
        var indexPrefix = BuildIndexPrefix(indexId);
        var btreeEnd = BuildIndexPrefix(indexId + 1);

        using (LockManager.TableReadLock(0))
        {
            if (IsInMemory)
            {
                var store = (InMemoryStore)_dataStore;
                int count = store.Count;
                if (count == 0) return results;

                var searchKey = BuildIndexPrefixWithKeyAndSuffix(indexId, startSortKey);
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    int cmp = ByteArrayComparer.Instance.Compare(store.GetKeyAt(mid), searchKey);
                    if (cmp < 0)
                        lo = mid + 1;
                    else
                        hi = mid - 1;
                }
                int startIdx = lo;

                for (int i = startIdx; i < count; i++)
                {
                    var key = store.GetKeyAt(i);

                    // Stop when we leave this index's key space.
                    if (ByteArrayComparer.Instance.Compare(key, btreeEnd) >= 0)
                        break;

                    // Check sort key bounds and stop when past the end range.
                    int cmpEnd = endSortKey.Length > 0
                        ? CompareIndexSortKeyEndBound(key, endSortKey)
                        : -1;
                    if (endSortKey.Length > 0 && cmpEnd > 0)
                        break;
                    if (cmpEnd == 0 && !endInclusive)
                        break;

                    int cmpStart = startSortKey.Length > 0
                        ? CompareIndexSortKey(key, startSortKey)
                        : 1;
                    if (cmpStart < 0 || (cmpStart == 0 && !startInclusive))
                        continue;

                    results.Add(ParseIndexEntryKeyToTableRow(key));
                }
                return results;
            }
            // Disk mode: scan sorted MemTable first, then B+Tree.
            var startKey = startSortKey.Length > 0
                ? BuildIndexPrefixWithKey(indexId, startSortKey)
                : indexPrefix;
            var hasMemTableResults = false;
            HashSet<(int TableId, long RowId)>? seenKeys = null;

            int memIdx = MemSortedKeysFindStart(startKey);
            int memSortedCount = _memSortedKeys.Count;
            for (int i = memIdx; i < memSortedCount; i++)
            {
                var key = _memSortedKeys[i];
                if (!StartsWithPrefix(key, indexPrefix))
                    break;

                int cmpEnd = endSortKey.Length > 0
                    ? CompareIndexSortKeyEndBound(key, endSortKey)
                    : -1;
                if (cmpEnd > 0 || (cmpEnd == 0 && !endInclusive))
                    break;

                int cmpStart = startSortKey.Length > 0
                    ? CompareIndexSortKey(key, startSortKey)
                    : 1;
                if (cmpStart < 0 || (cmpStart == 0 && !startInclusive))
                    continue;

                var tr = ParseIndexEntryKeyToTableRow(key);
                results.Add(tr);
                (seenKeys ??= new HashSet<(int, long)>()).Add(tr);
                hasMemTableResults = true;
            }

            // BPlusTree scan: use zero-copy EnumerateEntriesSpan.
            if (_dataStore is BPlusTreeStore bpStore)
            {
                int endSortKeyLen = endSortKey.Length;
                bpStore.Tree.EnumerateEntriesSpan(startKey, btreeEnd,
                    (buf, keyOff, keyLen, valOff, valLen) =>
                    {
                        // Key layout: [sentinel:4][indexId:4][sortKey...][tableId:4][rowId:8]
                        int totalKeyLen = keyOff + keyLen;
                        var tableId = BinaryPrimitives.ReadInt32LittleEndian(
                            buf.AsSpan(totalKeyLen - 12, 4));
                        var rowId = BinaryPrimitives.ReadInt64LittleEndian(
                            buf.AsSpan(totalKeyLen - 8, 8));

                        if (hasMemTableResults && seenKeys!.Contains((tableId, rowId)))
                            return true; // continue

                        // Compare sort key portion against bounds.
                        int sortKeyOff = keyOff + 8; // skip sentinel + indexId
                        int sortKeyLen = keyLen - 8 - 12; // minus sentinel, indexId, tableId, rowId

                        if (startSortKey.Length > 0)
                        {
                            int cmpStart = CompareSpanToKey(
                                buf.AsSpan(sortKeyOff, sortKeyLen), startSortKey);
                            if (cmpStart < 0 || (cmpStart == 0 && !startInclusive))
                                return true; // continue
                        }

                        if (endSortKeyLen > 0)
                        {
                            int cmpEnd = CompareSpanToKey(
                                buf.AsSpan(sortKeyOff, sortKeyLen), endSortKey);
                            if (cmpEnd > 0 || (cmpEnd == 0 && !endInclusive))
                                return false; // stop — past end of range
                        }

                        results.Add((tableId, rowId));
                        return true;
                    });
            }
            else
            {
                // MvccBPlusTree und andere Stores: generischer EnumerateRange-Scan
                foreach (var kv in _dataStore.EnumerateRange(startKey, btreeEnd))
                {
                    var key = kv.Key;

                    if (hasMemTableResults)
                    {
                        var tr = ParseIndexEntryKeyToTableRow(key);
                        if (seenKeys!.Contains(tr))
                            continue;
                    }

                    // Check sort key bounds.
                    int cmpEnd = endSortKey.Length > 0
                        ? CompareIndexSortKeyEndBound(key, endSortKey)
                        : -1;
                    if (endSortKey.Length > 0 && cmpEnd > 0)
                        break;
                    if (cmpEnd == 0 && !endInclusive)
                        break;

                    int cmpStart = startSortKey.Length > 0
                        ? CompareIndexSortKey(key, startSortKey)
                        : 1;
                    if (cmpStart < 0 || (cmpStart == 0 && !startInclusive))
                        continue;

                    results.Add(ParseIndexEntryKeyToTableRow(key));
                }
            }
        }

        return results;
    }

    public void AddIndexToTable(string tableName, string indexName, int indexId, SqlTableDefinition newTableDef)
    {
        // Catalog-write: mutates entry + persists catalog row.
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(tableName, out var entry))
                throw new WalhallaException($"Table '{tableName}' not found.");

            entry.Definition = newTableDef;
            entry.IndexIds[indexName] = indexId;
            entry.CachedIndexMeta = null;

            // Persist updated catalog entry.
            var catKey = BuildCatalogKey(tableName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    public void RemoveIndexFromTable(string tableName, string indexName, int indexId, SqlTableDefinition newTableDef)
    {
        // Catalog-write: mutates entry + persists catalog row.
        using (_catalogLockManager.TableWriteLock(0))
        using (LockManager.TableWriteLock(0))
        {
            if (!_catalog.TryGetValue(tableName, out var entry))
                throw new WalhallaException($"Table '{tableName}' not found.");

            // Delete all index entries from MemTable + B+Tree.
            var prefix = BuildIndexPrefix(indexId);
            var endKey = BuildIndexPrefix(indexId + 1);

            var toRemove = new List<byte[]>();
            EnsureMemSortedKeys();
            foreach (var key in _memSortedKeys)
            {
                if (StartsWithPrefix(key, prefix))
                    toRemove.Add(key);
            }
            foreach (var key in toRemove)
                _memDict.Remove(key);
            _memSortedKeysDirty = true;

            foreach (var kv in _dataStore.EnumerateRange(prefix, endKey).ToList())
                _dataStore.Delete(kv.Key);

            entry.Definition = newTableDef;
            entry.IndexIds.Remove(indexName);
            entry.CachedIndexMeta = null;

            // Persist updated catalog entry.
            var catKey = BuildCatalogKey(tableName, CatalogProperty.Definition);
            var catValue = SerializeCatalogEntry(entry);
            _dataStore.Upsert(catKey, catValue);
        }
    }

    public int AllocateIndexId()
    {
        return _nextIndexId++;
    }

    private void DeleteAllIndexEntriesForTable(int tableId)
    {
        // Delete from MemTable.
        var indexPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(indexPrefix, IndexSentinel);

        var toRemove = new List<byte[]>();
        EnsureMemSortedKeys();
        foreach (var key in _memSortedKeys)
        {
            if (StartsWithPrefix(key, indexPrefix))
            {
                var (_, tid, _) = ParseIndexEntryKey(key);
                if (tid == tableId)
                    toRemove.Add(key);
            }
        }
        foreach (var key in toRemove)
        {
            _memDict.Remove(key);
            _memSortedKeysDirty = true;
        }

        // Delete from B+Tree. End at CatalogSentinel so catalog entries are excluded.
        var indexEnd = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        foreach (var kv in _dataStore.EnumerateRange(indexPrefix, indexEnd).ToList())
        {
            var (_, tid, _) = ParseIndexEntryKey(kv.Key);
            if (tid == tableId)
                _dataStore.Delete(kv.Key);
        }
    }

    private static int CompareIndexSortKey(byte[] indexEntryKey, byte[] sortKey)
    {
        // indexEntryKey: [0xFFFFFFFE:4][indexId:4][sortKey...][tableId:4][rowId:8]
        int sortKeyStart = 8;
        int sortKeyLen = indexEntryKey.Length - 8 - 12; // subtract sentinel, indexId, tableId, rowId
        int cmpLen = Math.Min(sortKeyLen, sortKey.Length);
        for (int i = 0; i < cmpLen; i++)
        {
            if (indexEntryKey[sortKeyStart + i] != sortKey[i])
                return indexEntryKey[sortKeyStart + i] < sortKey[i] ? -1 : 1;
        }
        return sortKeyLen.CompareTo(sortKey.Length);
    }

    /// <summary>
    /// Like <see cref="CompareIndexSortKey"/> but treats prefix-equal longer keys as equal (0)
    /// instead of greater. This is used for the end bound in exact-match index scans where
    /// sort key "type=doc" (longer) should not stop iteration when searching for "type".
    /// </summary>
    private static int CompareIndexSortKeyEndBound(byte[] indexEntryKey, byte[] sortKey)
    {
        int sortKeyStart = 8;
        int sortKeyLen = indexEntryKey.Length - 8 - 12;
        int cmpLen = Math.Min(sortKeyLen, sortKey.Length);
        for (int i = 0; i < cmpLen; i++)
        {
            if (indexEntryKey[sortKeyStart + i] != sortKey[i])
                return indexEntryKey[sortKeyStart + i] < sortKey[i] ? -1 : 1;
        }
        // If all compared bytes match and the entry sort key is longer, treat as still within range (0).
        if (sortKeyLen > sortKey.Length) return 0;
        return sortKeyLen.CompareTo(sortKey.Length);
    }

    private static int CompareSpanToKey(ReadOnlySpan<byte> sortKeySpan, byte[] sortKey)
    {
        int cmpLen = Math.Min(sortKeySpan.Length, sortKey.Length);
        for (int i = 0; i < cmpLen; i++)
        {
            if (sortKeySpan[i] != sortKey[i])
                return sortKeySpan[i] < sortKey[i] ? -1 : 1;
        }
        return sortKeySpan.Length.CompareTo(sortKey.Length);
    }

    // ── Checkpoint ───────────────────────────────────────────────────────────

    public void Checkpoint()
    {
        using (LockManager.TableWriteLock(0))
        {
            // Flush MemTable to store.
            if (_odsPager != null)
            {
                _odsPager.BeginWriteBatch();
                try
                {
                    foreach (var kv in _memDict)
                        _dataStore.Upsert(kv.Key, kv.Value);
                    _memDict.Clear();
                    _memSortedKeys.Clear();
                    _memSortedKeysDirty = false;
                    _odsPager.CommitWriteBatch();
                }
                catch
                {
                    _odsPager.AbortWriteBatch();
                    throw;
                }
            }
            else
            {
                foreach (var kv in _memDict)
                    _dataStore.Upsert(kv.Key, kv.Value);
                _memDict.Clear();
                _memSortedKeys.Clear();
                _memSortedKeysDirty = false;
            }

            // Engine-spezifisches Checkpoint (MvccBPlusTreeStore truncates interne WAL hier)
            _dataStore.Checkpoint();

            // Drain pending group commits, then truncate WAL and flush ODS.
            _groupCommit?.FlushAndWait();
            _walLog?.Truncate();
            _odsPager?.Flush();

            // Clear cache on checkpoint.
            _rowCache = new LruValueCache(_options.CacheSizeBytes);
        }
    }

    // ── Vacuum ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Triggers manual garbage collection on the storage engine.
    /// For MvccBPlusTree, delegates to the store's native Vacuum.
    /// For other modes, this is a no-op.
    /// </summary>
    public int Vacuum(string? tableName)
    {
        if (IsMvccBPlusTree)
        {
            using (LockManager.TableWriteLock(0))
            {
                _dataStore.Vacuum();
            }
            return 0; // MvccBPlusTree-Vacuum gibt keinen Count zurück
        }

        return 0;
    }

    /// <summary>Scans all visible rows for the table and collects every live BlobRef.</summary>
    private List<BlobRef> CollectLiveBlobRefs(int tableId, SqlTableDefinition def)
    {
        var liveRefs = new List<BlobRef>();
        var binaryColumnIndices = new List<int>();
        for (int i = 0; i < def.Columns.Count; i++)
            if (def.Columns[i].Type == SqlScalarType.Binary)
                binaryColumnIndices.Add(i);

        if (binaryColumnIndices.Count == 0)
            return liveRefs;

        var from = BuildRowKey(tableId, long.MinValue);
        var to = BuildRowKey(tableId, long.MaxValue);

        foreach (var kv in _dataStore.EnumerateRange(from, to))
        {
            var rowBytes = kv.Value;
            int pos = 0;
            // Skip null-bitmap (1 byte per 8 columns, rounded up)
            int nullBitmapBytes = (def.Columns.Count + 7) / 8;
            pos += nullBitmapBytes;

            for (int colIdx = 0; colIdx < def.Columns.Count; colIdx++)
            {
                if (pos + 4 > rowBytes.Length)
                    break;

                var type = def.Columns[colIdx].Type;
                if (type == SqlScalarType.Binary)
                {
                    var lenU = BinaryPrimitives.ReadUInt32LittleEndian(rowBytes.AsSpan(pos, 4));
                    if (lenU == BlobRef.Sentinel)
                    {
                        pos += 4;
                        if (pos + BlobRef.SizeInBytes <= rowBytes.Length)
                        {
                            var blobRef = BlobRef.Decode(rowBytes.AsSpan(pos, BlobRef.SizeInBytes));
                            liveRefs.Add(blobRef);
                        }
                        pos += BlobRef.SizeInBytes;
                    }
                    else
                    {
                        pos += 4 + (int)lenU;
                    }
                }
                else
                {
                    pos += GetFixedTypeSize(type);
                }
            }
        }

        return liveRefs;
    }

    private static int GetFixedTypeSize(SqlScalarType type) => type switch
    {
        SqlScalarType.Int16 => 2,
        SqlScalarType.Int32 => 4,
        SqlScalarType.Int64 or SqlScalarType.Double or SqlScalarType.DateTime or SqlScalarType.Date or SqlScalarType.Time => 8,
        SqlScalarType.Decimal => 16,
        SqlScalarType.Boolean => 1,
        SqlScalarType.Guid => 4 + 36, // length prefix + "D" format guid string
        SqlScalarType.Binary => throw new InvalidOperationException("Variable-length type should not reach fixed-size path."),
        _ => throw new InvalidOperationException($"Unknown type {type}")
    };

    /// <summary>Rewrites every row that contains BlobRefs, updating their offsets.</summary>
    private void RewriteBlobRefsInRows(int tableId, SqlTableDefinition def, Dictionary<long, long> offsetMap)
    {
        var from = BuildRowKey(tableId, long.MinValue);
        var to = BuildRowKey(tableId, long.MaxValue);
        var updates = new List<(byte[], byte[])>();

        foreach (var kv in _dataStore.EnumerateRange(from, to))
        {
            var rowBytes = kv.Value;
            var newRow = RewriteBlobRefsInRow(rowBytes, def, offsetMap);
            if (newRow != null)
                updates.Add((kv.Key, newRow));
        }

        foreach (var (key, value) in updates)
            _dataStore.Upsert(key, value);
    }

    /// <summary>Creates a new row byte array with updated BlobRef offsets.
    /// Returns null if no BlobRef was updated.
    /// </summary>
    private static byte[]? RewriteBlobRefsInRow(byte[] rowBytes, SqlTableDefinition def, Dictionary<long, long> offsetMap)
    {
        int nullBitmapBytes = (def.Columns.Count + 7) / 8;
        int pos = nullBitmapBytes;
        bool mutated = false;
        var builder = new List<byte>();
        builder.AddRange(rowBytes.AsSpan(0, nullBitmapBytes));

        for (int colIdx = 0; colIdx < def.Columns.Count; colIdx++)
        {
            if (pos + 4 > rowBytes.Length)
                break;

            var type = def.Columns[colIdx].Type;
            if (type == SqlScalarType.Binary)
            {
                var lenU = BinaryPrimitives.ReadUInt32LittleEndian(rowBytes.AsSpan(pos, 4));
                if (lenU == BlobRef.Sentinel)
                {
                    pos += 4;
                    var blobRef = BlobRef.Decode(rowBytes.AsSpan(pos, BlobRef.SizeInBytes));
                    pos += BlobRef.SizeInBytes;

                    if (offsetMap.TryGetValue(blobRef.Offset, out var newOffset))
                    {
                        mutated = true;
                        builder.AddRange(BitConverter.GetBytes(BlobRef.Sentinel));
                        builder.AddRange(new BlobRef(newOffset, blobRef.Length, blobRef.Flags).Encode());
                    }
                    else
                    {
                        builder.AddRange(BitConverter.GetBytes(BlobRef.Sentinel));
                        builder.AddRange(blobRef.Encode());
                    }
                }
                else
                {
                    int len = (int)lenU;
                    builder.AddRange(rowBytes.AsSpan(pos, 4 + len));
                    pos += 4 + len;
                }
            }
            else if (type == SqlScalarType.Guid)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(rowBytes.AsSpan(pos, 4));
                builder.AddRange(rowBytes.AsSpan(pos, 4 + len));
                pos += 4 + len;
            }
            else
            {
                int fixedSize = GetFixedTypeSize(type);
                builder.AddRange(rowBytes.AsSpan(pos, fixedSize));
                pos += fixedSize;
            }
        }

        return mutated ? builder.ToArray() : null;
    }

    /// <summary>
    /// Legacy hook previously used for WTree-specific flush. Now a no-op;
    /// MvccBPlusTree commits via its own IStorageTransaction lifecycle.
    /// </summary>
    public void CommitStore()
    {
    }

    // ── Recovery ─────────────────────────────────────────────────────────────

    private void Recover()
    {
        if (_walLog != null)
        {
            // 1. Replay WAL into MemTable.
            var committed = _walLog.ReadCommittedTransactions();
            foreach (var tx in committed)
            {
                _nextTransactionId = Math.Max(_nextTransactionId, tx.TransactionId + 1);
                foreach (var op in tx.Operations)
                {
                    if (op.Type == WalRecordType.Put)
                        _memDict[op.Key] = op.Value!;
                    else
                        _memDict.Remove(op.Key);
                    _memSortedKeysDirty = true;
                }
            }

            // 2. Load catalog from store + MemTable.
            LoadCatalog();

            // 3. Auto-checkpoint if WAL exceeds threshold.
            if (_walLog.SizeBytes >= _options.AutoCheckpointWalThresholdBytes && _options.AutoCheckpointWalThresholdBytes > 0)
                Checkpoint();
        }
        else
        {
            // In-memory: nothing to replay. Just load catalog from store.
            LoadCatalog();
        }
    }

    private void LoadCatalog()
    {
        // Load catalog entries from B+Tree.
        var catPrefix = BuildCatalogPrefix();
        var catToExclusive = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        foreach (var kv in _dataStore.EnumerateRange(catPrefix, catToExclusive))
        {
            var entry = DeserializeCatalogEntry(kv.Value);
            if (entry != null)
            {
                _catalog[entry.Definition.CollectionName] = entry;
                _nextTableId = Math.Max(_nextTableId, entry.TableId + 1);
            }
        }

        // Also check MemTable for catalog entries.
        foreach (var kv in _memDict)
        {
            if (kv.Key.Length <= 4 || BinaryPrimitives.ReadUInt32LittleEndian(kv.Key) != CatalogSentinel)
                continue;

            var entry = DeserializeCatalogEntry(kv.Value);
            if (entry != null)
            {
                _catalog[entry.Definition.CollectionName] = entry;
                _nextTableId = Math.Max(_nextTableId, entry.TableId + 1);
            }
        }
    }

    // ── Key helpers ──────────────────────────────────────────────────────────

    internal static byte[] BuildRowKey(int tableId, long rowId)
    {
        // Row keys are layout: [tableId:4 LE][rowId:8 BE-with-sign-flip].
        // tableId is little-endian because BuildTablePrefix uses the same encoding and only
        // needs to match exactly as a 4-byte prefix.
        // rowId is encoded as big-endian with the sign bit flipped so that lexicographic byte
        // comparison preserves signed numeric order. This is required for ScanRowKeyRange's
        // binary search and EnumerateRange iteration to return rows in numeric rowId order.
        var key = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(key, (uint)tableId);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(4), (ulong)rowId ^ 0x8000_0000_0000_0000UL);
        return key;
    }

    private static byte[] BuildTablePrefix(uint tableId)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, tableId);
        return prefix;
    }

    internal static (int TableId, long RowId) ParseRowKey(byte[] key)
    {
        var tableId = (int)BinaryPrimitives.ReadUInt32LittleEndian(key);
        var rowId = (long)(BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(4)) ^ 0x8000_0000_0000_0000UL);
        return (tableId, rowId);
    }

    private static readonly byte[] IndexEntryValue = new byte[] { 0x01 };

    private void PutEntry(byte[] key, byte[] value)
    {
        if (UsesDirectStore)
            _dataStore.Upsert(key, value);
        else
        {
            _memDict[key] = value;
            _memSortedKeysDirty = true;
        }
    }

    private void BulkMergeMemTable(IReadOnlyList<KeyValuePair<byte[], byte[]>> entries)
    {
        if (entries.Count == 0) return;

        for (int i = 0; i < entries.Count; i++)
        {
            var kv = entries[i];
            _memDict[kv.Key] = kv.Value;
        }
        _memSortedKeysDirty = true;
    }

    private void BulkDeleteMemTable(IReadOnlyList<byte[]> keys)
    {
        if (keys.Count == 0 || _memDict.Count == 0) return;

        for (int i = 0; i < keys.Count; i++)
            _memDict.Remove(keys[i]);
        _memSortedKeysDirty = true;
    }

    // ── Index key helpers ─────────────────────────────────────────────────

    internal static byte[] BuildIndexEntryKey(int indexId, byte[] sortKey, int tableId, long rowId)
    {
        // [0xFFFFFFFE:4][indexId:4 LE][sortKey...][tableId:4 LE][rowId:8 LE]
        var key = new byte[4 + 4 + sortKey.Length + 4 + 8];
        int offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(offset), IndexSentinel);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(offset), indexId);
        offset += 4;
        Buffer.BlockCopy(sortKey, 0, key, offset, sortKey.Length);
        offset += sortKey.Length;
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(offset), tableId);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(key.AsSpan(offset), rowId);
        return key;
    }

    internal static byte[] BuildIndexPrefix(int indexId)
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, IndexSentinel);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), indexId);
        return prefix;
    }

    internal static byte[] BuildIndexPrefixWithKey(int indexId, byte[] sortKey)
    {
        var prefix = new byte[8 + sortKey.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, IndexSentinel);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), indexId);
        Buffer.BlockCopy(sortKey, 0, prefix, 8, sortKey.Length);
        return prefix;
    }

    /// <summary>
    /// Build a search key with zero-filled [tableId:4][rowId:8] suffix so that
    /// binary search positions correctly within the SortedList instead of always
    /// returning ~0 for prefix-only keys.
    /// </summary>
    internal static byte[] BuildIndexPrefixWithKeyAndSuffix(int indexId, byte[] sortKey)
    {
        int totalLen = 8 + sortKey.Length + 12; // prefix + sortKey + tableId + rowId
        var key = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(key, IndexSentinel);
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4), indexId);
        if (sortKey.Length > 0)
            Buffer.BlockCopy(sortKey, 0, key, 8, sortKey.Length);
        // tableId and rowId remain zero-filled (last 12 bytes)
        return key;
    }

    internal static (int IndexId, int TableId, long RowId) ParseIndexEntryKey(byte[] key)
    {
        if (key.Length < 20) // 4 sentinel + 4 indexId + 4 tableId + 8 rowId minimum
            return (0, 0, 0);
        int offset = 4; // skip sentinel
        var indexId = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(offset));
        offset += 4;
        // Skip sortKey (variable length; tableId is 4 bytes from end - 12, rowId is last 8)
        var tableId = BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(key.Length - 12));
        var rowId = BinaryPrimitives.ReadInt64LittleEndian(key.AsSpan(key.Length - 8));
        return (indexId, tableId, rowId);
    }

    internal static (int TableId, long RowId) ParseIndexEntryKeyToTableRow(byte[] key)
    {
        var (_, tid, rid) = ParseIndexEntryKey(key);
        return (tid, rid);
    }

    private static byte[] BuildCatalogKey(string tableName, CatalogProperty property)
    {
        var nameBytes = Encoding.UTF8.GetBytes(tableName);
        var key = new byte[4 + nameBytes.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(key, CatalogSentinel);
        nameBytes.CopyTo(key.AsSpan(4));
        key[4 + nameBytes.Length] = (byte)property;
        return key;
    }

    private static byte[] BuildCatalogPrefix()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, CatalogSentinel);
        return prefix;
    }

    // ── Statistics key helpers ───────────────────────────────────────────────

    private static byte[] BuildStatsKey(int tableId)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(key, StatsSentinel);
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4), tableId);
        return key;
    }

    private static byte[] BuildStatsPrefix()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, StatsSentinel);
        return prefix;
    }

    private static bool StartsWithPrefix(byte[] key, byte[] prefix)
    {
        if (key.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (key[i] != prefix[i]) return false;
        return true;
    }

    // ── Catalog serialization ────────────────────────────────────────────────

    // ── Statistics persistence ───────────────────────────────────────────────

    internal void PersistStatistics(int tableId, TableStatistics stats)
    {
        var key = BuildStatsKey(tableId);
        var value = SerializeStatistics(stats);
        using (LockManager.TableWriteLock(0))
            _dataStore.Upsert(key, value);
    }

    internal void DeleteStatistics(int tableId)
    {
        var key = BuildStatsKey(tableId);
        using (LockManager.TableWriteLock(0))
            _dataStore.Delete(key);
    }

    internal IReadOnlyList<(int TableId, TableStatistics Stats)> LoadAllStatistics()
    {
        var result = new List<(int, TableStatistics)>();
        var statsPrefix = BuildStatsPrefix();
        // Range: [FD FF FF FF] inclusive to [FE FF FF FF] exclusive (= IndexSentinel)
        var toExclusive = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(toExclusive, IndexSentinel);

        foreach (var kv in _dataStore.EnumerateRange(statsPrefix, toExclusive))
        {
            if (kv.Key.Length < 8) continue;
            var tableId = BinaryPrimitives.ReadInt32LittleEndian(kv.Key.AsSpan(4));
            var stats = DeserializeStatistics(kv.Value);
            if (stats != null)
                result.Add((tableId, stats));
        }

        // Also check uncommitted MemTable entries (defensive — stats normally go direct to store).
        foreach (var kv in _memDict)
        {
            if (kv.Key.Length < 8) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(kv.Key) != StatsSentinel) continue;
            var tableId = BinaryPrimitives.ReadInt32LittleEndian(kv.Key.AsSpan(4));
            var stats = DeserializeStatistics(kv.Value);
            if (stats != null)
                result.Add((tableId, stats));
        }

        return result;
    }

    // ── Statistics serialization ─────────────────────────────────────────────

    private static byte[] SerializeStatistics(TableStatistics stats)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((byte)1); // version
        w.Write(stats.RowCount);
        w.Write(stats.AnalyzedAt.Ticks);
        w.Write(stats.Columns.Count);

        foreach (var (colName, col) in stats.Columns)
        {
            var nameBytes = Encoding.UTF8.GetBytes(colName);
            w.Write(nameBytes.Length);
            w.Write(nameBytes);
            w.Write(col.NullFraction);
            w.Write(col.DistinctCount);
            w.Write(col.AverageWidth);

            w.Write(col.MostCommonValues.Length);
            foreach (var (val, freq) in col.MostCommonValues)
            {
                WriteStatsObject(w, val);
                w.Write(freq);
            }

            w.Write(col.Histogram.Length);
            foreach (var bound in col.Histogram)
                WriteStatsObject(w, bound);
        }

        return ms.ToArray();
    }

    private static TableStatistics? DeserializeStatistics(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            var version = r.ReadByte();
            if (version != 1) return null;

            var rowCount = r.ReadInt64();
            var analyzedAtTicks = r.ReadInt64();
            var columnCount = r.ReadInt32();

            var columns = new Dictionary<string, ColumnStatistics>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < columnCount; i++)
            {
                var nameLen = r.ReadInt32();
                var colName = Encoding.UTF8.GetString(r.ReadBytes(nameLen));

                var nullFraction = r.ReadDouble();
                var distinctCount = r.ReadDouble();
                var averageWidth = r.ReadInt32();

                var mcvCount = r.ReadInt32();
                var mcv = new (object Value, double Frequency)[mcvCount];
                for (var j = 0; j < mcvCount; j++)
                {
                    var val = ReadStatsObject(r);
                    var freq = r.ReadDouble();
                    mcv[j] = (val!, freq);
                }

                var histCount = r.ReadInt32();
                var hist = new object[histCount];
                for (var j = 0; j < histCount; j++)
                    hist[j] = ReadStatsObject(r)!;

                columns[colName] = new ColumnStatistics
                {
                    NullFraction = nullFraction,
                    DistinctCount = distinctCount,
                    AverageWidth = averageWidth,
                    MostCommonValues = mcv,
                    Histogram = hist
                };
            }

            return new TableStatistics
            {
                RowCount = rowCount,
                AnalyzedAt = new DateTime(analyzedAtTicks, DateTimeKind.Utc),
                Columns = columns
            };
        }
        catch
        {
            return null; // corrupt/unknown data — stats are best-effort hints
        }
    }

    private static void WriteStatsObject(BinaryWriter w, object? val)
    {
        switch (val)
        {
            case null:
                w.Write((byte)0);
                break;
            case int i:
                w.Write((byte)1);
                w.Write(i);
                break;
            case long l:
                w.Write((byte)2);
                w.Write(l);
                break;
            case double d:
                w.Write((byte)3);
                w.Write(d);
                break;
            case string s:
                w.Write((byte)4);
                var sb = Encoding.UTF8.GetBytes(s);
                w.Write(sb.Length);
                w.Write(sb);
                break;
            case bool b:
                w.Write((byte)5);
                w.Write(b ? (byte)1 : (byte)0);
                break;
            case DateTime dt:
                w.Write((byte)6);
                w.Write(dt.Ticks);
                break;
            case float f:
                w.Write((byte)7);
                w.Write(f);
                break;
            default:
                // Fallback: serialize unknown types as their string representation.
                w.Write((byte)4);
                var fb = Encoding.UTF8.GetBytes(val.ToString() ?? "");
                w.Write(fb.Length);
                w.Write(fb);
                break;
        }
    }

    private static object? ReadStatsObject(BinaryReader r)
    {
        var tag = r.ReadByte();
        return tag switch
        {
            0 => null,
            1 => (object)r.ReadInt32(),
            2 => (object)r.ReadInt64(),
            3 => (object)r.ReadDouble(),
            4 => (object)Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())),
            5 => (object)(r.ReadByte() != 0),
            6 => (object)new DateTime(r.ReadInt64(), DateTimeKind.Utc),
            7 => (object)r.ReadSingle(),
            _ => throw new InvalidDataException($"Unknown stats object type tag: {tag}")
        };
    }



    private static byte[] SerializeCatalogEntry(TableEntry entry)
    {
        // Binary format:
        //   [tableId: 4 bytes LE]
        //   [nextRowId: 8 bytes LE]
        //   [collectionNameLength: 4 bytes LE]
        //   [collectionName: UTF8 bytes]
        //   [columnCount: 4 bytes LE]
        //   for each column:
        //     [nameLength: 4 bytes LE][name: UTF8][type: 1 byte][isNullable: 1 byte][isPk: 1 byte][isUnique: 1 byte]
        //     [collationLength: 1 byte][collation: UTF8 bytes if length > 0]

        var nameBytes = Encoding.UTF8.GetBytes(entry.Definition.CollectionName);
        var size = 4 + 8 + 4 + nameBytes.Length + 4;
        foreach (var col in entry.Definition.Columns)
        {
            size += 4 + Encoding.UTF8.GetByteCount(col.Name) + 4;
            size += 1 + (col.Collation != null ? Encoding.UTF8.GetByteCount(col.Collation) : 0);
        }

        // Size for indexes.
        var indexes = entry.Definition.Indexes;
        size += 4; // index count
        foreach (var idx in indexes)
        {
            size += 4 + Encoding.UTF8.GetByteCount(idx.IndexName); // name
            size += 4; // column count
            foreach (var colName in idx.ColumnNames)
                size += 4 + Encoding.UTF8.GetByteCount(colName);
            size += 2; // isUnique + flags (bit0=isInternal, bit1=indexType)
            size += 4; // indexId
        }

        // Size for CHECK constraints (appended after indexes; backward-compatible).
        var checkConstraints = entry.Definition.CheckConstraints;
        size += 4; // check count
        if (checkConstraints != null)
        {
            foreach (var chk in checkConstraints)
            {
                size += 4 + Encoding.UTF8.GetByteCount(chk.Name);
                size += 4 + Encoding.UTF8.GetByteCount(chk.Expression);
            }
        }

        // Size for FOREIGN KEY constraints (appended after CHECK constraints).
        var foreignKeys = entry.Definition.ForeignKeys;
        size += 4; // FK count
        if (foreignKeys != null)
        {
            foreach (var fk in foreignKeys)
            {
                size += 4 + Encoding.UTF8.GetByteCount(fk.ConstraintName); // constraint name
                size += 4; // FK column count
                foreach (var colName in fk.ColumnNames)
                    size += 4 + Encoding.UTF8.GetByteCount(colName);
                size += 4 + Encoding.UTF8.GetByteCount(fk.ReferencedCollection); // ref table name
                size += 4; // ref column count
                foreach (var colName in fk.ReferencedColumns)
                    size += 4 + Encoding.UTF8.GetByteCount(colName);
                size += 1; // onDelete
                size += 1; // onUpdate
            }
        }

        var buf = new byte[size];
        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), entry.TableId);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset), entry.NextRowId);
        offset += 8;

        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), nameBytes.Length);
        offset += 4;
        nameBytes.CopyTo(buf.AsSpan(offset));
        offset += nameBytes.Length;

        var cols = entry.Definition.Columns;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cols.Count);
        offset += 4;

        foreach (var col in cols)
        {
            var cn = Encoding.UTF8.GetBytes(col.Name);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cn.Length);
            offset += 4;
            cn.CopyTo(buf.AsSpan(offset));
            offset += cn.Length;

            buf[offset++] = (byte)col.Type;
            buf[offset++] = (byte)(col.IsNullable ? 1 : 0);
            buf[offset++] = (byte)(col.IsPrimaryKey ? 1 : 0);
            buf[offset++] = (byte)(col.IsUnique ? 1 : 0);

            if (col.Collation != null)
            {
                var collBytes = Encoding.UTF8.GetBytes(col.Collation);
                buf[offset++] = (byte)collBytes.Length;
                collBytes.CopyTo(buf.AsSpan(offset));
                offset += collBytes.Length;
            }
            else
            {
                buf[offset++] = 0;
            }
        }

        // Serialize index definitions + index IDs.
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), indexes.Count);
        offset += 4;

        foreach (var idx in indexes)
        {
            var iname = Encoding.UTF8.GetBytes(idx.IndexName);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), iname.Length);
            offset += 4;
            iname.CopyTo(buf.AsSpan(offset));
            offset += iname.Length;

            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), idx.ColumnNames.Count);
            offset += 4;
            foreach (var colName in idx.ColumnNames)
            {
                var cn = Encoding.UTF8.GetBytes(colName);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cn.Length);
                offset += 4;
                cn.CopyTo(buf.AsSpan(offset));
                offset += cn.Length;
            }

            buf[offset++] = (byte)(idx.IsUnique ? 1 : 0);
            // Flags byte: bit0 = isInternal, bit1 = IndexType (0=BTree, 1=Gin)
            var flags = (byte)((idx.IsInternal ? 1 : 0) | ((int)idx.IndexType << 1));
            buf[offset++] = flags;

            // Write indexId from entry.IndexIds.
            var indexId = entry.IndexIds.TryGetValue(idx.IndexName, out var id) ? id : 0;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), indexId);
            offset += 4;
        }

        // Serialize CHECK constraints (name + raw expression text).
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), checkConstraints?.Count ?? 0);
        offset += 4;
        if (checkConstraints != null)
        {
            foreach (var chk in checkConstraints)
            {
                var cnameBytes = Encoding.UTF8.GetBytes(chk.Name);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cnameBytes.Length);
                offset += 4;
                cnameBytes.CopyTo(buf.AsSpan(offset));
                offset += cnameBytes.Length;

                var exprBytes = Encoding.UTF8.GetBytes(chk.Expression);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), exprBytes.Length);
                offset += 4;
                exprBytes.CopyTo(buf.AsSpan(offset));
                offset += exprBytes.Length;
            }
        }

        // Serialize FOREIGN KEY constraints (appended after CHECK constraints).
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), foreignKeys?.Count ?? 0);
        offset += 4;
        if (foreignKeys != null)
        {
            foreach (var fk in foreignKeys)
            {
                // Constraint name
                var fkNameBytes = Encoding.UTF8.GetBytes(fk.ConstraintName);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), fkNameBytes.Length);
                offset += 4;
                fkNameBytes.CopyTo(buf.AsSpan(offset));
                offset += fkNameBytes.Length;

                // FK column names
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), fk.ColumnNames.Count);
                offset += 4;
                foreach (var colName in fk.ColumnNames)
                {
                    var cnBytes = Encoding.UTF8.GetBytes(colName);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cnBytes.Length);
                    offset += 4;
                    cnBytes.CopyTo(buf.AsSpan(offset));
                    offset += cnBytes.Length;
                }

                // Referenced table
                var refTableBytes = Encoding.UTF8.GetBytes(fk.ReferencedCollection);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), refTableBytes.Length);
                offset += 4;
                refTableBytes.CopyTo(buf.AsSpan(offset));
                offset += refTableBytes.Length;

                // Referenced column names
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), fk.ReferencedColumns.Count);
                offset += 4;
                foreach (var colName in fk.ReferencedColumns)
                {
                    var cnBytes = Encoding.UTF8.GetBytes(colName);
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset), cnBytes.Length);
                    offset += 4;
                    cnBytes.CopyTo(buf.AsSpan(offset));
                    offset += cnBytes.Length;
                }

                // Actions
                buf[offset++] = (byte)fk.OnDelete;
                buf[offset++] = (byte)fk.OnUpdate;
            }
        }

        return buf;
    }

    private TableEntry? DeserializeCatalogEntry(byte[] data)
    {
        try
        {
            var offset = 0;
            var tableId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var nextRowId = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
            offset += 8;

            var nameLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var name = Encoding.UTF8.GetString(data, offset, nameLen);
            offset += nameLen;

            var colCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += 4;

            var columns = new List<SqlColumnDefinition>(colCount);
            for (var i = 0; i < colCount; i++)
            {
                var cnLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                offset += 4;
                var cn = Encoding.UTF8.GetString(data, offset, cnLen);
                offset += cnLen;

                var type = (SqlScalarType)data[offset++];
                var isNullable = data[offset++] == 1;
                var isPk = data[offset++] == 1;
                var isUnique = data[offset++] == 1;

                // Read collation (backward-compatible: old catalogs have no trailing bytes).
                string? collation = null;
                if (offset < data.Length)
                {
                    var collLen = data[offset++];
                    if (collLen > 0)
                    {
                        collation = Encoding.UTF8.GetString(data, offset, collLen);
                        offset += collLen;
                    }
                }

                columns.Add(new SqlColumnDefinition(cn, type, isNullable, isPk, isUnique, collation));
            }

            // Deserialize indexes if present.
            var indexDefs = new List<SqlIndexDefinition>();
            var indexIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (offset + 4 <= data.Length)
            {
                var idxCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                offset += 4;

                for (int i = 0; i < idxCount; i++)
                {
                    var inameLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                    offset += 4;
                    var iname = Encoding.UTF8.GetString(data, offset, inameLen);
                    offset += inameLen;

                    var idxColCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                    offset += 4;
                    var colNames = new List<string>(idxColCount);
                    for (int j = 0; j < idxColCount; j++)
                    {
                        var cnLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        colNames.Add(Encoding.UTF8.GetString(data, offset, cnLen));
                        offset += cnLen;
                    }

                    var isUnique = data[offset++] == 1;
                    var flags = data[offset++];
                    var isInternal = (flags & 1) == 1;
                    var indexType = (SqlIndexType)((flags >> 1) & 1);

                    var indexId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                    offset += 4;

                    var idxDef = new SqlIndexDefinition(iname, colNames, isUnique)
                    {
                        IsInternal = isInternal,
                        IndexType = indexType
                    };
                    indexDefs.Add(idxDef);
                    indexIds[iname] = indexId;

                    _nextIndexId = Math.Max(_nextIndexId, indexId + 1);
                }
            }

            // Deserialize CHECK constraints if present (appended after indexes).
            List<SqlCheckConstraint>? checkConstraints = null;
            if (offset + 4 <= data.Length)
            {
                var checkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                offset += 4;
                if (checkCount > 0)
                {
                    checkConstraints = new List<SqlCheckConstraint>(checkCount);
                    for (int i = 0; i < checkCount; i++)
                    {
                        var nLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var cname = Encoding.UTF8.GetString(data, offset, nLen);
                        offset += nLen;

                        var eLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var expr = Encoding.UTF8.GetString(data, offset, eLen);
                        offset += eLen;

                        checkConstraints.Add(new SqlCheckConstraint(cname, expr));
                    }
                }
            }

            // Deserialize FOREIGN KEY constraints if present (appended after CHECK constraints).
            List<SqlForeignKeyDefinition>? foreignKeys = null;
            if (offset + 4 <= data.Length)
            {
                var fkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                offset += 4;
                if (fkCount > 0)
                {
                    foreignKeys = new List<SqlForeignKeyDefinition>(fkCount);
                    for (int i = 0; i < fkCount; i++)
                    {
                        // Constraint name
                        var fkNameLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var fkName = Encoding.UTF8.GetString(data, offset, fkNameLen);
                        offset += fkNameLen;

                        // FK column names
                        var fkColCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var fkCols = new List<string>(fkColCount);
                        for (int j = 0; j < fkColCount; j++)
                        {
                            var cnLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                            offset += 4;
                            fkCols.Add(Encoding.UTF8.GetString(data, offset, cnLen));
                            offset += cnLen;
                        }

                        // Referenced table
                        var refTableLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var refTable = Encoding.UTF8.GetString(data, offset, refTableLen);
                        offset += refTableLen;

                        // Referenced column names
                        var refColCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                        offset += 4;
                        var refCols = new List<string>(refColCount);
                        for (int j = 0; j < refColCount; j++)
                        {
                            var cnLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                            offset += 4;
                            refCols.Add(Encoding.UTF8.GetString(data, offset, cnLen));
                            offset += cnLen;
                        }

                        // Actions
                        var onDelete = (SqlForeignKeyAction)data[offset++];
                        var onUpdate = (SqlForeignKeyAction)data[offset++];

                        foreignKeys.Add(new SqlForeignKeyDefinition(
                            fkName, fkCols, refTable, refCols, onDelete, onUpdate));
                    }
                }
            }

            return new TableEntry
            {
                TableId = tableId,
                NextRowId = nextRowId,
                Definition = new SqlTableDefinition(name, columns, indexDefs, foreignKeys, null, checkConstraints),
                IndexIds = indexIds
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private int AllocateTableId()
    {
        var id = _nextTableId++;
        PersistNextTableId();
        return id;
    }

    private void PersistNextTableId()
    {
        var key = BuildCatalogKey("__nextTableId", CatalogProperty.NextTableId);
        var value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, _nextTableId);
        _memDict[key] = value;
        _memSortedKeysDirty = true;
    }

    private TableEntry GetEntryForTable(int tableId)
    {
        foreach (var entry in _catalog.Values)
        {
            if (entry.TableId == tableId)
                return entry;
        }
        throw new WalhallaException($"Table with id {tableId} not found.");
    }

    private static long InterlockedIncrement(ref long location)
    {
        return System.Threading.Interlocked.Increment(ref location);
    }

    internal sealed class TableEntry
    {
        public int TableId;
        public SqlTableDefinition Definition = null!;
        public long NextRowId;
        public Dictionary<string, int> IndexIds = new(StringComparer.OrdinalIgnoreCase);
        internal WalhallaEngine.IndexMeta[]? CachedIndexMeta;
    }

    private enum CatalogProperty : byte
    {
        Definition = 0,
        NextTableId = 1
    }

    public void Dispose()
    {
        _orphanCts.Cancel();
        LockManager.Dispose();
        _catalogLockManager.Dispose();
        _dataStore.Dispose();
        _odsPager?.Dispose();
        _groupCommit?.Dispose();
        _walLog?.Dispose();
        foreach (var sidecar in _sidecars.Values)
            sidecar.Dispose();
        _sidecars.Clear();
        _orphanCts.Dispose();
    }

    /// <summary>
    /// Returns aggregated telemetry for all active blob sidecars.
    /// </summary>
    public (long TotalBytesAppended, long TotalBlobsAppended, long TotalBytesCompacted,
           long CompactionCount, long OrphanRowsProcessed, long OrphanBlobsReclaimed,
           long OrphanBytesReclaimed) GetBlobSidecarStats()
    {
        long bytesAppended = 0, blobsAppended = 0, bytesCompacted = 0, compactions = 0;
        foreach (var sc in _sidecars.Values)
        {
            bytesAppended += sc.TotalBytesAppended;
            blobsAppended += sc.TotalBlobsAppended;
            bytesCompacted += sc.TotalBytesCompacted;
            compactions += sc.CompactionCount;
        }
        return (bytesAppended, blobsAppended, bytesCompacted, compactions,
                Interlocked.Read(ref _orphanRowsProcessed),
                Interlocked.Read(ref _orphanBlobsReclaimed),
                Interlocked.Read(ref _orphanBytesReclaimed));
    }

    // ── Blob Sidecar (Phase H) ───────────────────────────────────────────────

    private BlobSidecarFile GetOrCreateSidecar(int tableId)
    {
        if (!UsesBlobSidecar)
            throw new InvalidOperationException("Blob sidecar is not enabled for this storage mode.");

        return _sidecars.GetOrAdd(tableId, id =>
        {
            var path = Path.Combine(_options.BlobSidecarRootDirectory, $"table_{id}", "blobs.dat");
            return new BlobSidecarFile(path, inMemory: false);
        });
    }

    /// <summary>
    /// Offloads large binary values from the row into the blob sidecar before encoding.
    /// Returns a new row-values array where byte[] values exceeding the threshold
    /// are replaced with <see cref="BlobRef"/>.
    /// </summary>
    public object?[] OffloadBlobs(int tableId, object?[] values, SqlTableDefinition def)
    {
        if (!UsesBlobSidecar)
            return values;

        bool offloaded = false;
        for (int i = 0; i < def.Columns.Count; i++)
        {
            if (def.Columns[i].Type != SqlScalarType.Binary)
                continue;
            if (values[i] is not byte[] raw || raw.Length <= _options.BlobInliningThreshold)
                continue;

            if (!offloaded)
            {
                values = (object?[])values.Clone();
                offloaded = true;
            }

            var sidecar = GetOrCreateSidecar(tableId);
            values[i] = sidecar.Append(raw);
        }
        return values;
    }

    /// <summary>
    /// Resolves any <see cref="BlobRef"/> values in the decoded row into
    /// <see cref="PendingBlobValue"/> instances backed by the sidecar stream.
    /// When <paramref name="columnIndices"/> is provided, <paramref name="values"/>
    /// is a projection and index <i>i</i> corresponds to <paramref name="def"/>
    /// column <code>columnIndices[i]</code>.
    /// </summary>
    public void ResolveBlobs(int tableId, object?[] values, SqlTableDefinition def, int[]? columnIndices = null)
    {
        if (!UsesBlobSidecar)
            return;

        BlobSidecarFile? sidecar = null;
        int count = columnIndices?.Length ?? values.Length;
        for (int i = 0; i < count; i++)
        {
            int colIdx = columnIndices?[i] ?? i;
            if (colIdx < 0 || colIdx >= def.Columns.Count)
                continue;
            if (def.Columns[colIdx].Type != SqlScalarType.Binary)
                continue;
            if (values[i] is not BlobRef blobRef)
                continue;

            sidecar ??= GetOrCreateSidecar(tableId);
            values[i] = new PendingBlobValue(() => sidecar.OpenStream(blobRef), blobRef);
        }
    }

    // ── MVCC prune orphan tracking (Phase H.5) ─────────────────────────────

    private async Task ProcessOrphanQueueAsync()
    {
        while (!_orphanCts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _orphanCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_pendingOrphanRows.TryDequeue(out var item))
            {
                var (tableId, encoded) = item;
                if (!_catalog.Values.Any(t => t.TableId == tableId))
                    continue; // table may have been dropped

                var entry = _catalog.Values.First(t => t.TableId == tableId);
                var def = entry.Definition;
                var values = RowCodec.DecodeToArray(encoded, def);
                Interlocked.Increment(ref _orphanRowsProcessed);
                for (int i = 0; i < def.Columns.Count; i++)
                {
                    if (def.Columns[i].Type == SqlScalarType.Binary && values[i] is BlobRef blobRef)
                    {
                        var list = _orphanRefs.GetOrAdd(tableId, _ => new List<BlobRef>());
                        lock (list) { list.Add(blobRef); }
                        Interlocked.Increment(ref _orphanBlobsReclaimed);
                        Interlocked.Add(ref _orphanBytesReclaimed, blobRef.Length);
                    }
                }
            }
        }
    }

    /// <summary>Returns the accumulated orphan BlobRefs for a table (for VACUUM/Compaction).</summary>
    internal IReadOnlyList<BlobRef> GetOrphanBlobRefs(int tableId)
    {
        if (_orphanRefs.TryGetValue(tableId, out var list))
        {
            lock (list) { return list.ToArray(); }
        }
        return Array.Empty<BlobRef>();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Adapter: IStorageTransaction → ITransaction<byte[], byte[]>
// ═══════════════════════════════════════════════════════════════════════════

file sealed class StorageTransactionAdapter : ITransaction<byte[], byte[]>
{
    private readonly IStorageTransaction _tx;

    public StorageTransactionAdapter(IStorageTransaction tx)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
    }

    public ulong TxId => _tx.TxId;
    public ulong StartSequence => _tx.TxId; // best available proxy
    public TransactionStatus Status => _tx.Status;

    public bool TryGet(byte[] key, out byte[] value) => _tx.TryGet(key, out value);
    public bool ContainsKey(byte[] key) => _tx.TryGet(key, out _);
    public void Upsert(byte[] key, byte[] value) => _tx.Upsert(key, value);
    public void Delete(byte[] key) => _tx.Delete(key);
    public void Commit() => _tx.Commit();
    public void Rollback() => _tx.Rollback();
    public void Dispose() => _tx.Dispose();
}
