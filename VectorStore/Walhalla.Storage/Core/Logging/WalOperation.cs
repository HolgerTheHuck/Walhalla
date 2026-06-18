// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core.Logging;

/// <summary>
/// Immutable representation of a single mutation (Put or Delete) within a transaction.
/// Instances are created by <see cref="Walhalla.Storage.Core.Transactions.WalhallaTransaction"/>
/// and batched into a <see cref="WalRecordType.CommitTransaction"/> record by the WAL flush loop.
/// </summary>
internal readonly struct WalOperation
{
    /// <summary>
    /// Initialises a new operation.
    /// </summary>
    /// <param name="type">Must be <see cref="WalRecordType.Put"/> or <see cref="WalRecordType.Delete"/>.</param>
    /// <param name="key">Key bytes (must not be <c>null</c>).</param>
    /// <param name="value">Value bytes for Put operations; <c>null</c> for Delete operations.</param>
    public WalOperation(WalRecordType type, byte[] key, byte[]? value)
    {
        if (type is not WalRecordType.Put and not WalRecordType.Delete)
            throw new ArgumentOutOfRangeException(nameof(type), "Operation must be Put or Delete.");

        Type = type;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value;
    }

    /// <summary>Whether this operation is a write (<see cref="WalRecordType.Put"/>) or a removal (<see cref="WalRecordType.Delete"/>).</summary>
    public WalRecordType Type { get; }

    /// <summary>The key bytes affected by this operation.</summary>
    public byte[] Key { get; }

    /// <summary>The value bytes for a Put; <c>null</c> for a Delete.</summary>
    public byte[]? Value { get; }
}
