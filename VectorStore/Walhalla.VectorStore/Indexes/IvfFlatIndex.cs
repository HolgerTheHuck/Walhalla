// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// IVFFlat (Inverted File Flat) ANN-Index.
/// </summary>
/// <remarks>
/// RAM-effiziente Alternative zu HNSW für IoT und Low-RAM-Szenarien.
/// Speichert nur Centroids im RAM; Cluster-Listen sind disk-resident oder
/// werden on-demand geladen.
/// </remarks>
public sealed class IvfFlatIndex
{
    private readonly IvfOptions _options;
    private readonly DistanceMetric _metric;
    private readonly Random _random;

    // Centroids: k × dimension
    private float[][]? _centroids;

    // Cluster-Listen: pro Cluster eine Liste von IDs
    private List<ulong>[] _clusters;

    private readonly object _clusterLock = new();
    private volatile int _dimension;
    private volatile bool _built;

    public IvfOptions Options => _options;
    public bool IsBuilt => _built;
    public int NClusters => _centroids?.Length ?? 0;
    public int Dimension => _dimension;

    public IvfFlatIndex(IvfOptions? options = null, DistanceMetric metric = DistanceMetric.Euclidean)
    {
        _options = options ?? new IvfOptions();
        _metric = metric;
        _random = _options.RandomSeed.HasValue ? new Random(_options.RandomSeed.Value) : new Random();
        _clusters = Array.Empty<List<ulong>>();
    }

    /// <summary>
    /// Baut den Index aus einer Menge von Vektoren mit K-Means++.
    /// </summary>
    public void Build(IReadOnlyList<(ulong Id, float[] Vector)> vectors)
    {
        if (vectors.Count == 0) return;

        _dimension = vectors[0].Vector.Length;
        var nClusters = _options.NClusters > 0
            ? Math.Min(_options.NClusters, vectors.Count)
            : Math.Max(1, (int)Math.Sqrt(vectors.Count));

        // K-Means++ Initialisierung
        _centroids = KMeansPlusPlus(vectors, nClusters);
        _clusters = new List<ulong>[nClusters];
        for (int i = 0; i < nClusters; i++)
            _clusters[i] = new List<ulong>();

        // Lloyd-Iterationen
        var assignments = new int[vectors.Count];
        for (int iter = 0; iter < _options.MaxIterations; iter++)
        {
            // E-Step: Zuordnung zu Clustern
            int changed = 0;
            Parallel.For(0, vectors.Count, i =>
            {
                var newCluster = FindNearestCentroid(vectors[i].Vector);
                if (assignments[i] != newCluster)
                {
                    Interlocked.Increment(ref changed);
                    assignments[i] = newCluster;
                }
            });

            // Konvergenz prüfen
            if ((double)changed / vectors.Count < _options.ConvergenceThreshold)
                break;

            // M-Step: Centroids neu berechnen
            UpdateCentroids(vectors, assignments, nClusters);
        }

        // Finale Zuordnung
        for (int i = 0; i < vectors.Count; i++)
        {
            var cluster = FindNearestCentroid(vectors[i].Vector);
            _clusters[cluster].Add(vectors[i].Id);
        }

        _built = true;
    }

    /// <summary>
    /// Fügt einen neuen Vektor zum Index hinzu (ohne Rebuild).
    /// </summary>
    public void Insert(ulong id, float[] vector)
    {
        if (!_built || _centroids is null) return;

        var cluster = FindNearestCentroid(vector);
        lock (_clusterLock)
        {
            _clusters[cluster].Add(id);
        }
    }

    /// <summary>
    /// Sucht K nächste Nachbarn.
    /// </summary>
    public IReadOnlyList<(ulong Id, float Distance)> SearchKnn(
        float[] query,
        int k,
        Func<ulong, float[]> vectorLoader,
        int? nprobe = null,
        Func<ulong, bool>? isAllowed = null)
    {
        if (!_built || _centroids is null || _centroids.Length == 0)
            return Array.Empty<(ulong, float)>();

        var probeCount = nprobe ?? _options.Nprobe;
        probeCount = Math.Min(probeCount, _centroids.Length);

        // Finde die probeCount nächsten Centroids
        var centroidDists = new List<(int Index, float Distance)>(_centroids.Length);
        for (int i = 0; i < _centroids.Length; i++)
        {
            var dist = ComputeDistance(query, _centroids[i]);
            centroidDists.Add((i, dist));
        }

        var nearestCentroids = centroidDists
            .OrderBy(c => c.Distance)
            .Take(probeCount)
            .Select(c => c.Index)
            .ToList();

        // Suche in den ausgewählten Clustern
        var candidates = new List<(ulong Id, float Distance)>();
        foreach (var cIdx in nearestCentroids)
        {
            List<ulong> ids;
            lock (_clusterLock)
            {
                ids = new List<ulong>(_clusters[cIdx]);
            }

            foreach (var id in ids)
            {
                if (isAllowed is not null && !isAllowed(id)) continue;
                var vec = vectorLoader(id);
                var dist = ComputeDistance(query, vec);
                candidates.Add((id, dist));
            }
        }

        return candidates
            .OrderBy(c => c.Distance)
            .Take(k)
            .ToList();
    }

    /// <summary>
    /// Entfernt eine ID aus allen Clustern.
    /// </summary>
    public void Remove(ulong id)
    {
        if (!_built || _centroids is null) return;

        lock (_clusterLock)
        {
            foreach (var cluster in _clusters)
            {
                cluster.Remove(id);
            }
        }
    }

    /// <summary>
    /// Löscht alle Daten.
    /// </summary>
    public void Clear()
    {
        lock (_clusterLock)
        {
            foreach (var cluster in _clusters)
                cluster.Clear();
        }
        _centroids = null;
        _built = false;
    }

    #region K-Means

    private float[][] KMeansPlusPlus(IReadOnlyList<(ulong Id, float[] Vector)> vectors, int k)
    {
        var centroids = new float[k][];
        var n = vectors.Count;
        var dim = vectors[0].Vector.Length;

        // Erster Centroid: zufällig
        centroids[0] = new float[dim];
        vectors[_random.Next(n)].Vector.CopyTo(centroids[0], 0);

        var distances = new float[n];

        for (int c = 1; c < k; c++)
        {
            // Berechne Distanz jedes Punktes zum nächsten existierenden Centroid
            float totalDist = 0;
            for (int i = 0; i < n; i++)
            {
                float minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    var d = ComputeDistance(vectors[i].Vector, centroids[j]);
                    if (d < minDist) minDist = d;
                }
                distances[i] = minDist;
                totalDist += minDist;
            }

            // Gewichtete Zufallsauswahl
            var threshold = (float)(_random.NextDouble() * totalDist);
            float cumulative = 0;
            int selected = n - 1;
            for (int i = 0; i < n; i++)
            {
                cumulative += distances[i];
                if (cumulative >= threshold)
                {
                    selected = i;
                    break;
                }
            }

            centroids[c] = new float[dim];
            vectors[selected].Vector.CopyTo(centroids[c], 0);
        }

        return centroids;
    }

    private void UpdateCentroids(IReadOnlyList<(ulong Id, float[] Vector)> vectors, int[] assignments, int k)
    {
        var dim = _dimension;
        var newCentroids = new float[k][];
        var counts = new int[k];

        for (int i = 0; i < k; i++)
            newCentroids[i] = new float[dim];

        for (int i = 0; i < vectors.Count; i++)
        {
            var c = assignments[i];
            var vec = vectors[i].Vector;
            for (int d = 0; d < dim; d++)
                newCentroids[c][d] += vec[d];
            counts[c]++;
        }

        for (int i = 0; i < k; i++)
        {
            if (counts[i] > 0)
            {
                for (int d = 0; d < dim; d++)
                    newCentroids[i][d] /= counts[i];
            }
        }

        _centroids = newCentroids;
    }

    private int FindNearestCentroid(float[] vector)
    {
        if (_centroids is null) return 0;

        int best = 0;
        float bestDist = ComputeDistance(vector, _centroids[0]);

        for (int i = 1; i < _centroids.Length; i++)
        {
            var dist = ComputeDistance(vector, _centroids[i]);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    #endregion

    #region Distance

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ComputeDistance(float[] a, float[] b)
    {
        if (_metric == DistanceMetric.Cosine)
            return 1.0f - VectorDistance.DotProduct(a.AsSpan(), b.AsSpan());
        if (_metric == DistanceMetric.DotProduct)
            return -VectorDistance.DotProduct(a.AsSpan(), b.AsSpan());
        return VectorDistance.Euclidean(a.AsSpan(), b.AsSpan());
    }

    #endregion
}
