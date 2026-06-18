using System;
using System.Collections.Generic;
using System.Threading;

namespace WalhallaSql.Storage;

/// <summary>
/// Row-level lock manager with deadlock prevention via ordered acquisition.
///
/// Two lock tiers:
///   Table locks  — per-table ReaderWriterLockSlim, held briefly during BPlusTree operations.
///   Row locks    — shared/exclusive, held for transaction duration.
///
/// Lock ordering: table locks before row locks; tables by ascending tableId; rows by ascending (tableId, rowId).
/// </summary>
internal sealed class RowLockManager : IDisposable
{
    // ── Table-level locks (short-lived, for BPlusTree structure protection) ────

    private ReaderWriterLockSlim?[] _perTableLocks = new ReaderWriterLockSlim[64];
    private readonly object _tableLockInitSync = new();

    public TableReadLockToken TableReadLock(int tableId)
    {
        var rwl = GetOrCreateTableRwl(tableId);
        rwl.EnterReadLock();
        return new TableReadLockToken(rwl);
    }

    public TableWriteLockToken TableWriteLock(int tableId)
    {
        var rwl = GetOrCreateTableRwl(tableId);
        rwl.EnterWriteLock();
        return new TableWriteLockToken(rwl);
    }

    private ReaderWriterLockSlim GetOrCreateTableRwl(int tableId)
    {
        var arr = _perTableLocks;
        if (tableId < arr.Length)
        {
            var existing = arr[tableId];
            if (existing != null)
                return existing;
        }
        return GetOrCreateTableRwlSlow(tableId);
    }

    private ReaderWriterLockSlim GetOrCreateTableRwlSlow(int tableId)
    {
        lock (_tableLockInitSync)
        {
            if (tableId >= _perTableLocks.Length)
            {
                var newArr = new ReaderWriterLockSlim?[Math.Max(tableId + 1, _perTableLocks.Length * 2)];
                Array.Copy(_perTableLocks, newArr, _perTableLocks.Length);
                _perTableLocks = newArr;
            }
            var existing = _perTableLocks[tableId];
            if (existing != null)
                return existing;
            var rwl = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            _perTableLocks[tableId] = rwl;
            return rwl;
        }
    }

    internal readonly struct TableReadLockToken : IDisposable
    {
        private readonly ReaderWriterLockSlim? _rwl;
        internal TableReadLockToken(ReaderWriterLockSlim rwl) => _rwl = rwl;
        public void Dispose() => _rwl?.ExitReadLock();
    }

    internal readonly struct TableWriteLockToken : IDisposable
    {
        private readonly ReaderWriterLockSlim? _rwl;
        internal TableWriteLockToken(ReaderWriterLockSlim rwl) => _rwl = rwl;
        public void Dispose() => _rwl?.ExitWriteLock();
    }

    // ── Row-level locks (held for transaction duration) ────────────────────────

    private readonly Dictionary<(int TableId, long RowId), RowLockEntry> _rowLocks = new();
    private readonly object _sync = new();

    public void AcquireRowShared(WalhallaSqlTransaction tx, int tableId, long rowId)
    {
        AcquireRowLock(tx, tableId, rowId, exclusive: false);
    }

    public void AcquireRowExclusive(WalhallaSqlTransaction tx, int tableId, long rowId)
    {
        AcquireRowLock(tx, tableId, rowId, exclusive: true);
    }

    private void AcquireRowLock(WalhallaSqlTransaction tx, int tableId, long rowId, bool exclusive)
    {
        if (tx.HoldsRowLock(tableId, rowId))
        {
            if (exclusive)
                tx.MarkRowExclusive(tableId, rowId);
            return;
        }

        var heldHigher = tx.GetHigherRowLocks(tableId, rowId);

        if (heldHigher.Count > 0)
        {
            LockRowInternal(tx, tableId, rowId, exclusive, heldHigher);
        }
        else
        {
            LockRowObject(tableId, rowId, exclusive, tx);
        }
    }

    private void LockRowObject(int tableId, long rowId, bool exclusive, WalhallaSqlTransaction tx)
    {
        var key = (tableId, rowId);
        lock (_sync)
        {
            if (!_rowLocks.TryGetValue(key, out var entry))
            {
                entry = new RowLockEntry();
                _rowLocks[key] = entry;
            }

            if (exclusive)
            {
                while (entry.HolderCount > 0)
                    Monitor.Wait(_sync);
                entry.ExclusiveHolder = tx;
                entry.HolderCount = 1;
            }
            else
            {
                while (entry.ExclusiveHolder != null)
                    Monitor.Wait(_sync);
                entry.SharedHolders.Add(tx);
                entry.HolderCount++;
            }
        }
        tx.TrackRowLock(tableId, rowId, exclusive);
    }

    private void LockRowInternal(WalhallaSqlTransaction tx, int tableId, long rowId,
        bool exclusive, List<(int TableId, long RowId)> heldHigher)
    {
        foreach (var h in heldHigher)
            ReleaseRowLockInternal(tx, h.TableId, h.RowId);

        LockRowObject(tableId, rowId, exclusive, tx);

        foreach (var h in heldHigher)
            LockRowObject(h.TableId, h.RowId, exclusive: true, tx);
    }

    private void ReleaseRowLockInternal(WalhallaSqlTransaction tx, int tableId, long rowId)
    {
        var key = (tableId, rowId);
        lock (_sync)
        {
            if (!_rowLocks.TryGetValue(key, out var entry))
                return;

            if (entry.ExclusiveHolder == tx)
            {
                entry.ExclusiveHolder = null;
                entry.HolderCount = 0;
                if (entry.SharedHolders.Count == 0)
                    _rowLocks.Remove(key);
                Monitor.PulseAll(_sync);
            }
            else if (entry.SharedHolders.Remove(tx))
            {
                entry.HolderCount--;
                if (entry.HolderCount == 0)
                    _rowLocks.Remove(key);
                Monitor.PulseAll(_sync);
            }
        }
        tx.UntrackRowLock(tableId, rowId);
    }

    public void ReleaseAllRowLocks(WalhallaSqlTransaction tx)
    {
        var locked = tx.GetTrackedRowLocks();
        lock (_sync)
        {
            foreach (var (tableId, rowId) in locked)
            {
                var key = (tableId, rowId);
                if (_rowLocks.TryGetValue(key, out var entry))
                {
                    if (entry.ExclusiveHolder == tx)
                    {
                        entry.ExclusiveHolder = null;
                        entry.HolderCount = 0;
                    }
                    else
                    {
                        entry.SharedHolders.Remove(tx);
                        entry.HolderCount--;
                    }
                    if (entry.HolderCount == 0)
                        _rowLocks.Remove(key);
                }
            }
            Monitor.PulseAll(_sync);
        }
        tx.ClearRowLocks();
    }

    public void Dispose()
    {
        foreach (var rwl in _perTableLocks)
            rwl?.Dispose();
        _perTableLocks = Array.Empty<ReaderWriterLockSlim?>();
    }

    private sealed class RowLockEntry
    {
        public WalhallaSqlTransaction? ExclusiveHolder;
        public readonly HashSet<WalhallaSqlTransaction> SharedHolders = new();
        public int HolderCount;
    }
}
