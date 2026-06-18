// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.Storage.Contract;

/// <summary>Backend-Baum-Variante für <see cref="StorageEngineOptions">.</summary>
public enum StorageBackend
{
    /// <summary>MVCC-fähiger B+Tree — Default für Embedded/VectorStore.</summary>
    MvccBPlusTree,

    /// <summary>Klassischer B+Tree + Blob-Sidecar als Embedded-Option.</summary>
    BPlusTree,

    /// <summary>Rein in-memory (Test/Entwicklung).</summary>
    InMemory
}
