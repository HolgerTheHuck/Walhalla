// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Indexes.FullText;
using Walhalla.Indexes.Primitives;
using Walhalla.Indexes.Spatial;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Core.Runtime;
using Walhalla.Storage.Core.Transactions;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Filtering;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Sekundaerer Payload-Index fuer schnelle Filter-Auswertung.
/// Match/Range bleiben in-memory, FullText und Geo koennen optional persistent sein.
/// </summary>
public sealed class PayloadIndex : IDisposable
{
    private const float Bm25K1 = 1.2f;
    private const float Bm25B = 0.75f;

    private readonly Dictionary<string, Dictionary<object, SimpleBitmap>> _invertedIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedList<long, SimpleBitmap>> _rangeIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RTree> _geoIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FullTextIndex> _fullTextIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistentFullTextIndex> _persistentFullTextIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistentRTree> _persistentGeoIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, Dictionary<string, object>> _indexedPayloads = new();

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly PayloadIndexOptions _options;
    private readonly string? _collectionName;
    private readonly IKeyValueStore? _store;
    private readonly IKeyValueStore? _payloadStore;
    private readonly byte[]? _allIdsKey;
    private readonly byte[]? _matchPrefix;
    private readonly byte[]? _rangePrefix;
    private readonly byte[]? _fullTextPrefix;
    private readonly byte[]? _geoPrefix;
    private readonly SimpleBitmap _allIds;
    private bool _isFullyBuilt;
    private bool _disposed;

    public PayloadIndex()
        : this(null, null, null, assumeFullyBuilt: true, createPersistentStores: false)
    {
    }

    internal PayloadIndex(string collectionName, IKeyValueStore store, PayloadIndexOptions? options, bool assumeFullyBuilt)
        : this(collectionName, store, options, assumeFullyBuilt, createPersistentStores: true)
    {
    }

    private PayloadIndex(string? collectionName, IKeyValueStore? store, PayloadIndexOptions? options, bool assumeFullyBuilt, bool createPersistentStores = false)
    {
        _options = options ?? new PayloadIndexOptions();
        _collectionName = collectionName;
        _store = store;
        _isFullyBuilt = assumeFullyBuilt;

        if (collectionName is not null && store is not null)
        {
            _allIdsKey = Encoding.UTF8.GetBytes($"pi:{collectionName}:allids");
            _matchPrefix = Encoding.UTF8.GetBytes($"pi:{collectionName}:match:");
            _rangePrefix = Encoding.UTF8.GetBytes($"pi:{collectionName}:range:");
            _fullTextPrefix = Encoding.UTF8.GetBytes($"pi:{collectionName}:ft:");
            _geoPrefix = Encoding.UTF8.GetBytes($"pi:{collectionName}:geo:");
        }

        _allIds = LoadAllIds(store, _allIdsKey);

        if (createPersistentStores && (_options.PersistentMatch || _options.PersistentRange || _options.PersistentFullText))
        {
            if (collectionName is null || store is null)
                throw new ArgumentException("Persistent payload indices require a collection name and store.");

            string rootPath = ResolveStoragePath(collectionName, _options);
            Directory.CreateDirectory(rootPath);
            _payloadStore = new WalhallaStore(new Walhalla.Storage.Core.Configuration.WalhallaOptions(Path.Combine(rootPath, "payload")));
        }
    }

    public void IndexPayload(ulong id, Dictionary<string, object>? payload)
    {
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            if (_indexedPayloads.TryGetValue(id, out var previousPayload))
            {
                RemovePayloadDataLocked(id, previousPayload);
                _indexedPayloads.Remove(id);
            }

            if (payload is null || payload.Count == 0)
            {
                _allIds.Clear(id);
                PersistAllIdsLocked();
                return;
            }

            var normalizedPayload = NormalizePayload(payload);
            _allIds.Set(id);

            foreach (var (key, value) in normalizedPayload)
            {
                if (IsMatchIndexable(value))
                    AddMatchValueLocked(id, key, value);

                if (value is long l)
                    AddRangeValueLocked(id, key, l);

                if (TryExtractGeo(value, out var lat, out var lon))
                {
                    if (_options.PersistentGeo)
                        GetOrCreatePersistentGeoIndex(key).Insert((long)id, new[] { lat, lon }, new[] { lat, lon });
                    else
                        GetOrCreateGeoIndex(key).Insert((long)id, new[] { lat, lon }, new[] { lat, lon });
                }

                if (value is string text)
                {
                    if (_options.PersistentFullText)
                        GetOrCreatePersistentFullTextIndex(key).IndexDocument((long)id, text);
                    else
                        GetOrCreateFullTextIndex(key).IndexDocument((long)id, text);
                }
            }

            _indexedPayloads[id] = normalizedPayload;
            PersistAllIdsLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RemovePayload(ulong id)
    {
        RemovePayload(id, null);
    }

    public void RemovePayload(ulong id, Dictionary<string, object>? payload)
    {
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            Dictionary<string, object>? normalizedPayload = payload is not null ? NormalizePayload(payload) : null;
            if (normalizedPayload is null && !_indexedPayloads.TryGetValue(id, out normalizedPayload))
            {
                _allIds.Clear(id);
                PersistAllIdsLocked();
                return;
            }

            RemovePayloadDataLocked(id, normalizedPayload!);
            _indexedPayloads.Remove(id);
            _allIds.Clear(id);
            PersistAllIdsLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Liefert nur exakte Index-Ergebnisse. Approximative Geo-PreFilter liefern weiterhin null.
    /// </summary>
    public SimpleBitmap? Evaluate(FilterClause clause)
    {
        var evaluation = EvaluateForSearch(clause);
        return evaluation.IsExact ? evaluation.Bitmap : null;
    }

    /// <summary>
    /// Liefert einen Ausfuehrungsplan fuer den Suchpfad inklusive approximativer Geo-PreFilter.
    /// </summary>
    public PayloadIndexEvaluation EvaluateForSearch(FilterClause clause)
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            return EvaluateLocked(clause);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            ClearLocked();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Baut den Index aus einer bestehenden Collection auf.</summary>
    public async Task BuildFromCollection(VectorCollection collection, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        Clear();

        await foreach (var id in collection.EnumerateIdsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var entry = await collection.GetAsync(id, ct);
            if (entry?.Metadata?.Payload is not null)
                IndexPayload(id, entry.Metadata.Payload);
        }

        _lock.EnterWriteLock();
        try
        {
            _isFullyBuilt = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _payloadStore?.Dispose();
        _lock.Dispose();
    }

    private PayloadIndexEvaluation EvaluateLocked(FilterClause clause)
    {
        SimpleBitmap? mustResult = null;
        bool requiresPostFilter = false;

        foreach (var condition in clause.Must)
        {
            var evaluation = EvaluateCondition(condition);
            if (evaluation.Bitmap is null)
                return new PayloadIndexEvaluation(null, true);

            mustResult = mustResult is null ? evaluation.Bitmap : mustResult.And(evaluation.Bitmap);
            requiresPostFilter |= evaluation.RequiresPostFilter;
            if (mustResult.Count == 0)
                return new PayloadIndexEvaluation(mustResult, requiresPostFilter);
        }

        SimpleBitmap? mustNotResult = null;
        if (clause.MustNot is not null)
        {
            foreach (var condition in clause.MustNot)
            {
                var evaluation = EvaluateCondition(condition);
                if (evaluation.Bitmap is null)
                    return new PayloadIndexEvaluation(null, true);

                mustNotResult = mustNotResult is not null ? mustNotResult.Or(evaluation.Bitmap) : evaluation.Bitmap;
                requiresPostFilter |= evaluation.RequiresPostFilter;
            }
        }

        SimpleBitmap? shouldResult = null;
        if (clause.Should is not null && clause.Should.Length > 0 && clause.Must.Length == 0)
        {
            foreach (var condition in clause.Should)
            {
                var evaluation = EvaluateCondition(condition);
                if (evaluation.Bitmap is null)
                    return new PayloadIndexEvaluation(null, true);

                shouldResult = shouldResult is not null ? shouldResult.Or(evaluation.Bitmap) : evaluation.Bitmap;
                requiresPostFilter |= evaluation.RequiresPostFilter;
            }
        }

        SimpleBitmap? result = mustResult;
        if (shouldResult is not null && clause.Must.Length == 0)
            result = shouldResult;

        if (mustNotResult is not null)
        {
            result = result is null ? _allIds.AndNot(mustNotResult) : result.AndNot(mustNotResult);
        }

        if (result is null)
            return new PayloadIndexEvaluation(_allIds, requiresPostFilter);

        return new PayloadIndexEvaluation(result, requiresPostFilter);
    }

    private PayloadIndexEvaluation EvaluateCondition(Condition condition)
    {
        return condition switch
        {
            MatchCondition match => new PayloadIndexEvaluation(EvaluateMatch(match), false),
            RangeCondition range => new PayloadIndexEvaluation(EvaluateRange(range), false),
            GeoRadiusCondition geo => EvaluateGeo(geo),
            FullTextCondition ft => new PayloadIndexEvaluation(EvaluateFullText(ft), false),
            _ => new PayloadIndexEvaluation(null, true)
        };
    }

    private PayloadIndexEvaluation EvaluateGeo(GeoRadiusCondition condition)
    {
        if (_options.PersistentGeo)
        {
            var persistentIndex = GetOrCreatePersistentGeoIndex(condition.Key);
            if (!_isFullyBuilt && persistentIndex.EntryCount == 0)
                return new PayloadIndexEvaluation(null, true);

            var (min, max) = BuildGeoBoundingBox(condition.Lat, condition.Lon, condition.RadiusMeters);
            return BuildGeoEvaluation(persistentIndex.Search(min, max));
        }

        if (!_isFullyBuilt)
            return new PayloadIndexEvaluation(null, true);

        if (!_geoIndex.TryGetValue(condition.Key, out var rtree))
            return new PayloadIndexEvaluation(new SimpleBitmap(), false);

        var (bboxMin, bboxMax) = BuildGeoBoundingBox(condition.Lat, condition.Lon, condition.RadiusMeters);
        return BuildGeoEvaluation(rtree.Search(bboxMin, bboxMax));
    }

    private SimpleBitmap? EvaluateFullText(FullTextCondition condition)
    {
        if (_options.PersistentFullText)
        {
            if (!_isFullyBuilt)
                return null;

            var persistentIndex = GetOrCreatePersistentFullTextIndex(condition.Key);
            return persistentIndex.Search(condition.Query, condition.Mode, condition.NotQuery) ?? new SimpleBitmap();
        }

        if (!_isFullyBuilt)
            return null;

        if (!_fullTextIndex.TryGetValue(condition.Key, out var ftIndex))
            return new SimpleBitmap();

        return condition.Mode == FullTextQueryMode.Any
            ? ftIndex.SearchAny(condition.Query, condition.NotQuery) ?? new SimpleBitmap()
            : ftIndex.Search(condition.Query, condition.Mode, condition.NotQuery) ?? new SimpleBitmap();
    }

    internal IReadOnlyList<(ulong Id, float Score)>? SearchFullText(string key, string query, int topK, FullTextQueryMode mode = FullTextQueryMode.All, string? notQuery = null)
    {
        ThrowIfDisposed();

        if (topK <= 0)
            return Array.Empty<(ulong Id, float Score)>();

        _lock.EnterReadLock();
        try
        {
            if (_options.PersistentFullText)
            {
                if (!_isFullyBuilt)
                    return null;

                var persistentIndex = GetOrCreatePersistentFullTextIndex(key);
                var results = mode == FullTextQueryMode.Any
                    ? persistentIndex.SearchAnyScored(query, topK, notQuery)
                    : persistentIndex.SearchScored(query, topK, mode, notQuery);

                return results.Select(static result => ((ulong)result.Id, result.Score)).ToArray();
            }

            if (!_isFullyBuilt)
                return null;

            return SearchFullTextLocked(key, query, topK, mode, notQuery);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private SimpleBitmap? EvaluateMatch(MatchCondition condition)
    {
        object? key = condition.Match switch
        {
            MatchString m => m.Value,
            MatchInt m => (object)m.Value,
            MatchBool m => m.Value ? 1L : 0L,
            MatchDouble => null,
            _ => null
        };

        if (key is null)
            return null;

        if (_options.PersistentMatch)
        {
            if (_payloadStore is null)
                return null;

            return _payloadStore.TryGet(BuildPersistentMatchKey(condition.Key, key), out var payload) && payload is not null
                ? SimpleBitmap.Deserialize(payload)
                : new SimpleBitmap();
        }

        if (!_isFullyBuilt)
            return null;

        if (!_invertedIndex.TryGetValue(condition.Key, out var termMap))
            return new SimpleBitmap();

        return termMap.TryGetValue(key, out var bitmap) ? bitmap : new SimpleBitmap();
    }

    private SimpleBitmap? EvaluateRange(RangeCondition condition)
    {
        var r = condition.Range;
        long min = r.Gt.HasValue ? r.Gt.Value + 1 : (r.Gte.HasValue ? r.Gte.Value : long.MinValue);
        long max = r.Lt.HasValue ? r.Lt.Value - 1 : (r.Lte.HasValue ? r.Lte.Value : long.MaxValue);

        if (_options.PersistentRange)
        {
            if (_payloadStore is null)
                return null;

            if (_rangePrefix is null)
                return new SimpleBitmap();

            var entries = _payloadStore.ScanPrefix(BuildPersistentRangeFieldPrefix(condition.Key));
            SimpleBitmap? persistedResult = null;
            foreach (var (entryKey, payload) in entries)
            {
                long value = ReadPersistentRangeValue(entryKey);
                if (value < min) continue;
                if (value > max) continue;

                var bitmap = SimpleBitmap.Deserialize(payload);
                persistedResult = persistedResult is not null ? persistedResult.Or(bitmap) : bitmap;
            }

            return persistedResult ?? new SimpleBitmap();
        }

        if (!_isFullyBuilt)
            return null;

        if (!_rangeIndex.TryGetValue(condition.Key, out var sortedList))
            return new SimpleBitmap();

        SimpleBitmap? result = null;
        foreach (var (value, bitmap) in sortedList)
        {
            if (value < min) continue;
            if (value > max) break;
            result = result is not null ? result.Or(bitmap) : bitmap;
        }

        return result ?? new SimpleBitmap();
    }

    private void AddMatchValueLocked(ulong id, string key, object value)
    {
        if (!_invertedIndex.TryGetValue(key, out var termMap))
        {
            termMap = new Dictionary<object, SimpleBitmap>();
            _invertedIndex[key] = termMap;
        }

        if (!termMap.TryGetValue(value, out var bitmap))
        {
            bitmap = new SimpleBitmap();
            termMap[value] = bitmap;
        }

        bitmap.Set(id);

        if (_options.PersistentMatch)
            PersistBitmapLocked(BuildPersistentMatchKey(key, value), bitmap);
    }

    private void AddRangeValueLocked(ulong id, string key, long value)
    {
        if (!_rangeIndex.TryGetValue(key, out var sortedList))
        {
            sortedList = new SortedList<long, SimpleBitmap>();
            _rangeIndex[key] = sortedList;
        }

        if (!sortedList.TryGetValue(value, out var bitmap))
        {
            bitmap = new SimpleBitmap();
            sortedList[value] = bitmap;
        }

        bitmap.Set(id);

        if (_options.PersistentRange)
            PersistBitmapLocked(BuildPersistentRangeKey(key, value), bitmap);
    }

    private void RemovePayloadDataLocked(ulong id, Dictionary<string, object> payload)
    {
        foreach (var (key, value) in payload)
        {
            if (IsMatchIndexable(value))
                RemoveMatchValueLocked(id, key, value);

            if (value is long l)
                RemoveRangeValueLocked(id, key, l);

            if (TryExtractGeo(value, out _, out _))
            {
                if (_options.PersistentGeo)
                {
                    var persistentGeo = GetOrCreatePersistentGeoIndex(key);
                    persistentGeo.Delete((long)id);
                    if (persistentGeo.EntryCount == 0)
                    {
                        persistentGeo.Clear();
                        _persistentGeoIndex.Remove(key);
                    }
                }
                else if (_geoIndex.TryGetValue(key, out var rtree))
                {
                    rtree.Delete((long)id);
                    if (rtree.EntryCount == 0)
                        _geoIndex.Remove(key);
                }
            }

            if (value is string)
            {
                if (_options.PersistentFullText)
                {
                    var persistentFullText = GetOrCreatePersistentFullTextIndex(key);
                    persistentFullText.RemoveDocument((long)id);
                    if (persistentFullText.DocumentCount == 0)
                    {
                        persistentFullText.Clear();
                        _persistentFullTextIndex.Remove(key);
                    }
                }
                else if (_fullTextIndex.TryGetValue(key, out var ftIndex))
                {
                    ftIndex.RemoveDocument((long)id);
                    if (ftIndex.DocumentCount == 0)
                        _fullTextIndex.Remove(key);
                }
            }
        }
    }

    private void RemoveMatchValueLocked(ulong id, string key, object value)
    {
        if (!_invertedIndex.TryGetValue(key, out var termMap))
            return;

        if (!termMap.TryGetValue(value, out var bitmap))
            return;

        bitmap.Clear(id);
        if (_options.PersistentMatch)
        {
            if (bitmap.Count == 0)
                DeletePersistentKeyLocked(BuildPersistentMatchKey(key, value));
            else
                PersistBitmapLocked(BuildPersistentMatchKey(key, value), bitmap);
        }

        if (bitmap.Count == 0)
            termMap.Remove(value);
        if (termMap.Count == 0)
            _invertedIndex.Remove(key);
    }

    private void RemoveRangeValueLocked(ulong id, string key, long value)
    {
        if (!_rangeIndex.TryGetValue(key, out var sortedList))
            return;

        if (!sortedList.TryGetValue(value, out var bitmap))
            return;

        bitmap.Clear(id);
        if (_options.PersistentRange)
        {
            if (bitmap.Count == 0)
                DeletePersistentKeyLocked(BuildPersistentRangeKey(key, value));
            else
                PersistBitmapLocked(BuildPersistentRangeKey(key, value), bitmap);
        }

        if (bitmap.Count == 0)
            sortedList.Remove(value);
        if (sortedList.Count == 0)
            _rangeIndex.Remove(key);
    }

    private RTree GetOrCreateGeoIndex(string key)
    {
        if (!_geoIndex.TryGetValue(key, out var rtree))
        {
            rtree = new RTree(2, maxEntries: _options.GeoMaxEntries);
            _geoIndex[key] = rtree;
        }

        return rtree;
    }

    private FullTextIndex GetOrCreateFullTextIndex(string key)
    {
        if (!_fullTextIndex.TryGetValue(key, out var ftIndex))
        {
            ftIndex = new FullTextIndex();
            _fullTextIndex[key] = ftIndex;
        }

        return ftIndex;
    }

    private PersistentFullTextIndex GetOrCreatePersistentFullTextIndex(string key)
    {
        if (_payloadStore is null || _collectionName is null)
            throw new InvalidOperationException("Persistent full-text index is not configured.");

        if (!_persistentFullTextIndex.TryGetValue(key, out var persistentIndex))
        {
            persistentIndex = new PersistentFullTextIndex(_payloadStore, BuildFieldKeyPrefix("ft", key));
            _persistentFullTextIndex[key] = persistentIndex;
        }

        return persistentIndex;
    }

    private PersistentRTree GetOrCreatePersistentGeoIndex(string key)
    {
        if (_store is null || _collectionName is null)
            throw new InvalidOperationException("Persistent geo index is not configured.");

        if (!_persistentGeoIndex.TryGetValue(key, out var persistentIndex))
        {
            persistentIndex = new PersistentRTree(_store, BuildFieldKeyPrefix("geo", key), dimensions: 2, maxEntries: _options.GeoMaxEntries);
            _persistentGeoIndex[key] = persistentIndex;
        }

        return persistentIndex;
    }

    private IReadOnlyList<(ulong Id, float Score)> SearchFullTextLocked(string key, string query, int topK, FullTextQueryMode mode, string? notQuery)
    {
        var parsedQuery = FullTextQueryParser.Parse(query, notQuery);
        if (!parsedQuery.HasPositiveClauses)
            return Array.Empty<(ulong Id, float Score)>();

        string[] candidateTerms = parsedQuery.EnumeratePositiveTerms().Distinct(StringComparer.Ordinal).ToArray();
        if (candidateTerms.Length == 0)
            return Array.Empty<(ulong Id, float Score)>();

        var documents = new List<(ulong Id, FullTextDocumentTerms Terms)>();
        var documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (id, payload) in _indexedPayloads)
        {
            if (!payload.TryGetValue(key, out var value) || value is not string text)
                continue;

            var document = FullTextQueryParser.BuildDocumentTerms(text);
            if (document.Length == 0)
                continue;

            documents.Add((id, document));

            foreach (string term in candidateTerms)
            {
                if (!document.TermFrequencies.ContainsKey(term))
                    continue;

                documentFrequencies.TryGetValue(term, out int count);
                documentFrequencies[term] = count + 1;
            }
        }

        if (documents.Count == 0)
            return Array.Empty<(ulong Id, float Score)>();

        double averageDocumentLength = Math.Max(1.0, documents.Average(static document => document.Terms.Length));
        var results = new List<(ulong Id, float Score)>();

        foreach (var document in documents)
        {
            if (!FullTextQueryParser.Matches(document.Terms, parsedQuery, mode))
                continue;

            float score = FullTextQueryParser.ComputeBm25Score(document.Terms, parsedQuery, documentFrequencies, documents.Count, averageDocumentLength);
            if (score <= 0)
                continue;

            results.Add((document.Id, score));
        }

        return results
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id)
            .Take(topK)
            .ToArray();
    }

    private static PayloadIndexEvaluation BuildGeoEvaluation(IEnumerable<long> ids)
    {
        var bitmap = new SimpleBitmap();
        foreach (long id in ids)
            bitmap.Set((ulong)id);

        return new PayloadIndexEvaluation(bitmap, true);
    }

    private void ClearLocked()
    {
        _invertedIndex.Clear();
        _rangeIndex.Clear();
        _geoIndex.Clear();
        _fullTextIndex.Clear();
        _persistentFullTextIndex.Clear();
        _persistentGeoIndex.Clear();
        _indexedPayloads.Clear();
        _allIds.ClearAll();
        _isFullyBuilt = false;

        if (_payloadStore is not null)
        {
            if (_matchPrefix is not null)
                DeleteStorePrefixLocked(_payloadStore, _matchPrefix);
            if (_rangePrefix is not null)
                DeleteStorePrefixLocked(_payloadStore, _rangePrefix);
            if (_fullTextPrefix is not null)
                DeleteStorePrefixLocked(_payloadStore, _fullTextPrefix);
        }

        if (_store is not null && _geoPrefix is not null)
        {
            foreach (var (key, _) in _store.ScanPrefix(_geoPrefix))
                _store.Delete(key);
        }

        if (_store is not null && _allIdsKey is not null)
            _store.Delete(_allIdsKey);
    }

    private void PersistAllIdsLocked()
    {
        if (_store is null || _allIdsKey is null || !_options.HasPersistentBackends)
            return;

        if (_allIds.Count == 0)
            _store.Delete(_allIdsKey);
        else
            _store.Upsert(_allIdsKey, _allIds.Serialize());
    }

    private static SimpleBitmap LoadAllIds(IKeyValueStore? store, byte[]? allIdsKey)
    {
        if (store is null || allIdsKey is null)
            return new SimpleBitmap();

        if (store.TryGet(allIdsKey, out var payload) && payload is not null)
            return SimpleBitmap.Deserialize(payload);

        return new SimpleBitmap();
    }

    private static void DeleteFullTextPrefixLocked(IKeyValueStore store, byte[] prefix)
    {
        var keys = store.ScanPrefix(prefix).Select(static pair => pair.Key).ToList();
        if (keys.Count == 0)
            return;

        using var tx = store.BeginTransaction();
        DeleteKeys(tx, keys);
        tx.Commit();
    }

    private void PersistBitmapLocked(byte[] key, SimpleBitmap bitmap)
    {
        if (_payloadStore is null)
            throw new InvalidOperationException("Persistent payload store is not configured.");

        _payloadStore.Upsert(key, bitmap.Serialize());
    }

    private void DeletePersistentKeyLocked(byte[] key)
    {
        if (_payloadStore is null)
            throw new InvalidOperationException("Persistent payload store is not configured.");

        _payloadStore.Delete(key);
    }

    private byte[] BuildPersistentMatchKey(string field, object value)
    {
        if (_matchPrefix is null)
            throw new InvalidOperationException("Persistent match prefix is not configured.");

        byte[] fieldPrefix = BuildFieldPrefix(_matchPrefix, field);
        return value switch
        {
            string text => BuildBinaryKey(fieldPrefix, 1, Encoding.UTF8.GetBytes(text)),
            long number => BuildBinaryKey(fieldPrefix, 2, EncodeInt64(number)),
            _ => throw new NotSupportedException($"Unsupported persistent match value type {value.GetType().Name}.")
        };
    }

    private byte[] BuildPersistentRangeFieldPrefix(string field)
    {
        if (_rangePrefix is null)
            throw new InvalidOperationException("Persistent range prefix is not configured.");

        return BuildFieldPrefix(_rangePrefix, field);
    }

    private byte[] BuildPersistentRangeKey(string field, long value)
    {
        return BuildBinaryKey(BuildPersistentRangeFieldPrefix(field), 1, EncodeInt64(value));
    }

    private static byte[] BuildFieldPrefix(byte[] prefix, string field)
    {
        byte[] fieldBytes = Encoding.UTF8.GetBytes(field);
        byte[] key = new byte[prefix.Length + sizeof(int) + fieldBytes.Length];
        prefix.CopyTo(key, 0);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(prefix.Length), fieldBytes.Length);
        fieldBytes.CopyTo(key, prefix.Length + sizeof(int));
        return key;
    }

    private static byte[] BuildBinaryKey(byte[] prefix, byte kind, byte[] valueBytes)
    {
        byte[] key = new byte[prefix.Length + 1 + valueBytes.Length];
        prefix.CopyTo(key, 0);
        key[prefix.Length] = kind;
        valueBytes.CopyTo(key, prefix.Length + 1);
        return key;
    }

    private static byte[] EncodeInt64(long value)
    {
        byte[] buffer = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        return buffer;
    }

    private static long ReadPersistentRangeValue(byte[] key)
    {
        if (key.Length < sizeof(long) + 1)
            throw new InvalidDataException("Persistent range key is too short.");

        return BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(key.Length - sizeof(long)));
    }

    private static void DeleteStorePrefixLocked(IKeyValueStore store, byte[] prefix)
    {
        DeleteFullTextPrefixLocked(store, prefix);
    }

    private static void DeleteKeys(IStorageTransaction tx, IEnumerable<byte[]> keys)
    {
        foreach (byte[] key in keys)
            tx.Delete(key);
    }

    private static bool IsMatchIndexable(object value)
    {
        return value is string or long;
    }

    private static Dictionary<string, object> NormalizePayload(Dictionary<string, object> payload)
    {
        var result = new Dictionary<string, object>(payload.Count, StringComparer.Ordinal);
        foreach (var (key, rawValue) in payload)
        {
            object value = UnwrapJsonElement(rawValue);
            if (value is bool b)
                value = b ? 1L : 0L;

            result[key] = value;
        }

        return result;
    }

    private static object UnwrapJsonElement(object value)
    {
        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => value
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

    private string BuildFieldKeyPrefix(string indexType, string field)
    {
        return $"pi:{_collectionName}:{indexType}:{field}";
    }

    private static string ResolveStoragePath(string collectionName, PayloadIndexOptions options)
    {
        string root = options.StoragePath ?? Path.Combine(Environment.CurrentDirectory, "payload-indexes");
        return Path.Combine(root, collectionName);
    }

    private static (double[] Min, double[] Max) BuildGeoBoundingBox(double lat, double lon, double radiusMeters)
    {
        const double EarthRadius = 6371000.0;
        double latDelta = radiusMeters / EarthRadius * (180.0 / Math.PI);
        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        double safeCosLat = Math.Abs(cosLat) < 1e-6 ? 1e-6 : cosLat;
        double lonDelta = radiusMeters / (EarthRadius * safeCosLat) * (180.0 / Math.PI);
        return (new[] { lat - latDelta, lon - lonDelta }, new[] { lat + latDelta, lon + lonDelta });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PayloadIndex));
    }
}
