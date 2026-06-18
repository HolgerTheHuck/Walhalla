// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Filtering;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore;

/// <summary>Schnittstelle für Vektor-CRUD und Suche.</summary>
public interface IVectorRepository : IDisposable
{
    int Dimension { get; }
    DistanceMetric DefaultMetric { get; }
    int Count { get; }

    /// <summary>Fügt oder ersetzt einen Vektor.</summary>
    Task PutAsync(ulong id, Vector vector, VectorMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>Liest einen Vektor zurück.</summary>
    Task<VectorEntry?> GetAsync(ulong id, CancellationToken ct = default);

    /// <summary>Löscht einen Vektor.</summary>
    Task DeleteAsync(ulong id, CancellationToken ct = default);

    /// <summary>Exakte Brute-Force Suche über alle Vektoren (langsam, aber korrekt).</summary>
    IAsyncEnumerable<VectorSearchResult> SearchExactAsync(Vector query, int topK, FilterClause? filter = null, CancellationToken ct = default);

    /// <summary>HNSW-basierte Approximate Nearest Neighbor Suche (schnell).</summary>
    IAsyncEnumerable<VectorSearchResult> SearchHnswAsync(Vector query, int topK, int? ef = null, FilterClause? filter = null, CancellationToken ct = default);

    /// <summary>IVF-basierte Approximate Nearest Neighbor Suche (RAM-effizient).</summary>
    IAsyncEnumerable<VectorSearchResult> SearchIvfAsync(Vector query, int topK, int? nprobe = null, FilterClause? filter = null, CancellationToken ct = default);

    /// <summary>Prüft Existenz.</summary>
    Task<bool> ExistsAsync(ulong id, CancellationToken ct = default);

    /// <summary>Alle IDs aufzählen.</summary>
    IAsyncEnumerable<ulong> EnumerateIdsAsync(CancellationToken ct = default);

    /// <summary>Baut den HNSW-Index für alle Vektoren auf.</summary>
    Task RebuildIndexAsync(IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>Kompletter Eintrag aus Repository.</summary>
public sealed class VectorEntry
{
    public required ulong Id { get; init; }
    public required Vector Vector { get; init; }
    public VectorMetadata? Metadata { get; init; }
}

/// <summary>Implementierung auf Basis von Walhalla.Storage.Blobs mit HNSW-Index.</summary>
public sealed class BlobVectorRepository : IVectorRepository
{
    private readonly IKeyValueStore _store;
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly HnswIndex? _hnsw;
    private readonly VectorCache _cache;
    private readonly bool _normalizeCosine;
    private long _count;
    private bool _disposed;

    public int Dimension => _dimension;
    public DistanceMetric DefaultMetric => _metric;
    public int Count => (int)Interlocked.Read(ref _count);
    public HnswIndex? Index => _hnsw;

    private static byte[] GetVectorKey(ulong id) => System.Text.Encoding.UTF8.GetBytes($"v:{id}");
    private static byte[] GetMetadataKey(ulong id) => System.Text.Encoding.UTF8.GetBytes($"m:{id}");

    public BlobVectorRepository(IKeyValueStore store, int dimension, DistanceMetric metric = DistanceMetric.Cosine, bool enableHnsw = true, HnswOptions? hnswOptions = null, int cacheSize = 10000)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dimension = dimension > 0 ? dimension : throw new ArgumentOutOfRangeException(nameof(dimension));
        _metric = metric;
        _normalizeCosine = metric == DistanceMetric.Cosine;
        _cache = new VectorCache(cacheSize);

        if (enableHnsw)
        {
            _hnsw = new HnswIndex(hnswOptions ?? new HnswOptions(), metric);
        }
    }

    /// <summary>Legacy-Konstruktor für Rückwärtskompatibilität.</summary>
    public BlobVectorRepository(BlobStore store, int dimension, DistanceMetric metric = DistanceMetric.Cosine, bool enableHnsw = true, HnswOptions? hnswOptions = null, int cacheSize = 10000)
        : this(new BlobStoreIKeyValueAdapter(store), dimension, metric, enableHnsw, hnswOptions, cacheSize)
    {
    }

    public async Task PutAsync(ulong id, Vector vector, VectorMetadata? metadata = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vector.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {vector.Dimension}");

        // Für Cosine: normalisieren
        float[]? dataToStore = null;
        if (_normalizeCosine)
        {
            dataToStore = new float[_dimension];
            vector.Span.CopyTo(dataToStore);
            VectorDistance.NormalizeL2(dataToStore.AsSpan());
        }

        // Vektor speichern
        var vectorBytes = dataToStore is not null
            ? new Vector(dataToStore).ToByteArray()
            : vector.ToByteArray();

        var exists = await ExistsAsync(id, ct);
        _store.Upsert(GetVectorKey(id), vectorBytes);

        // Metadaten speichern
        if (metadata != null)
        {
            metadata.Id = id;
            metadata.Collection ??= "default";
            var metaBytes = metadata.ToJsonBytes();
            _store.Upsert(GetMetadataKey(id), metaBytes);
        }

        // Cache aktualisieren
        var cacheVector = dataToStore is not null ? new Vector(dataToStore) : vector;
        _cache.Put(id, cacheVector);

        // HNSW-Index aktualisieren
        if (_hnsw is not null)
        {
            if (exists)
            {
                _hnsw.MarkDeleted(id);
            }
            _hnsw.Insert(id, LoadVectorForHnsw);
        }

        if (!exists)
        {
            Interlocked.Increment(ref _count);
        }
    }

    public async Task<VectorEntry?> GetAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Zuerst Cache prüfen
        if (_cache.TryGet(id, out var cached))
        {
            var metadata = _store.TryGet(GetMetadataKey(id), out var metaBytes) && metaBytes is not null
                ? VectorMetadata.FromJsonBytes(metaBytes) : null;
            return new VectorEntry { Id = id, Vector = cached, Metadata = metadata };
        }

        // Aus Storage laden
        if (!_store.TryGet(GetVectorKey(id), out var vectorBytes) || vectorBytes is null) return null;

        var vector = Vector.FromByteArray(vectorBytes, _dimension);

        var metadata2 = _store.TryGet(GetMetadataKey(id), out var metaBytes2) && metaBytes2 is not null
            ? VectorMetadata.FromJsonBytes(metaBytes2) : null;

        // In Cache einfügen
        _cache.Put(id, vector);

        return new VectorEntry { Id = id, Vector = vector, Metadata = metadata2 };
    }

    public async Task DeleteAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var existed = await ExistsAsync(id, ct);
        if (!existed) return;

        _store.Delete(GetVectorKey(id));
        _store.Delete(GetMetadataKey(id));

        _cache.Remove(id);
        _hnsw?.MarkDeleted(id);

        Interlocked.Decrement(ref _count);
    }

    public Task<bool> ExistsAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return Task.FromResult(_store.TryGet(GetVectorKey(id), out var vectorBytes) && vectorBytes is not null);
    }

    /// <summary>
    /// Brute-Force Suche: Lädt ALLE Vektoren und berechnet Distanzen.
    /// Nur für kleine Mengen oder Testzwecke geeignet!
    /// </summary>
    public async IAsyncEnumerable<VectorSearchResult> SearchExactAsync(
        Vector query,
        int topK,
        FilterClause? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (query.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {query.Dimension}");

        var results = new List<(ulong Id, float Score, VectorMetadata? Metadata)>();
        var queryData = query.Span.ToArray();

        await foreach (var id in EnumerateIdsAsync(ct))
        {
            var entry = await GetAsync(id, ct);
            if (entry is null) continue;

            float score = _metric switch
            {
                DistanceMetric.Euclidean => VectorDistance.Euclidean(queryData, entry.Vector.Span),
                DistanceMetric.Cosine => 1.0f - VectorDistance.Cosine(queryData, entry.Vector.Span),
                DistanceMetric.DotProduct => -VectorDistance.DotProduct(queryData, entry.Vector.Span),
                _ => throw new NotSupportedException(_metric.ToString())
            };

            results.Add((id, score, entry.Metadata));
        }

        var ordered = results.OrderBy(r => r.Score);
        var yielded = 0;

        foreach (var (id, score, metadata) in ordered)
        {
            if (yielded >= topK) break;
            if (filter is null || FilterEvaluator.Evaluate(filter, metadata?.Payload))
            {
                yield return new VectorSearchResult { Id = id, Score = score, Metadata = metadata };
                yielded++;
            }
        }
    }

    /// <summary>
    /// HNSW-basierte Approximate Nearest Neighbor Suche.
    /// </summary>
    public async IAsyncEnumerable<VectorSearchResult> SearchHnswAsync(
        Vector query,
        int topK,
        int? ef = null,
        FilterClause? filter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_hnsw is null)
            throw new InvalidOperationException("HNSW index is not enabled");
        if (query.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {query.Dimension}");

        float[] queryData = new float[_dimension];
        query.Span.CopyTo(queryData);
        if (_normalizeCosine)
        {
            VectorDistance.NormalizeL2(queryData.AsSpan());
        }

        if (filter is null)
        {
            var results = _hnsw.SearchKnn(id => LoadVectorForHnsw(id), topK, ef);
            foreach (var (id, distance) in results)
            {
                yield return new VectorSearchResult
                {
                    Id = id,
                    Score = distance,
                    Metadata = await GetMetadataAsync(id, ct)
                };
            }
            yield break;
        }

        // Oversampling + Post-Filter
        const int initialFactor = 10;
        const int maxFactor = 50;
        var factor = initialFactor;
        var yielded = new HashSet<ulong>();

        while (factor <= maxFactor)
        {
            var candidateCount = topK * factor;
            var results = _hnsw.SearchKnn(id => LoadVectorForHnsw(id), candidateCount, ef);

            foreach (var (id, distance) in results)
            {
                if (yielded.Contains(id)) continue;

                var metadata = await GetMetadataAsync(id, ct);
                if (FilterEvaluator.Evaluate(filter, metadata?.Payload))
                {
                    yield return new VectorSearchResult
                    {
                        Id = id,
                        Score = distance,
                        Metadata = metadata
                    };
                    yielded.Add(id);

                    if (yielded.Count >= topK)
                        yield break;
                }
            }

            if (yielded.Count >= topK || results.Count < candidateCount)
                break;

            factor *= 2;
        }
    }

    public IAsyncEnumerable<VectorSearchResult> SearchIvfAsync(Vector query, int topK, int? nprobe = null, FilterClause? filter = null, CancellationToken ct = default)
    {
        throw new NotImplementedException("IVF search is only supported on VectorCollection");
    }

    private async Task<VectorMetadata?> GetMetadataAsync(ulong id, CancellationToken ct)
    {
        if (_store.TryGet(GetMetadataKey(id), out var metaBytes) && metaBytes is not null)
            return VectorMetadata.FromJsonBytes(metaBytes);
        return null;
    }

    /// <summary>
    /// Baut den HNSW-Index für alle vorhandenen Vektoren neu auf.
    /// </summary>
    public async Task RebuildIndexAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (_hnsw is null) return;

        var ids = new List<ulong>();
        await foreach (var id in EnumerateIdsAsync(ct))
        {
            ids.Add(id);
        }

        for (int i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            _hnsw.Insert(ids[i], LoadVectorForHnsw);
            progress?.Report((i + 1) * 100.0 / ids.Count);
        }
    }

    /// <summary>
    /// Callback für HNSW: Lädt Vektor aus Cache oder Storage.
    /// </summary>
    private float[] LoadVectorForHnsw(ulong id)
    {
        if (_cache.TryGet(id, out var cached))
        {
            return cached.Data;
        }

        // Synchron laden für HNSW
        if (_store.TryGet(GetVectorKey(id), out var vectorBytes) && vectorBytes is not null)
        {
            var vector = Vector.FromByteArray(vectorBytes, _dimension);
            _cache.Put(id, vector);
            return vector.Data;
        }
        return new float[_dimension];
    }

    /// <summary>
    /// IDs aufzählen. Erfordert Iterator-Interface vom WalhallaStore.
    /// Workaround: Scan range "v:" prefix (wenn supported).
    /// </summary>
    public IAsyncEnumerable<ulong> EnumerateIdsAsync(CancellationToken ct = default)
    {
        // TODO: Implementieren sobald WalhallaStore prefix-scan oder iterator hat
        throw new NotImplementedException("Enumeration requires prefix-scan or iterator support from WalhallaStore");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _hnsw?.Dispose();
            _store.Dispose();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BlobVectorRepository));
    }
}
