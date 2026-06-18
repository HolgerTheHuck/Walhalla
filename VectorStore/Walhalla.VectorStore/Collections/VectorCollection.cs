// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Walhalla.Indexes.FullText;
using Walhalla.Indexes.Primitives;
using Walhalla.Storage.Contract;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Filtering;
using Walhalla.VectorStore.Indexes;

namespace Walhalla.VectorStore.Collections;

/// <summary>
/// Eine benannte Collection von Vektoren im gemeinsamen Store.
/// </summary>
public sealed class VectorCollection : IVectorRepository
{
    private readonly string _name;
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly IKeyValueStore _store;
    private readonly HnswIndex? _hnsw;
    private readonly IvfFlatIndex? _ivf;
    private readonly VectorCache _cache;
    private readonly bool _normalizeCosine;
    private readonly PayloadIndexOptions _payloadIndexOptions;
    private readonly CollectionManifest _manifest;
    private long _count;
    private long _sequence;
    private bool _disposed;
    private readonly PayloadIndex? _payloadIndex;
    private readonly ConcurrentDictionary<Guid, Channel<CollectionChangeEvent>> _changeSubscribers = new();

    private readonly record struct IndexingJob(ulong Id, float[] Vector, bool Exists, TaskCompletionSource? FlushSignal = null);
    private readonly Channel<IndexingJob>? _indexingChannel;
    private Task? _indexingWorker;
    private CancellationTokenSource? _indexingCts;
    private static readonly JsonSerializerOptions s_changeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string Name => _name;
    public int Dimension => _dimension;
    public DistanceMetric DefaultMetric => _metric;
    public int Count => (int)Interlocked.Read(ref _count);
    public long CurrentSequence => Interlocked.Read(ref _sequence);
    public HnswIndex? Index => _hnsw;
    public IvfFlatIndex? IvfIndex => _ivf;

    // Key-Layout: c:{name}:v:{id} → Vektor, c:{name}:m:{id} → Metadata
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetVectorKey(ulong id) => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:v:{id}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetMetadataKey(ulong id) => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:m:{id}");

    private byte[] GetSequenceKey() => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:s");
    private byte[] GetCountKey() => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:c");
    private byte[] GetManifestKey() => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:i");
    private byte[] GetChangePrefix() => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:chg:");
    private byte[] GetChangeKey(long sequence) => System.Text.Encoding.UTF8.GetBytes($"c:{_name}:chg:{sequence:D20}");

    internal VectorCollection(string name, int dimension, DistanceMetric metric, IKeyValueStore store, bool enableHnsw, HnswOptions? hnswOptions, bool enablePayloadIndex = true, bool enableIvf = false, IvfOptions? ivfOptions = null, PayloadIndexOptions? payloadIndexOptions = null)
    {
        _name = name;
        _dimension = dimension;
        _metric = metric;
        _store = store;
        _manifest = LoadManifest(name);
        _payloadIndexOptions = ResolvePayloadIndexOptions(_manifest, payloadIndexOptions);
        _normalizeCosine = metric == DistanceMetric.Cosine;
        var hnswOpts = hnswOptions ?? new HnswOptions();
        hnswOpts.Dimension = dimension;
        _cache = new VectorCache(hnswOpts.VectorCacheSize);

        if (enableHnsw)
        {
            _hnsw = new HnswIndex(hnswOpts, metric);
            if (hnswOpts.AsyncIndexing)
            {
                _indexingCts = new CancellationTokenSource();
                _indexingChannel = Channel.CreateBounded<IndexingJob>(
                    new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.Wait });
                _indexingWorker = Task.Run(() => RunIndexingWorkerAsync(_indexingCts.Token));
            }
        }

        if (enableIvf)
        {
            _ivf = new IvfFlatIndex(ivfOptions, metric);
        }

        // Bestehende Sequenznummer laden
        if (_store.TryGet(GetSequenceKey(), out var seqBytes) && seqBytes is not null && seqBytes.Length == sizeof(long))
        {
            _sequence = BitConverter.ToInt64(seqBytes);
        }

        // Bestehenden Count laden
        if (_store.TryGet(GetCountKey(), out var countBytes) && countBytes is not null && countBytes.Length == sizeof(long))
        {
            _count = BitConverter.ToInt64(countBytes);
        }

        ApplyManifestConfiguration(enablePayloadIndex);

        if (enablePayloadIndex)
            _payloadIndex = new PayloadIndex(name, store, _payloadIndexOptions, assumeFullyBuilt: _count == 0 || _manifest.PayloadIndexWarm);

        PersistManifest();
    }

    public async Task PutAsync(ulong id, Vector vector, VectorMetadata? metadata = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vector.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {vector.Dimension}");

        float[]? dataToStore = null;
        if (_normalizeCosine)
        {
            dataToStore = new float[_dimension];
            vector.Span.CopyTo(dataToStore);
            VectorDistance.NormalizeL2(dataToStore.AsSpan());
        }

        var vectorBytes = dataToStore is not null
            ? new Vector(dataToStore).ToByteArray()
            : vector.ToByteArray();

        var exists = await ExistsAsync(id, ct);

        Dictionary<string, object>? previousPayload = exists
            ? await GetStoredPayloadAsync(id, ct).ConfigureAwait(false)
            : null;

        // Atomare Sequenznummer + Speicherung
        var seq = Interlocked.Increment(ref _sequence);
        var seqBytes = BitConverter.GetBytes(seq);

        _store.Upsert(GetVectorKey(id), vectorBytes);
        _store.Upsert(GetSequenceKey(), seqBytes);

        if (metadata is not null)
        {
            metadata.Id = id;
            metadata.Collection = _name;
            _store.Upsert(GetMetadataKey(id), metadata.ToJsonBytes());
        }

        var cacheVector = dataToStore is not null ? new Vector(dataToStore) : vector;
        _cache.Put(id, cacheVector);

        if (_hnsw is not null)
        {
            if (_indexingChannel is not null)
            {
                await _indexingChannel.Writer.WriteAsync(
                    new IndexingJob(id, cacheVector.Data, exists), ct).ConfigureAwait(false);
            }
            else
            {
                if (exists) _hnsw.MarkDeleted(id);
                _hnsw.Insert(id, cacheVector.Data);
            }
        }

        if (previousPayload is not null)
            _payloadIndex?.RemovePayload(id, previousPayload);
        _payloadIndex?.IndexPayload(id, metadata?.Payload);
        _ivf?.Insert(id, cacheVector.Data);

        long updatedCount = Interlocked.Read(ref _count);
        if (!exists)
        {
            updatedCount = Interlocked.Increment(ref _count);
            _store.Upsert(GetCountKey(), BitConverter.GetBytes(updatedCount));
        }

        var effectivePayload = metadata?.Payload ?? previousPayload;
        UpdateManifestFromPayload(effectivePayload);
        UpdateManifestAfterMutation(seq, updatedCount);
        PersistManifest();
        await AppendChangeAsync(CreateUpsertChange(seq, id, cacheVector.Data, effectivePayload), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Batch-Insert mehrerer Vektoren. Deutlich schneller als einzelne PutAsync-Aufrufe.
    /// Mit <paramref name="skipHnswIndex"/> = true werden die Vektoren nur gespeichert,
    /// der HNSW-Index wird nicht aktualisiert. Danach <see cref="RebuildIndexAsync"/> aufrufen.
    /// </summary>
    public async Task PutBatchAsync(IEnumerable<(ulong Id, Vector Vector, VectorMetadata? Metadata)> items, bool skipHnswIndex = false, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var batch = items.ToList();
        if (batch.Count == 0) return;

        // Phase 1: HNSW-Layers sequentiell berechnen (benötigt _nodes.Count)
        int[]? hnswLayers = _hnsw is not null && !skipHnswIndex ? _hnsw.PrepareLayers(batch.Count) : null;

        // Phase 2: Vektoren und HNSW-Nodes parallel vorbereiten (CPU-bound)
        var vectorPrep = new (ulong Id, byte[] VectorBytes, Vector CacheVector, VectorMetadata? Metadata)[batch.Count];
        HnswNode[]? hnswNodes = hnswLayers is not null ? new HnswNode[batch.Count] : null;

        Parallel.For(0, batch.Count, i =>
        {
            var (id, vector, metadata) = batch[i];
            if (vector.Dimension != _dimension)
                throw new ArgumentException($"Expected dimension {_dimension}, got {vector.Dimension}");

            float[]? dataToStore = null;
            if (_normalizeCosine)
            {
                dataToStore = new float[_dimension];
                vector.Span.CopyTo(dataToStore);
                VectorDistance.NormalizeL2(dataToStore.AsSpan());
            }

            var cacheVector = dataToStore is not null ? new Vector(dataToStore) : vector;
            var vectorBytes = cacheVector.ToByteArray();
            vectorPrep[i] = (id, vectorBytes, cacheVector, metadata);

            if (hnswNodes is not null)
            {
                hnswNodes[i] = _hnsw!.PrepareNode(id, cacheVector.Data, hnswLayers![i]);
            }
        });

        // Phase 3: Existenzprüfung (I/O-bound, parallel für bessere Durchsatz)
        var existsTasks = new Task<bool>[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            var id = vectorPrep[i].Id;
            existsTasks[i] = ExistsAsync(id, ct);
        }
        await Task.WhenAll(existsTasks);
        var exists = existsTasks.Select(t => t.Result).ToArray();

        var previousPayloads = new Dictionary<string, object>?[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            if (exists[i])
                previousPayloads[i] = await GetStoredPayloadAsync(vectorPrep[i].Id, ct).ConfigureAwait(false);
        }

        // Phase 4: Sequenznummer erhöhen
        var seq = Interlocked.Add(ref _sequence, batch.Count);
        var seqBytes = BitConverter.GetBytes(seq);

        // Phase 5: Alle Store-Operationen in einer einzigen Batch-Transaktion committen
        var storeItems = new List<(byte[] Key, byte[] Blob)>(batch.Count * 2 + 1);
        for (int i = 0; i < batch.Count; i++)
        {
            var (id, vectorBytes, _, metadata) = vectorPrep[i];
            storeItems.Add((GetVectorKey(id), vectorBytes));
            if (metadata is not null)
            {
                metadata.Id = id;
                metadata.Collection = _name;
                storeItems.Add((GetMetadataKey(id), metadata.ToJsonBytes()));
            }
        }
        storeItems.Add((GetSequenceKey(), seqBytes));
        _store.BulkUpsert(storeItems.Select(e => new KeyValuePair<byte[], byte[]>(e.Key, e.Blob)).ToList());

        // Phase 6: Cache aktualisieren
        for (int i = 0; i < batch.Count; i++)
        {
            _cache.Put(vectorPrep[i].Id, vectorPrep[i].CacheVector);
        }

        // Phase 7: Payload-Index aktualisieren
        for (int i = 0; i < batch.Count; i++)
        {
            if (previousPayloads[i] is not null)
                _payloadIndex?.RemovePayload(vectorPrep[i].Id, previousPayloads[i]);
            _payloadIndex?.IndexPayload(vectorPrep[i].Id, vectorPrep[i].Metadata?.Payload);
            UpdateManifestFromPayload(vectorPrep[i].Metadata?.Payload);
        }

        // Phase 8: HNSW-Index einfügen
        if (_hnsw is not null && !skipHnswIndex)
        {
            if (_indexingChannel is not null)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var id = vectorPrep[i].Id;
                    await _indexingChannel.Writer.WriteAsync(
                        new IndexingJob(id, vectorPrep[i].CacheVector.Data, exists[i]), ct).ConfigureAwait(false);
                }
            }
            else if (hnswNodes is not null)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    var id = vectorPrep[i].Id;
                    if (exists[i]) _hnsw.MarkDeleted(id);
                    _hnsw.InsertPreparedNode(hnswNodes[i]);
                }
            }
        }

        // Phase 9: IVF-Index einfügen
        if (_ivf is not null)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                _ivf.Insert(vectorPrep[i].Id, vectorPrep[i].CacheVector.Data);
            }
        }

        // Phase 10: Count aktualisieren
        var newCount = exists.Count(x => !x);
        Interlocked.Add(ref _count, newCount);
        _store.Upsert(GetCountKey(), BitConverter.GetBytes(Interlocked.Read(ref _count)));

        UpdateManifestAfterMutation(seq, Interlocked.Read(ref _count));
        PersistManifest();
        await AppendChangeAsync(CreateBatchUpsertChange(seq, vectorPrep, previousPayloads), ct).ConfigureAwait(false);
    }

    public async Task<VectorEntry?> GetAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_cache.TryGet(id, out var cached))
        {
            var metadata = _store.TryGet(GetMetadataKey(id), out var metaBytes) && metaBytes is not null
                ? VectorMetadata.FromJsonBytes(metaBytes) : null;
            return new VectorEntry { Id = id, Vector = cached, Metadata = metadata };
        }

        if (!_store.TryGet(GetVectorKey(id), out var vectorBytes) || vectorBytes is null) return null;

        var vector = Vector.FromByteArray(vectorBytes, _dimension);
        var metadata2 = _store.TryGet(GetMetadataKey(id), out var metaBytes2) && metaBytes2 is not null
            ? VectorMetadata.FromJsonBytes(metaBytes2) : null;

        _cache.Put(id, vector);
        return new VectorEntry { Id = id, Vector = vector, Metadata = metadata2 };
    }

    public async Task DeleteAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!await ExistsAsync(id, ct)) return;

        Dictionary<string, object>? previousPayload = await GetStoredPayloadAsync(id, ct).ConfigureAwait(false);

        var seq = Interlocked.Increment(ref _sequence);
        var seqBytes = BitConverter.GetBytes(seq);

        _store.Delete(GetVectorKey(id));
        _store.Delete(GetMetadataKey(id));
        _store.Upsert(GetSequenceKey(), seqBytes);

        _cache.Remove(id);
        _hnsw?.MarkDeleted(id);
        _ivf?.Remove(id);
        _payloadIndex?.RemovePayload(id, previousPayload);
        Interlocked.Decrement(ref _count);
        _store.Upsert(GetCountKey(), BitConverter.GetBytes(Interlocked.Read(ref _count)));

        if (Interlocked.Read(ref _count) == 0)
            ResetManifestPayloadDataFlags();

        UpdateManifestAfterMutation(seq, Interlocked.Read(ref _count));
        PersistManifest();
        await AppendChangeAsync(CreateDeleteChange(seq, id), ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<CollectionChangeEvent> ReadChangesAsync(long afterSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<CollectionChangeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _changeSubscribers[subscriptionId] = channel;

        try
        {
            var entries = _store.ScanPrefix(GetChangePrefix()).ToList();
            foreach (var (_, value) in entries.OrderBy(static entry => System.Text.Encoding.UTF8.GetString(entry.Key), StringComparer.Ordinal))
            {
                var change = JsonSerializer.Deserialize<CollectionChangeEvent>(value, s_changeJsonOptions);
                if (change is null || change.Sequence <= afterSequence)
                    continue;

                afterSequence = change.Sequence;
                yield return change;
            }

            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var change))
                {
                    if (change.Sequence <= afterSequence)
                        continue;

                    afterSequence = change.Sequence;
                    yield return change;
                }
            }
        }
        finally
        {
            if (_changeSubscribers.TryRemove(subscriptionId, out var existing))
                existing.Writer.TryComplete();
        }
    }

    public async Task<bool> ExistsAsync(ulong id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cache.TryGet(id, out _))
            return true;
        return _store.TryGet(GetVectorKey(id), out _);
    }

    public async IAsyncEnumerable<VectorSearchResult> SearchExactAsync(Vector query, int topK, FilterClause? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (query.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {query.Dimension}");

        var queryData = query.Span.ToArray();
        var candidates = new List<(ulong Id, float Score)>();

        // Prüfe, ob alle Vektoren im Cache sind
        var cachedIds = _cache.GetAllIds();
        if (cachedIds.Count() >= _count && _count > 0)
        {
            // Batch-Cache-Extraktion: alle Vektoren in einem Lock-Zyklus
            var vectors = _cache.TryGetBatch(cachedIds);

            var scores = new float[vectors.Count];
            Parallel.For(0, vectors.Count, i =>
            {
                var (id, data) = vectors[i];
                scores[i] = _metric switch
                {
                    DistanceMetric.Euclidean => VectorDistance.EuclideanSquared(queryData, data.AsSpan()),
                    DistanceMetric.Cosine => 1.0f - VectorDistance.DotProduct(queryData, data.AsSpan()),
                    DistanceMetric.DotProduct => -VectorDistance.DotProduct(queryData, data.AsSpan()),
                    _ => throw new NotSupportedException(_metric.ToString())
                };
            });

            for (int i = 0; i < vectors.Count; i++)
            {
                candidates.Add((vectors[i].Id, scores[i]));
            }
        }
        else
        {
            // Nicht alle im Cache - lade aus Store
            var prefix = System.Text.Encoding.UTF8.GetBytes($"c:{_name}:v:");
            var entries = _store.ScanPrefix(prefix).ToList();

            var vectors = new List<(ulong Id, float[] Data)>(entries.Count);
            foreach (var (key, value) in entries)
            {
                var keyStr = System.Text.Encoding.UTF8.GetString(key);
                if (TryParseIdFromKey(keyStr, out var id))
                {
                    var vector = Vector.FromByteArray(value, _dimension);
                    vectors.Add((id, vector.Data));
                }
            }

            var scores = new float[vectors.Count];
            Parallel.For(0, vectors.Count, i =>
            {
                var (id, data) = vectors[i];
                scores[i] = _metric switch
                {
                    DistanceMetric.Euclidean => VectorDistance.EuclideanSquared(queryData, data.AsSpan()),
                    DistanceMetric.Cosine => 1.0f - VectorDistance.DotProduct(queryData, data.AsSpan()),
                    DistanceMetric.DotProduct => -VectorDistance.DotProduct(queryData, data.AsSpan()),
                    _ => throw new NotSupportedException(_metric.ToString())
                };
            });

            for (int i = 0; i < vectors.Count; i++)
            {
                candidates.Add((vectors[i].Id, scores[i]));
            }
        }

        // Pre-Filter falls vorhanden
        PayloadIndexEvaluation? filterPlan = filter is not null ? _payloadIndex?.EvaluateForSearch(filter) : null;
        SimpleBitmap? allowedIds = filterPlan?.Bitmap;
        bool requiresPostFilter = filter is not null && (filterPlan is null || filterPlan.RequiresPostFilter || allowedIds is null);

        if (allowedIds is not null)
        {
            candidates = candidates.Where(c => allowedIds.Get(c.Id)).ToList();
        }

        // Top-K Heap: O(n log K) statt O(n log n)
        var topKHeap = new PriorityQueue<(ulong Id, float Score), float>(
            topK, Comparer<float>.Create((a, b) => b.CompareTo(a))); // max-heap

        if (!requiresPostFilter)
        {
            // Kein Post-Filter: direkt Top-K aus Heap
            foreach (var c in candidates)
            {
                if (topKHeap.Count < topK)
                    topKHeap.Enqueue(c, c.Score);
                else
                    topKHeap.EnqueueDequeue(c, c.Score);
            }

            var result = new List<(ulong Id, float Score)>(topKHeap.Count);
            while (topKHeap.Count > 0)
                result.Add(topKHeap.Dequeue());
            result.Reverse(); // Aufsteigend nach Score

            foreach (var (id, score) in result)
            {
                yield return new VectorSearchResult { Id = id, Score = score };
            }
        }
        else
        {
            var orderedCandidates = candidates.OrderBy(static candidate => candidate.Score).ToList();

            var yielded = 0;
            foreach (var (id, score) in orderedCandidates)
            {
                if (yielded >= topK) break;

                var metadata = await GetMetadataAsync(id, ct);
                if (FilterEvaluator.Evaluate(filter!, metadata?.Payload))
                {
                    yield return new VectorSearchResult { Id = id, Score = score, Metadata = metadata };
                    yielded++;
                }
            }
        }
    }

    public async IAsyncEnumerable<VectorSearchResult> SearchHnswAsync(Vector query, int topK, int? ef = null, FilterClause? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_hnsw is null)
            throw new InvalidOperationException("HNSW index is not enabled");
        if (query.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {query.Dimension}");

        float[] queryData = new float[_dimension];
        query.Span.CopyTo(queryData);
        if (_normalizeCosine)
            VectorDistance.NormalizeL2(queryData.AsSpan());

        if (filter is null)
        {
            var results = _hnsw.SearchKnn(id => id == ulong.MaxValue ? queryData : LoadVectorForHnsw(id), topK, ef);
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

        // Versuche Pre-Filter via PayloadIndex
        PayloadIndexEvaluation? filterPlan = _payloadIndex?.EvaluateForSearch(filter);
        SimpleBitmap? allowedBitmap = filterPlan?.Bitmap;
        bool requiresPostFilter = filterPlan is null || filterPlan.RequiresPostFilter || allowedBitmap is null;
        if (allowedBitmap is not null && !requiresPostFilter)
        {
            if (allowedBitmap.Count == 0)
                yield break;

            Func<ulong, bool> isAllowed = id => allowedBitmap.Get(id);
            var results = _hnsw.SearchKnn(id => id == ulong.MaxValue ? queryData : LoadVectorForHnsw(id), topK, ef, isAllowed);
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

        // Fallback bzw. approximativer Pre-Filter: Oversampling + exakter Post-Filter
        const int initialFactor = 10;
        const int maxFactor = 50;
        var factor = initialFactor;
        var yielded = new HashSet<ulong>();
        Func<ulong, bool>? preFilterPredicate = allowedBitmap is not null ? id => allowedBitmap.Get(id) : null;

        while (factor <= maxFactor)
        {
            var candidateCount = topK * factor;
            var results = _hnsw.SearchKnn(id => id == ulong.MaxValue ? queryData : LoadVectorForHnsw(id), candidateCount, ef, preFilterPredicate);

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

    public async IAsyncEnumerable<VectorSearchResult> SearchIvfAsync(Vector query, int topK, int? nprobe = null, FilterClause? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_ivf is null)
            throw new InvalidOperationException("IVF index is not enabled");
        if (query.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {query.Dimension}");

        float[] queryData = new float[_dimension];
        query.Span.CopyTo(queryData);
        if (_normalizeCosine)
            VectorDistance.NormalizeL2(queryData.AsSpan());

        if (filter is null)
        {
            var results = _ivf.SearchKnn(queryData, topK, LoadVectorForIvf, nprobe);
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

        // Versuche Pre-Filter via PayloadIndex
        PayloadIndexEvaluation? filterPlan = _payloadIndex?.EvaluateForSearch(filter);
        SimpleBitmap? allowedBitmap = filterPlan?.Bitmap;
        bool requiresPostFilter = filterPlan is null || filterPlan.RequiresPostFilter || allowedBitmap is null;
        if (allowedBitmap is not null && !requiresPostFilter)
        {
            if (allowedBitmap.Count == 0)
                yield break;

            Func<ulong, bool> isAllowed = id => allowedBitmap.Get(id);
            var results = _ivf.SearchKnn(queryData, topK, LoadVectorForIvf, nprobe, isAllowed);
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

        // Fallback bzw. approximativer Pre-Filter: Oversampling + exakter Post-Filter
        const int initialFactor = 10;
        const int maxFactor = 50;
        var factor = initialFactor;
        var yielded = new HashSet<ulong>();
        Func<ulong, bool>? preFilterPredicate = allowedBitmap is not null ? id => allowedBitmap.Get(id) : null;

        while (factor <= maxFactor)
        {
            var candidateCount = topK * factor;
            var results = _ivf.SearchKnn(queryData, candidateCount, LoadVectorForIvf, nprobe, preFilterPredicate);

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

    public async IAsyncEnumerable<VectorSearchResult> SearchTextAsync(string field, string query, int topK = 10, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            yield break;

        var indexedResults = _payloadIndex?.SearchFullText(field, query, topK, mode, notQuery);
        if (indexedResults is not null)
        {
            foreach (var (id, score) in indexedResults)
            {
                yield return new VectorSearchResult
                {
                    Id = id,
                    Score = score,
                    Metadata = await GetMetadataAsync(id, ct).ConfigureAwait(false)
                };
            }

            yield break;
        }

        var fallbackResults = await SearchTextFallbackAsync(field, query, topK, mode, notQuery, ct).ConfigureAwait(false);
        foreach (var result in fallbackResults)
            yield return result;
    }

    public async IAsyncEnumerable<VectorSearchResult> SearchHybridAsync(string field, string textQuery, Vector queryVector, int topK = 10, int textCandidateCount = 50, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (queryVector.Dimension != _dimension)
            throw new ArgumentException($"Expected dimension {_dimension}, got {queryVector.Dimension}");

        if (string.IsNullOrWhiteSpace(textQuery) || topK <= 0 || textCandidateCount <= 0)
            yield break;

        var textCandidates = new List<VectorSearchResult>();
        await foreach (var result in SearchTextAsync(field, textQuery, textCandidateCount, mode, notQuery, ct).ConfigureAwait(false))
            textCandidates.Add(result);

        if (textCandidates.Count == 0)
            yield break;

        float[] queryData = PrepareQueryVectorData(queryVector);
        var reranked = new List<(ulong Id, float VectorScore, float TextScore, VectorMetadata? Metadata)>(textCandidates.Count);

        foreach (var textCandidate in textCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await GetAsync(textCandidate.Id, ct).ConfigureAwait(false);
            if (entry is null)
                continue;

            reranked.Add((
                textCandidate.Id,
                ComputeSearchScore(queryData, entry.Vector.Span),
                textCandidate.Score,
                textCandidate.Metadata ?? entry.Metadata));
        }

        foreach (var result in reranked
            .OrderBy(static item => item.VectorScore)
            .ThenByDescending(static item => item.TextScore)
            .ThenBy(static item => item.Id)
            .Take(topK))
        {
            yield return new VectorSearchResult
            {
                Id = result.Id,
                Score = result.VectorScore,
                Metadata = result.Metadata
            };
        }
    }

    /// <summary>
    /// Baut den Payload-Index aus allen vorhandenen Metadaten neu auf.
    /// </summary>
    public async Task BuildPayloadIndexAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_payloadIndex is null) return;

        _payloadIndex.Clear();
        ResetManifestPayloadDataFlags();

        var ids = new List<ulong>();
        await foreach (var id in EnumerateIdsAsync(ct))
            ids.Add(id);

        for (int i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await GetAsync(ids[i], ct).ConfigureAwait(false);
            if (entry?.Metadata?.Payload is not null)
            {
                _payloadIndex.IndexPayload(ids[i], entry.Metadata.Payload);
                UpdateManifestFromPayload(entry.Metadata.Payload);
            }

            progress?.Report(ids.Count == 0 ? 100.0 : (i + 1) * 100.0 / ids.Count);
        }

        UpdateManifestAfterMutation(Interlocked.Read(ref _sequence), Interlocked.Read(ref _count));
        PersistManifest();

        if (ids.Count == 0)
            progress?.Report(100.0);
    }

    public async Task RebuildIndexAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (_hnsw is null && _ivf is null) return;

        _hnsw?.Clear();
        _ivf?.Clear();

        var ids = new List<ulong>();
        await foreach (var id in EnumerateIdsAsync(ct))
            ids.Add(id);

        if (ids.Count == 0) return;

        // Phase 1: Alle Vektoren parallel laden
        var vectors = new float[ids.Count][];
        Parallel.For(0, ids.Count, i =>
        {
            vectors[i] = LoadVectorForHnsw(ids[i]);
        });

        if (_hnsw is not null)
        {
            // Phase 2: HNSW-Layers sequentiell berechnen (benötigt _nodes.Count = 0 nach Clear)
            var layers = _hnsw.PrepareLayers(ids.Count);

            // Phase 3: HNSW-Nodes parallel vorbereiten
            var nodes = new HnswNode[ids.Count];
            Parallel.For(0, ids.Count, i =>
            {
                nodes[i] = _hnsw.PrepareNode(ids[i], vectors[i], layers[i]);
            });

            // Phase 4: Sequentiell in den Graph einfügen
            for (int i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _hnsw.InsertPreparedNode(nodes[i]);
                progress?.Report((i + 1) * 100.0 / ids.Count);
            }
        }

        if (_ivf is not null)
        {
            var ivfVectors = new List<(ulong Id, float[] Vector)>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
                ivfVectors.Add((ids[i], vectors[i]));

            _ivf.Build(ivfVectors);
            progress?.Report(100.0);
        }
    }

    /// <summary>
    /// Erzeugt einen Iterator über alle IDs dieser Collection.
    /// Nutzt Präfix-Scan über den Store.
    /// </summary>
    public async IAsyncEnumerable<ulong> EnumerateIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var prefix = System.Text.Encoding.UTF8.GetBytes($"c:{_name}:v:");
        var entries = _store.ScanPrefix(prefix);

        foreach (var (key, _) in entries)
        {
            var keyStr = System.Text.Encoding.UTF8.GetString(key);
            if (TryParseIdFromKey(keyStr, out var id))
                yield return id;
        }
    }

    private static bool TryParseIdFromKey(string key, out ulong id)
    {
        // Format: c:{name}:v:{id}
        id = 0;
        var lastColon = key.LastIndexOf(':');
        if (lastColon < 0 || lastColon == key.Length - 1) return false;
        return ulong.TryParse(key.AsSpan(lastColon + 1), out id);
    }

    private async Task<VectorMetadata?> GetMetadataAsync(ulong id, CancellationToken ct)
    {
        if (_store.TryGet(GetMetadataKey(id), out var metaBytes) && metaBytes is not null)
            return VectorMetadata.FromJsonBytes(metaBytes);
        return null;
    }

    private async Task<IReadOnlyList<VectorSearchResult>> SearchTextFallbackAsync(string field, string query, int topK, FullTextQueryMode mode, string? notQuery, CancellationToken ct)
    {
        var parsedQuery = FullTextQueryParser.Parse(query, notQuery);
        if (!parsedQuery.HasPositiveClauses)
            return Array.Empty<VectorSearchResult>();

        string[] positiveTerms = parsedQuery.EnumeratePositiveTerms().Distinct(StringComparer.Ordinal).ToArray();
        var documents = new List<(ulong Id, VectorMetadata Metadata, FullTextDocumentTerms Terms)>();
        var documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        await foreach (var id in EnumerateIdsAsync(ct))
        {
            var metadata = await GetMetadataAsync(id, ct).ConfigureAwait(false);
            if (metadata?.Payload is null)
                continue;

            if (!metadata.Payload.TryGetValue(field, out var value))
                continue;

            if (!TryGetPayloadString(value, out string? text))
                continue;

            var document = FullTextQueryParser.BuildDocumentTerms(text);
            if (document.Length == 0)
                continue;

            documents.Add((id, metadata, document));

            foreach (string term in positiveTerms)
            {
                if (!document.TermFrequencies.ContainsKey(term))
                    continue;

                documentFrequencies.TryGetValue(term, out int count);
                documentFrequencies[term] = count + 1;
            }
        }

        if (documents.Count == 0)
            return Array.Empty<VectorSearchResult>();

        double averageDocumentLength = Math.Max(1.0, documents.Average(static document => document.Terms.Length));
        return documents
            .Where(document => FullTextQueryParser.Matches(document.Terms, parsedQuery, mode))
            .Select(document => new VectorSearchResult
            {
                Id = document.Id,
                Score = FullTextQueryParser.ComputeBm25Score(document.Terms, parsedQuery, documentFrequencies, documents.Count, averageDocumentLength),
                Metadata = document.Metadata
            })
            .Where(static result => result.Score > 0)
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id)
            .Take(topK)
            .ToArray();
    }

    private float[] PrepareQueryVectorData(Vector query)
    {
        float[] queryData = new float[_dimension];
        query.Span.CopyTo(queryData);
        if (_normalizeCosine)
            VectorDistance.NormalizeL2(queryData.AsSpan());

        return queryData;
    }

    private float ComputeSearchScore(float[] queryData, ReadOnlySpan<float> data)
    {
        return _metric switch
        {
            DistanceMetric.Euclidean => VectorDistance.EuclideanSquared(queryData, data),
            DistanceMetric.Cosine => 1.0f - VectorDistance.DotProduct(queryData, data),
            DistanceMetric.DotProduct => -VectorDistance.DotProduct(queryData, data),
            _ => throw new NotSupportedException(_metric.ToString())
        };
    }

    private static bool TryGetPayloadString(object value, [NotNullWhen(true)] out string? text)
    {
        if (value is string stringValue)
        {
            text = stringValue;
            return true;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            text = element.GetString();
            return !string.IsNullOrEmpty(text);
        }

        text = null;
        return false;
    }

    private float[] LoadVectorForHnsw(ulong id)
    {
        if (_cache.TryGet(id, out var cached))
            return cached.Data;

        if (_store.TryGet(GetVectorKey(id), out var vectorBytes) && vectorBytes is not null)
        {
            var vector = Vector.FromByteArray(vectorBytes, _dimension);
            _cache.Put(id, vector);
            return vector.Data;
        }
        return new float[_dimension];
    }

    private float[] LoadVectorForIvf(ulong id)
    {
        if (_cache.TryGet(id, out var cached))
            return cached.Data;

        if (_store.TryGet(GetVectorKey(id), out var vectorBytes) && vectorBytes is not null)
        {
            var vector = Vector.FromByteArray(vectorBytes, _dimension);
            _cache.Put(id, vector);
            return vector.Data;
        }
        return new float[_dimension];
    }

    /// <summary>
    /// Wartet, bis der Hintergrund-HNSW-Worker alle pending Jobs verarbeitet hat.
    /// Kein Effekt, wenn AsyncIndexing deaktiviert ist.
    /// </summary>
    public async Task WaitForIndexingAsync(CancellationToken ct = default)
    {
        if (_indexingChannel is null) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _indexingChannel.Writer.WriteAsync(new IndexingJob(0, Array.Empty<float>(), false, tcs), ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunIndexingWorkerAsync(CancellationToken ct)
    {
        if (_indexingChannel is null || _hnsw is null) return;

        const int batchSize = 1000;
        var batch = new List<IndexingJob>(batchSize);

        await foreach (var job in _indexingChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (job.FlushSignal is not null)
            {
                if (batch.Count > 0)
                {
                    ProcessIndexingBatch(batch);
                    batch.Clear();
                }
                job.FlushSignal.TrySetResult();
                continue;
            }

            batch.Add(job);
            if (batch.Count >= batchSize)
            {
                ProcessIndexingBatch(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            ProcessIndexingBatch(batch);
        }
    }

    private void ProcessIndexingBatch(List<IndexingJob> jobs)
    {
        if (_hnsw is null || jobs.Count == 0) return;

        foreach (var job in jobs)
        {
            if (job.Exists) _hnsw.MarkDeleted(job.Id);
        }

        var layers = _hnsw.PrepareLayers(jobs.Count);

        var nodes = new HnswNode[jobs.Count];
        Parallel.For(0, jobs.Count, i =>
        {
            nodes[i] = _hnsw.PrepareNode(jobs[i].Id, jobs[i].Vector, layers[i]);
        });

        for (int i = 0; i < jobs.Count; i++)
        {
            _hnsw.InsertPreparedNode(nodes[i]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        PersistManifest();
        _disposed = true;

        if (_indexingCts is not null)
        {
            _indexingCts.Cancel();
            _indexingChannel?.Writer.TryComplete();
            try { _indexingWorker?.Wait(TimeSpan.FromSeconds(10)); } catch { }
            _indexingCts.Dispose();
        }

        _hnsw?.Dispose();
        _payloadIndex?.Dispose();
    }

    public CollectionManifest GetManifest()
    {
        return _manifest.Clone();
    }

    private async Task<Dictionary<string, object>?> GetStoredPayloadAsync(ulong id, CancellationToken ct)
    {
        if (_store.TryGet(GetMetadataKey(id), out var metaBytes) && metaBytes is not null)
            return VectorMetadata.FromJsonBytes(metaBytes)?.Payload;
        return null;
    }

    private CollectionManifest LoadManifest(string name)
    {
        if (!_store.TryGet(GetManifestKey(), out var raw) || raw is null)
            return new CollectionManifest { Name = name };

        try
        {
            return JsonSerializer.Deserialize<CollectionManifest>(raw) ?? new CollectionManifest { Name = name };
        }
        catch
        {
            return new CollectionManifest { Name = name };
        }
    }

    private static PayloadIndexOptions ResolvePayloadIndexOptions(CollectionManifest manifest, PayloadIndexOptions? requestedOptions)
    {
        if (requestedOptions is not null)
            return requestedOptions;

        return new PayloadIndexOptions
        {
            PersistentMatch = manifest.PersistentMatch,
            PersistentRange = manifest.PersistentRange,
            PersistentFullText = manifest.PersistentFullText,
            PersistentGeo = manifest.PersistentGeo,
            StoragePath = manifest.PayloadIndexStoragePath,
        };
    }

    private void ApplyManifestConfiguration(bool enablePayloadIndex)
    {
        long count = Interlocked.Read(ref _count);
        bool payloadIndexVersionCurrent = _manifest.PayloadIndexVersion == 0
            || _manifest.PayloadIndexVersion == CollectionManifest.CurrentPayloadIndexVersion;

        _manifest.Name = _name;
        _manifest.ManifestVersion = CollectionManifest.CurrentManifestVersion;
        _manifest.PayloadIndexVersion = CollectionManifest.CurrentPayloadIndexVersion;
        _manifest.ChangeStreamVersion = CollectionManifest.CurrentChangeStreamVersion;
        _manifest.PayloadIndexEnabled = enablePayloadIndex;
        _manifest.PersistentMatch = _payloadIndexOptions.PersistentMatch;
        _manifest.PersistentRange = _payloadIndexOptions.PersistentRange;
        _manifest.PersistentFullText = _payloadIndexOptions.PersistentFullText;
        _manifest.PersistentGeo = _payloadIndexOptions.PersistentGeo;
        _manifest.PayloadIndexStoragePath = _payloadIndexOptions.StoragePath;
        _manifest.ChangeSequence = Math.Max(_manifest.ChangeSequence, Interlocked.Read(ref _sequence));
        _manifest.PayloadIndexWarm = payloadIndexVersionCurrent && ComputePayloadIndexWarm(count);
        _manifest.LastUpdatedUtc = DateTime.UtcNow;
    }

    private void UpdateManifestFromPayload(Dictionary<string, object>? payload)
    {
        if (payload is null)
            return;

        foreach (object rawValue in payload.Values)
        {
            object value = rawValue is JsonElement element ? UnwrapJsonElement(element) : rawValue;

            if (value is bool)
                _manifest.HasMatchIndexData = true;

            if (value is string)
            {
                _manifest.HasMatchIndexData = true;
                _manifest.HasFullTextIndexData = true;
            }

            if (value is long or int or ulong)
            {
                _manifest.HasMatchIndexData = true;
                _manifest.HasRangeIndexData = true;
            }

            if (value is JsonElement geoElement && TryExtractGeo(geoElement, out _, out _))
                _manifest.HasGeoIndexData = true;
            else if (TryExtractGeo(value, out _, out _))
                _manifest.HasGeoIndexData = true;
        }
    }

    private void ResetManifestPayloadDataFlags()
    {
        _manifest.HasMatchIndexData = false;
        _manifest.HasRangeIndexData = false;
        _manifest.HasFullTextIndexData = false;
        _manifest.HasGeoIndexData = false;
    }

    private void UpdateManifestAfterMutation(long sequence, long count)
    {
        _manifest.ChangeSequence = sequence;
        _manifest.PayloadIndexWarm = ComputePayloadIndexWarm(count);
        _manifest.LastUpdatedUtc = DateTime.UtcNow;
    }

    private bool ComputePayloadIndexWarm(long count)
    {
        if (!_manifest.PayloadIndexEnabled)
            return false;

        if (count == 0)
            return true;

        return (!_manifest.HasMatchIndexData || _payloadIndexOptions.PersistentMatch)
            && (!_manifest.HasRangeIndexData || _payloadIndexOptions.PersistentRange)
            && (!_manifest.HasFullTextIndexData || _payloadIndexOptions.PersistentFullText)
            && (!_manifest.HasGeoIndexData || _payloadIndexOptions.PersistentGeo);
    }

    private void PersistManifest()
    {
        if (_disposed)
            return;

        _store.Upsert(GetManifestKey(), JsonSerializer.SerializeToUtf8Bytes(_manifest));
    }

    private async Task AppendChangeAsync(CollectionChangeEvent change, CancellationToken ct)
    {
        _store.Upsert(GetChangeKey(change.Sequence), JsonSerializer.SerializeToUtf8Bytes(change, s_changeJsonOptions));

        foreach (var subscriber in _changeSubscribers)
        {
            if (!subscriber.Value.Writer.TryWrite(change) && _changeSubscribers.TryRemove(subscriber.Key, out var stale))
                stale.Writer.TryComplete();
        }
    }

    private CollectionChangeEvent CreateUpsertChange(long sequence, ulong id, float[] vector, Dictionary<string, object>? payload)
    {
        return new CollectionChangeEvent
        {
            Sequence = sequence,
            Collection = _name,
            Operation = "upsert",
            TimestampUtc = DateTime.UtcNow,
            Items = new List<CollectionChangeItem>
            {
                new CollectionChangeItem
                {
                    Id = id,
                    Vector = vector.ToArray(),
                    Payload = ClonePayload(payload),
                }
            }
        };
    }

    private CollectionChangeEvent CreateBatchUpsertChange(long sequence, (ulong Id, byte[] VectorBytes, Vector CacheVector, VectorMetadata? Metadata)[] vectorPrep, Dictionary<string, object>?[] previousPayloads)
    {
        return new CollectionChangeEvent
        {
            Sequence = sequence,
            Collection = _name,
            Operation = "batch-upsert",
            TimestampUtc = DateTime.UtcNow,
            Items = vectorPrep.Select((item, index) => new CollectionChangeItem
            {
                Id = item.Id,
                Vector = item.CacheVector.Data.ToArray(),
                Payload = ClonePayload(item.Metadata?.Payload ?? previousPayloads[index]),
            }).ToList()
        };
    }

    private CollectionChangeEvent CreateDeleteChange(long sequence, ulong id)
    {
        return new CollectionChangeEvent
        {
            Sequence = sequence,
            Collection = _name,
            Operation = "delete",
            TimestampUtc = DateTime.UtcNow,
            Items = new List<CollectionChangeItem>
            {
                new CollectionChangeItem { Id = id }
            }
        };
    }

    private static Dictionary<string, object>? ClonePayload(Dictionary<string, object>? payload)
    {
        if (payload is null)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(payload));
    }

    private static object UnwrapJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element
        };
    }

    private static bool TryExtractGeo(object value, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() >= 2)
            {
                lat = element[0].GetDouble();
                lon = element[1].GetDouble();
                return true;
            }

            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("lat", out var latProp) &&
                element.TryGetProperty("lon", out var lonProp))
            {
                lat = latProp.GetDouble();
                lon = lonProp.GetDouble();
                return true;
            }

            return false;
        }

        if (value is Dictionary<string, object> dict &&
            dict.TryGetValue("lat", out var rawLat) &&
            dict.TryGetValue("lon", out var rawLon))
        {
            lat = rawLat switch
            {
                double d => d,
                long l => l,
                int i => i,
                float f => f,
                JsonElement je => je.GetDouble(),
                _ => Convert.ToDouble(rawLat)
            };
            lon = rawLon switch
            {
                double d => d,
                long l => l,
                int i => i,
                float f => f,
                JsonElement je => je.GetDouble(),
                _ => Convert.ToDouble(rawLon)
            };
            return true;
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VectorCollection));
    }
}
