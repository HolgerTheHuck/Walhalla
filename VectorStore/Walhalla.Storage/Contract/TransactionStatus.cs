// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Contract;

/// <summary>Status einer Transaktion.</summary>
public enum TransactionStatus
{
    Active,
    Committing,
    Committed,
    Aborted
}
