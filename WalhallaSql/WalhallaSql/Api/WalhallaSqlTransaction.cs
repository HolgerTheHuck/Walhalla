using System;
using System.Collections.Generic;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;

namespace WalhallaSql;

public sealed class WalhallaSqlTransaction : IDisposable
{
    private readonly WalhallaEngine _engine;
    private readonly List<DeferredWrite> _writes = new();
    private readonly List<DeferredIndexOp> _indexOps = new();
    // Read-your-own-writes: keyed by (tableId, rowId)
    private readonly Dictionary<(int TableId, long RowId), byte[]> _insertedRows = new();
    private readonly Dictionary<(int TableId, long RowId), byte[]> _updatedRows = new();
    private readonly HashSet<(int TableId, long RowId)> _deletedRows = new();
    // Table-level locking: tables locked by this transaction (legacy, being phased out)
    private readonly HashSet<int> _lockedTables = new();
    // Row-level locking: rows locked by this transaction
    private readonly HashSet<(int TableId, long RowId)> _lockedRows = new();
    private readonly HashSet<(int TableId, long RowId)> _exclusiveRows = new();
    private bool _committed;
    private bool _rolledBack;
    private ITransaction<byte[], byte[]>? _storageTransaction;

    internal WalhallaSqlTransaction(WalhallaEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    internal ITransaction<byte[], byte[]>? StorageTransaction => _storageTransaction;

    internal void SetStorageTransaction(ITransaction<byte[], byte[]> tx)
    {
        _storageTransaction = tx;
    }

    internal IsolationLevel? StorageIsolationLevel { get; private set; }

    internal void SetIsolationLevel(IsolationLevel level)
    {
        StorageIsolationLevel = level;
    }

    internal IReadOnlyList<DeferredWrite> Writes => _writes;

    internal IReadOnlyList<DeferredIndexOp> IndexOps => _indexOps;

    internal IReadOnlyCollection<int> LockedTables => _lockedTables;

    internal bool HoldsLock(int tableId) => _lockedTables.Contains(tableId);

    internal void TrackLock(int tableId)
    {
        _lockedTables.Add(tableId);
    }

    // ── Row-level lock tracking ────────────────────────────────────────────

    internal bool HoldsRowLock(int tableId, long rowId) => _lockedRows.Contains((tableId, rowId));

    internal void MarkRowExclusive(int tableId, long rowId) => _exclusiveRows.Add((tableId, rowId));

    internal void TrackRowLock(int tableId, long rowId, bool exclusive)
    {
        _lockedRows.Add((tableId, rowId));
        if (exclusive)
            _exclusiveRows.Add((tableId, rowId));
    }

    internal void UntrackRowLock(int tableId, long rowId)
    {
        _lockedRows.Remove((tableId, rowId));
        _exclusiveRows.Remove((tableId, rowId));
    }

    internal void ClearRowLocks()
    {
        _lockedRows.Clear();
        _exclusiveRows.Clear();
    }

    internal List<(int TableId, long RowId)> GetTrackedRowLocks()
        => new(_lockedRows);

    /// <summary>
    /// Returns row locks with (tableId, rowId) greater than the given key.
    /// Used for deadlock-preventing lock re-ordering.
    /// </summary>
    internal List<(int TableId, long RowId)> GetHigherRowLocks(int tableId, long rowId)
    {
        var result = new List<(int, long)>();
        foreach (var (tId, rId) in _lockedRows)
        {
            if (tId > tableId || (tId == tableId && rId > rowId))
                result.Add((tId, rId));
        }
        // Sort ascending so re-acquisition is in order
        result.Sort((a, b) =>
        {
            int cmp = a.Item1.CompareTo(b.Item1);
            return cmp != 0 ? cmp : a.Item2.CompareTo(b.Item2);
        });
        return result;
    }

    internal void BufferInsert(int tableId, long rowId, byte[] encodedRow)
    {
        _writes.Add(new DeferredWrite(WriteOp.Insert, tableId, rowId, encodedRow));
        _insertedRows[(tableId, rowId)] = encodedRow;
        _deletedRows.Remove((tableId, rowId)); // undoes prior delete in same tx
    }

    internal void BufferUpdate(int tableId, long rowId, byte[] encodedRow)
    {
        _writes.Add(new DeferredWrite(WriteOp.Update, tableId, rowId, encodedRow));
        _updatedRows[(tableId, rowId)] = encodedRow;
        // If this row was previously inserted in the same tx, update the inserted buffer too
        if (_insertedRows.ContainsKey((tableId, rowId)))
            _insertedRows[(tableId, rowId)] = encodedRow;
    }

    internal void BufferDelete(int tableId, long rowId)
    {
        _writes.Add(new DeferredWrite(WriteOp.Delete, tableId, rowId, null));
        _deletedRows.Add((tableId, rowId));
        _insertedRows.Remove((tableId, rowId));
        _updatedRows.Remove((tableId, rowId));
    }

    internal void BufferIndexInsert(int indexId, byte[] sortKey, int tableId, long rowId)
    {
        _indexOps.Add(new DeferredIndexOp(IndexOpType.Insert, indexId, sortKey, tableId, rowId));
    }

    internal void BufferIndexDelete(int indexId, byte[] sortKey, int tableId, long rowId)
    {
        _indexOps.Add(new DeferredIndexOp(IndexOpType.Delete, indexId, sortKey, tableId, rowId));
    }

    /// <summary>Check if this transaction has a buffered version of the row.</summary>
    internal bool TryGetBufferedRow(int tableId, long rowId, out byte[]? encodedRow)
    {
        var key = (tableId, rowId);
        if (_deletedRows.Contains(key))
        {
            encodedRow = null;
            return true;
        }
        if (_insertedRows.TryGetValue(key, out var inserted))
        {
            encodedRow = inserted;
            return true;
        }
        if (_updatedRows.TryGetValue(key, out var updated))
        {
            encodedRow = updated;
            return true;
        }
        encodedRow = null;
        return false;
    }

    /// <summary>Returns true if this row was deleted by this transaction.</summary>
    internal bool IsDeleted(int tableId, long rowId)
    {
        return _deletedRows.Contains((tableId, rowId));
    }

    /// <summary>Returns the set of table IDs affected by INSERTs in this transaction.</summary>
    internal HashSet<int> GetInsertedTableIds()
    {
        var set = new HashSet<int>();
        foreach (var kv in _insertedRows.Keys)
            set.Add(kv.TableId);
        return set;
    }

    /// <summary>Returns all inserted rows for a given table.</summary>
    internal List<(long RowId, byte[] EncodedRow)> GetInsertedRows(int tableId)
    {
        var result = new List<(long, byte[])>();
        foreach (var kv in _insertedRows)
        {
            if (kv.Key.TableId == tableId)
                result.Add((kv.Key.RowId, kv.Value));
        }
        return result;
    }

    public void Commit()
    {
        if (_committed || _rolledBack)
            throw new InvalidOperationException("Transaction already completed.");
        _committed = true;
        _engine.CommitTransaction(this);
    }

    public void Rollback()
    {
        if (_committed || _rolledBack)
            return; // idempotent
        _rolledBack = true;
        ClearBufferedChanges();
        _engine.RollbackTransaction(this);
    }

    /// <summary>
    /// Löscht alle im Arbeitsspeicher gepufferten Änderungen (Insert/Update/Delete),
    /// sodass ein Rollback die SQL-Engine-Transaktion konsistent zurücksetzt.
    /// </summary>
    internal void ClearBufferedChanges()
    {
        _writes.Clear();
        _indexOps.Clear();
        _insertedRows.Clear();
        _updatedRows.Clear();
        _deletedRows.Clear();
        _lockedRows.Clear();
        _exclusiveRows.Clear();
    }

    // ── Savepoints ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, SavepointState> _savepoints = new(StringComparer.OrdinalIgnoreCase);

    public void Savepoint(string name)
    {
        if (_committed || _rolledBack)
            throw new InvalidOperationException("Transaction already completed.");

        _savepoints[name] = new SavepointState(
            _writes.Count,
            _indexOps.Count,
            new Dictionary<(int, long), byte[]>(_insertedRows),
            new Dictionary<(int, long), byte[]>(_updatedRows),
            new HashSet<(int, long)>(_deletedRows),
            new HashSet<(int, long)>(_lockedRows),
            new HashSet<(int, long)>(_exclusiveRows));
    }

    public void RollbackTo(string name)
    {
        if (_committed || _rolledBack)
            throw new InvalidOperationException("Transaction already completed.");
        if (!_savepoints.TryGetValue(name, out var state))
            throw new InvalidOperationException($"Savepoint '{name}' not found.");

        // Truncate writes list.
        if (_writes.Count > state.WriteCount)
            _writes.RemoveRange(state.WriteCount, _writes.Count - state.WriteCount);

        // Truncate index ops list.
        if (_indexOps.Count > state.IndexOpCount)
            _indexOps.RemoveRange(state.IndexOpCount, _indexOps.Count - state.IndexOpCount);

        // Restore dictionaries.
        _insertedRows.Clear();
        foreach (var kv in state.InsertedRows)
            _insertedRows[kv.Key] = kv.Value;

        _updatedRows.Clear();
        foreach (var kv in state.UpdatedRows)
            _updatedRows[kv.Key] = kv.Value;

        _deletedRows.Clear();
        foreach (var key in state.DeletedRows)
            _deletedRows.Add(key);

        // Restore row-lock state.
        _lockedRows.Clear();
        foreach (var key in state.LockedRows)
            _lockedRows.Add(key);
        _exclusiveRows.Clear();
        foreach (var key in state.ExclusiveRows)
            _exclusiveRows.Add(key);

        // Keep the savepoint after rollback (standard SQL behavior).
    }

    public void Release(string name)
    {
        if (_committed || _rolledBack)
            throw new InvalidOperationException("Transaction already completed.");
        _savepoints.Remove(name);
    }

    private sealed record SavepointState(
        int WriteCount,
        int IndexOpCount,
        Dictionary<(int TableId, long RowId), byte[]> InsertedRows,
        Dictionary<(int TableId, long RowId), byte[]> UpdatedRows,
        HashSet<(int TableId, long RowId)> DeletedRows,
        HashSet<(int TableId, long RowId)> LockedRows,
        HashSet<(int TableId, long RowId)> ExclusiveRows);

    public void Dispose()
    {
        if (!_committed && !_rolledBack)
            Rollback();
        _storageTransaction?.Dispose();
        _storageTransaction = null;
    }
}

internal enum WriteOp { Insert, Update, Delete }

internal readonly struct DeferredWrite
{
    public WriteOp Op { get; }
    public int TableId { get; }
    public long RowId { get; }
    public byte[]? EncodedRow { get; }

    public DeferredWrite(WriteOp op, int tableId, long rowId, byte[]? encodedRow)
    {
        Op = op;
        TableId = tableId;
        RowId = rowId;
        EncodedRow = encodedRow;
    }
}

internal enum IndexOpType { Insert, Delete }

internal readonly struct DeferredIndexOp
{
    public IndexOpType Op { get; }
    public int IndexId { get; }
    public byte[] SortKey { get; }
    public int TableId { get; }
    public long RowId { get; }

    public DeferredIndexOp(IndexOpType op, int indexId, byte[] sortKey, int tableId, long rowId)
    {
        Op = op;
        IndexId = indexId;
        SortKey = sortKey;
        TableId = tableId;
        RowId = rowId;
    }
}
