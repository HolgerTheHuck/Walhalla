// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Core.Logging;
using Walhalla.Storage.Core.Runtime;

namespace Walhalla.Storage.Core.Transactions;

/// <summary>
/// Represents a single read-write transaction against a <see cref="WalhallaStore"/>.
/// Operations are buffered locally and written atomically to the WAL on
/// <see cref="Commit"/> or <see cref="CommitAsync"/>.
/// </summary>
/// <remarks>
/// Obtain a transaction via <see cref="WalhallaStore.BeginTransaction"/>.
/// Always call <see cref="Commit"/>, <see cref="CommitAsync"/>, or <see cref="Rollback"/>
/// before disposing – an uncommitted, non-rolled-back transaction is automatically
/// rolled back by <see cref="Dispose"/>.
/// </remarks>
public sealed class WalhallaTransaction : IDisposable
{
    private readonly WalhallaStore _runtime;
    private readonly long _transactionId;
    private readonly List<WalOperation> _operations = new();
    private readonly List<SavepointMarker> _savepoints = new();
    private bool _completed;

    internal WalhallaTransaction(WalhallaStore runtime, long transactionId)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _transactionId = transactionId;
    }

    /// <summary>Monotonically increasing identifier assigned by the store at transaction creation.</summary>
    public long TransactionId => _transactionId;

    internal IReadOnlyList<WalOperation> PendingOperations => _operations;

    /// <summary>Stages a key-value write in this transaction.  The key and value are cloned immediately.</summary>
    /// <param name="key">Key bytes (must not be <c>null</c>).</param>
    /// <param name="value">Value bytes (must not be <c>null</c>).</param>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is already completed.</exception>
    public void Put(byte[] key, byte[] value)
    {
        ThrowIfCompleted();

        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        _runtime.ValidateEntrySize(key, value);

        _operations.Add(new WalOperation(WalRecordType.Put, (byte[])key.Clone(), (byte[])value.Clone()));
    }

    /// <summary>Stages a deletion in this transaction.  No-op if the key does not exist in the store.</summary>
    /// <param name="key">Key bytes to remove (must not be <c>null</c>).</param>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is already completed.</exception>
    public void Delete(byte[] key)
    {
        ThrowIfCompleted();

        if (key == null)
            throw new ArgumentNullException(nameof(key));

        _operations.Add(new WalOperation(WalRecordType.Delete, (byte[])key.Clone(), null));
    }

    /// <summary>Atomically commits all staged operations to the WAL (synchronous).</summary>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is already completed.</exception>
    public void Commit()
    {
        ThrowIfCompleted();

        _runtime.Commit(_transactionId, _operations);
        _completed = true;
    }

    /// <summary>Atomically commits all staged operations to the WAL via the Group-Commit pipeline.</summary>
    /// <inheritdoc cref="WalhallaStore.PutAsync(byte[], byte[], CancellationToken)" select="param[@name='ct']"/>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is already completed.</exception>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        ThrowIfCompleted();

        await _runtime.CommitAsync(_transactionId, _operations, ct).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>Discards all staged operations without writing anything to the WAL.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the transaction is already completed.</exception>
    public void Rollback()
    {
        ThrowIfCompleted();
        _operations.Clear();
        _savepoints.Clear();
        _completed = true;
    }

    internal void CreateSavepoint(string name)
    {
        ThrowIfCompleted();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Savepoint name must not be empty.", nameof(name));

        _savepoints.Add(new SavepointMarker(name, _operations.Count));
    }

    internal void RollbackToSavepoint(string name)
    {
        ThrowIfCompleted();

        var index = FindSavepointIndex(name);
        var marker = _savepoints[index];

        if (_operations.Count > marker.OperationCount)
            _operations.RemoveRange(marker.OperationCount, _operations.Count - marker.OperationCount);

        if (_savepoints.Count > index + 1)
            _savepoints.RemoveRange(index + 1, _savepoints.Count - (index + 1));
    }

    internal void ReleaseSavepoint(string name)
    {
        ThrowIfCompleted();

        var index = FindSavepointIndex(name);
        _savepoints.RemoveAt(index);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has already been completed.");
    }

    private int FindSavepointIndex(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Savepoint name must not be empty.", nameof(name));

        for (var index = _savepoints.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_savepoints[index].Name, name, StringComparison.Ordinal))
                return index;
        }

        throw new InvalidOperationException($"Savepoint '{name}' does not exist.");
    }

    private sealed record SavepointMarker(string Name, int OperationCount);

    public void Dispose()
    {
        if (_completed)
            return;

        Rollback();
    }
}
