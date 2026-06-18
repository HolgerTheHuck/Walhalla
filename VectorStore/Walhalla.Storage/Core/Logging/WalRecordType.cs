// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Core.Logging;

/// <summary>Record-type discriminator written into each WAL entry.</summary>
internal enum WalRecordType : byte
{
    /// <summary>Marks the start of a new transaction block.</summary>
    BeginTransaction = 1,

    /// <summary>A key-value write operation belonging to the current transaction.</summary>
    Put = 2,

    /// <summary>A key-deletion operation belonging to the current transaction.</summary>
    Delete = 3,

    /// <summary>Marks the successful end of a transaction; only records with a matching commit are replayed.</summary>
    CommitTransaction = 4
}
