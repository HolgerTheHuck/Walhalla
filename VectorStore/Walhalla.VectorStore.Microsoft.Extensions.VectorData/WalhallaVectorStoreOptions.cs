// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.VectorStore.Microsoft.Extensions.VectorData;

/// <summary>
/// Konfigurationsoptionen für den Walhalla VectorData-Connector.
/// </summary>
public sealed class WalhallaVectorStoreOptions
{
    /// <summary>
    /// Aktiviert HNSW-Indexierung für neue Collections (Default: true).
    /// </summary>
    public bool EnableHnswByDefault { get; set; } = true;

    /// <summary>
    /// Standard-ef-Wert für HNSW-Suche (Default: 100).
    /// </summary>
    public int DefaultEfSearch { get; set; } = 100;

    /// <summary>
    /// Maximale Anzahl parallel geladener Vektoren beim client-seitigen Filtern.
    /// </summary>
    public int ClientFilterBatchSize { get; set; } = 1000;
}
