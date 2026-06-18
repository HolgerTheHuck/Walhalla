// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Konfigurationsoptionen für HNSW (Hierarchical Navigable Small World).
/// </summary>
public sealed class HnswOptions
{
    /// <summary>
    /// Dimension der Vektoren. 0 = noch nicht gesetzt.
    /// Wenn gesetzt, werden M, Ml0Factor, EfConstruction und EfSearch
    /// automatisch für die Dimension skaliert.
    /// </summary>
    public int Dimension
    {
        get => _dimension;
        set
        {
            _dimension = value;
            ApplyAutoScaling();
        }
    }

    /// <summary>
    /// Maximale Anzahl Verbindungen pro Knoten auf Layer > 0.
    /// Höher = bessere Recall, mehr Speicher, langsamerer Build.
    /// Default: 16 (low-dim), 48 (768+ dim), skaliert mit Dimension.
    /// </summary>
    public int M { get; set; } = 16;

    /// <summary>
    /// Multiplikator für Verbindungen auf Layer 0 (base layer).
    /// effM = M * Ml0Factor.
    /// Default: 2 (low-dim), 3 (768+ dim).
    /// </summary>
    public int Ml0Factor { get; set; } = 2;

    /// <summary>
    /// Kandidaten-Pool-Größe beim Einfügen.
    /// Höher = bessere Graph-Qualität, langsamerer Build.
    /// Default: 200 (low-dim), 400 (768+ dim).
    /// </summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>
    /// Kandidaten-Pool-Größe beim Suchen.
    /// Höher = bessere Recall, langsameres Query.
    /// Default: 128 (low-dim), 256 (768+ dim).
    /// </summary>
    public int EfSearch { get; set; } = 128;

    /// <summary>
    /// Zufallsseed für deterministische Einfügereihenfolge.
    /// null = zufällig.
    /// </summary>
    public int? RandomSeed { get; set; } = null;

    /// <summary>
    /// Maximale Layer-Anzahl. 0 = automatisch basierend auf M.
    /// </summary>
    public int MaxLayer { get; set; } = 0;

    /// <summary>
    /// EfConstruction für Layer 0. 0 = auto: EfConstruction * 2 bei dim >= 768, sonst EfConstruction.
    /// Layer 0 enthält alle Knoten und ist entscheidend für Recall — doppelt so viele Kandidaten
    /// verbessert die Graph-Qualität signifikant.
    /// </summary>
    public int EfConstructionL0Factor { get; set; } = 0;

    /// <summary>
    /// Berechnete efConstruction für Layer 0.
    /// Bei AutoScale & high-dim: EfConstruction (kein zusätzlicher Multiplikator),
    /// da M und Ml0 bereits für Layer 0 skaliert sind.
    /// </summary>
    internal int EfConstructionL0 => EfConstructionL0Factor > 0
        ? EfConstructionL0Factor
        : EfConstruction;

    /// <summary>
    /// Berechnete M für Layer 0.
    /// </summary>
    internal int Ml0 => M * Ml0Factor;

    /// <summary>
    /// Wenn true, werden M, Ml0Factor, EfConstruction und EfSearch automatisch
    /// basierend auf der Dimension skaliert. Auf false setzen, um manuelle Werte
    /// ohne Überschreibung zu verwenden.
    /// Standard: true
    /// </summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>
    /// Wenn true, wird der HNSW-Index asynchron im Hintergrund aufgebaut.
    /// Put-Operationen blockieren nicht auf dem Index-Build.
    /// Standard: false
    /// </summary>
    public bool AsyncIndexing { get; set; } = false;

    /// <summary>
    /// Maximale Distanzberechnungen in SelectNeighborsHeuristic pro Insert.
    /// Begrenzt O(Kandidaten × M)-Explosion in der Heuristik bei hohen Dimensionen.
    /// Default: 2000 (guter Kompromiss zwischen Graph-Qualität und Speed).
    /// </summary>
    public int MaxHeuristicDistanceComputations { get; set; } = 2000;

    /// <summary>
    /// Größe des LRU-Caches für Vektoren (beschleunigt HNSW-Suche und Exact Search).
    /// Höher = weniger I/O, mehr RAM.
    /// Default: 100000
    /// </summary>
    public int VectorCacheSize { get; set; } = 100000;

    private int _dimension;
    private bool _autoScaled;

    private void ApplyAutoScaling()
    {
        if (_autoScaled || !AutoScale) return;
        _autoScaled = true;

        if (_dimension >= 768)
        {
            // Hochdimensionale Vektoren (Embedding-Modelle: text-embedding-3-small/large, etc.)
            // M = max(16, dim / 48) → 32 bei 1536dim. Besserer Recall als M=16
            // ohne die massiven Build-Kosten von M=48 (M=48: ~11x langsamer, M=32: ~2-3x).
            M = Math.Max(16, _dimension / 48);

            // Ml0 = 2×M auf Layer 0. Gute Balance für hochdimensionale Vektoren.
            Ml0Factor = 2;

            // EfConstruction = 300: 50% mehr Kandidaten als Default für bessere Graph-Qualität.
            EfConstruction = 300;

            // EfSearch = 256: Notwendig für >90% Recall@10 bei 1536dim.
            // Kostet ~2ms Query-Latenz (immer noch 7x schneller als Qdrant).
            EfSearch = 256;
        }
        // Bei Dimension < 768 bleiben die konstruktor-defaults (M=16, Ml0Factor=2, etc.)
    }
}
