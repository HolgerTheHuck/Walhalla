// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Konfigurationsoptionen für IVFFlat (Inverted File Flat).
/// </summary>
public sealed class IvfOptions
{
    /// <summary>
    /// Anzahl Cluster (Voronoi-Zellen).
    /// Default: sqrt(N) zur Build-Zeit, falls 0 gesetzt.
    /// Standard: 0 (automatisch)
    /// </summary>
    public int NClusters { get; set; } = 0;

    /// <summary>
    /// Anzahl Cluster, die bei der Suche durchsucht werden.
    /// Höher = besserer Recall, langsamer.
    /// Standard: 3
    /// </summary>
    public int Nprobe { get; set; } = 3;

    /// <summary>
    /// Maximale K-Means-Iterationen beim Build.
    /// Standard: 20
    /// </summary>
    public int MaxIterations { get; set; } = 20;

    /// <summary>
    /// Konvergenz-Schwelle: Anteil der sich ändernden Zuordnungen.
    /// Standard: 0.01 (1%)
    /// </summary>
    public double ConvergenceThreshold { get; set; } = 0.01;

    /// <summary>
    /// Random-Seed für reproduzierbares K-Means.
    /// null = zufällig.
    /// </summary>
    public int? RandomSeed { get; set; } = null;
}
