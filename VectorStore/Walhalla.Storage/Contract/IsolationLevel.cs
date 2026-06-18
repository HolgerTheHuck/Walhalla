// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Contract;

/// <summary>MVCC-Isolationsstufen für Storage-Transaktionen.</summary>
public enum IsolationLevel
{
    /// <summary>Snapshot Isolation mit Write-Write-Konflikterkennung.</summary>
    Snapshot,

    /// <summary>Nur Read Committed – keine Konflikterkennung.</summary>
    ReadCommitted,

    /// <summary>Serializable Snapshot Isolation (SSI).</summary>
    Serializable
}
