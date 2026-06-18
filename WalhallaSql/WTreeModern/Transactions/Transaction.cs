using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;
using WTreeModern.Diagnostics;
using WTreeModern.Tree;

namespace WTreeModern.Transactions;

/// <summary>MVCC-Transaktion mit Snapshot-Isolation und WriteSet.</summary>
public sealed class Transaction<TKey, TValue> : ITransaction<TKey, TValue>
    where TKey : notnull
{
    private readonly WTree<TKey, TValue> _tree;
    private readonly TransactionManager _manager;
    private readonly ILogger _logger;

    // null-Wert im WriteSet bedeutet Delete
    private readonly Dictionary<TKey, TValue?> _writeSet = new();
    private readonly HashSet<TKey> _readSet = new();

    private bool _disposed;

    public ulong TxId { get; }
    public ulong StartSequence { get; }
    public TransactionStatus Status { get; private set; }
    public IsolationLevel Isolation { get; }

    internal Transaction(
        WTree<TKey, TValue> tree,
        TransactionManager manager,
        ulong txId,
        ulong startSequence,
        IsolationLevel isolation,
        ILogger? logger = null)
    {
        _tree         = tree;
        _manager      = manager;
        TxId          = txId;
        StartSequence = startSequence;
        Status        = TransactionStatus.Active;
        Isolation     = isolation;
        _logger       = logger ?? NoOpLogger.Instance;
    }

    // ── Read API ─────────────────────────────────────────────────────────────

    public bool TryGet(TKey key, out TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active)
            throw new TransactionStateException("Transaktion ist nicht aktiv.");

        if (_writeSet.TryGetValue(key, out var writeValue))
        {
            if (writeValue is null)
            {
                value = default!;
                return false;
            }
            value = writeValue;
            return true;
        }

        _readSet.Add(key);
        if (Isolation == IsolationLevel.Serializable)
            _manager.RegisterRead(key!, StartSequence);

        return _tree.TryGetWithSnapshot(key, StartSequence, out value!);
    }

    public bool ContainsKey(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active)
            throw new TransactionStateException("Transaktion ist nicht aktiv.");

        if (_writeSet.TryGetValue(key, out var writeValue))
            return writeValue is not null;

        _readSet.Add(key);
        if (Isolation == IsolationLevel.Serializable)
            _manager.RegisterRead(key!, StartSequence);

        return _tree.TryGetWithSnapshot(key, StartSequence, out _);
    }

    // ── Write API ────────────────────────────────────────────────────────────

    public void Upsert(TKey key, TValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active)
            throw new TransactionStateException("Transaktion ist nicht aktiv.");

        _writeSet[key] = value;
    }

    public void Delete(TKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active)
            throw new TransactionStateException("Transaktion ist nicht aktiv.");

        _writeSet[key] = default;
    }

    // ── Commit / Rollback ──────────────────────────────────────────────────────

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active)
            throw new TransactionStateException("Transaktion ist nicht aktiv.");

        Status = TransactionStatus.Committing;

        try
        {
            if (Isolation == IsolationLevel.Serializable)
            {
                lock (_manager.SerializableCommitLock)
                {
                    CommitCore();
                }
            }
            else
            {
                CommitCore();
            }
        }
        catch (TransactionConflictException ex)
        {
            _logger.Log(LogLevel.Warning, $"Transaction {TxId} conflicted.", ex);
            Status = TransactionStatus.Aborted;
            throw;
        }
        catch
        {
            Status = TransactionStatus.Aborted;
            throw;
        }
        finally
        {
            _manager.ReleaseTransaction(StartSequence);
        }
    }

    private void CommitCore()
    {
        if (Isolation is IsolationLevel.Snapshot or IsolationLevel.Serializable)
        {
            foreach (var key in _writeSet.Keys)
            {
                if (_manager.HasWriteConflict(key!, StartSequence))
                    throw new TransactionConflictException(
                        $"Write-Write-Konflikt in Tx {TxId} für Key {key}.");
            }
        }

        if (Isolation == IsolationLevel.Serializable)
        {
            bool hasConflictOut = false;
            foreach (var key in _readSet)
            {
                if (_manager.HasWriteConflict(key!, StartSequence))
                {
                    hasConflictOut = true;
                    break;
                }
            }

            bool hasConflictIn = _manager.HasReadWriteConflict(
                _writeSet.Keys.Cast<object>(), StartSequence);

            if (hasConflictOut && hasConflictIn)
                throw new TransactionConflictException(
                    $"SSI Serialization-Anomalie in Tx {TxId}.");
        }

        ulong commitSeq = _manager.AcquireCommitSequence();

        foreach (var (key, val) in _writeSet)
        {
            if (val is null)
                _tree.Delete(key, commitSeq);
            else
                _tree.Upsert(key, val, commitSeq);
        }

        foreach (var key in _writeSet.Keys)
            _manager.RegisterCommitted(key!, commitSeq);

        Status = TransactionStatus.Committed;
    }

    public void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(Transaction<TKey, TValue>));
        if (Status != TransactionStatus.Active && Status != TransactionStatus.Committing)
            return;

        _writeSet.Clear();
        Status = TransactionStatus.Aborted;

        if (Isolation == IsolationLevel.Serializable)
        {
            foreach (var key in _readSet)
                _manager.UnregisterRead(key!, StartSequence);
        }
        _manager.ReleaseTransaction(StartSequence);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (Status == TransactionStatus.Active)
                Rollback();
            else if (Isolation == IsolationLevel.Serializable && Status != TransactionStatus.Aborted)
            {
                foreach (var key in _readSet)
                    _manager.UnregisterRead(key!, StartSequence);
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
