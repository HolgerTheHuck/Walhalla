// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Contract;

/// <summary>Transaktion = konsistente Lesesicht + Schreibpfad.</summary>
public interface IStorageTransaction : IReadSnapshot
{
    ulong TxId { get; }
    TransactionStatus Status { get; }

    void Upsert(byte[] key, byte[] value);
    void Delete(byte[] key);

    /// <summary>Commit. Wirft <see cref="TransactionConflictException"> bei
    /// Write-Write- bzw. SSI-Konflikt (je nach Isolationsgrad).</summary>
    void Commit();

    void Rollback();
}
