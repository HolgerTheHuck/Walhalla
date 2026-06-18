// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Contract;

/// <summary>Wird geworfen, wenn eine Transaktion wegen eines Konflikts abgebrochen wird.</summary>
public sealed class TransactionConflictException : Exception
{
    public TransactionConflictException(string message) : base(message) { }
    public TransactionConflictException(string message, Exception inner) : base(message, inner) { }
}
