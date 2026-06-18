// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.VectorStore.Collections;

namespace Walhalla.VectorStore.Microsoft.Extensions.VectorData;

/// <summary>
/// VectorData-kompatible Collection-Implementierung auf Basis von Walhalla.VectorStore.
/// </summary>
public sealed class WalhallaVectorStoreCollection<TKey, TRecord> : global::Microsoft.Extensions.VectorData.VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly EmbeddedVectorStore _store;
    private readonly VectorCollection _collection;
    private readonly WalhallaRecordMapper<TRecord> _mapper;
    private readonly WalhallaVectorStoreOptions _options;

    public WalhallaVectorStoreCollection(
        EmbeddedVectorStore store,
        string name,
        global::Microsoft.Extensions.VectorData.VectorStoreCollectionDefinition? definition,
        WalhallaVectorStoreOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _mapper = new WalhallaRecordMapper<TRecord>(definition);

        var vectorProp = definition?.Properties.OfType<global::Microsoft.Extensions.VectorData.VectorStoreVectorProperty>().FirstOrDefault();
        int dimension = vectorProp?.Dimensions ?? _mapper.Dimensions;
        if (dimension <= 0)
            throw new ArgumentException("Vector dimension must be specified in VectorStoreCollectionDefinition or via VectorStoreVectorAttribute.");

        var metric = ParseDistanceFunction(vectorProp?.DistanceFunction);
        _collection = store.GetOrCreateCollection(name, dimension, metric, options.EnableHnswByDefault);
    }

    public override string Name { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_collection is not null);

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        _store.DeleteCollection(Name);
        return Task.CompletedTask;
    }

    public override async Task<TRecord?> GetAsync(TKey key, global::Microsoft.Extensions.VectorData.RecordRetrievalOptions? options, CancellationToken cancellationToken = default)
    {
        var id = ConvertKeyToULong(key);
        var entry = await _collection.GetAsync(id, cancellationToken);
        if (entry is null) return default;
        return _mapper.FromEntry(entry, options?.IncludeVectors ?? true);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(
        IEnumerable<TKey> keys,
        global::Microsoft.Extensions.VectorData.RecordRetrievalOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            var record = await GetAsync(key, options, cancellationToken);
            if (record is not null) yield return record;
        }
    }

    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var id = ConvertKeyToULong(key);
        return _collection.DeleteAsync(id, cancellationToken);
    }

    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            await DeleteAsync(key, cancellationToken);
        }
    }

    public override async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        var (id, vector, metadata) = _mapper.ToWalhalla(record);
        await _collection.PutAsync(id, vector, metadata, cancellationToken);
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        var items = records.Select(r =>
        {
            var (id, vector, metadata) = _mapper.ToWalhalla(r);
            return (Id: id, Vector: vector, Metadata: metadata);
        }).ToList();

        if (items.Count == 0) return;

        await _collection.PutBatchAsync(
            items.Select(x => (x.Id, x.Vector, x.Metadata)),
            ct: cancellationToken);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(
        Expression<Func<TRecord, bool>> filter,
        int top,
        global::Microsoft.Extensions.VectorData.FilteredRecordRetrievalOptions<TRecord>? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var compiledFilter = filter.Compile();
        var results = new List<TRecord>();

        await foreach (var id in _collection.EnumerateIdsAsync(cancellationToken))
        {
            var entry = await _collection.GetAsync(id, cancellationToken);
            if (entry is null) continue;

            var record = _mapper.FromEntry(entry, options?.IncludeVectors ?? true);
            if (compiledFilter(record))
                results.Add(record);

            if (results.Count >= top)
                break;
        }

        if (options?.OrderBy is not null)
        {
            var filteredOptionsType = typeof(global::Microsoft.Extensions.VectorData.FilteredRecordRetrievalOptions<>).MakeGenericType(typeof(TRecord));
            var orderByDefType = filteredOptionsType.GetNestedType("OrderByDefinition")!;
            var emptyDef = Activator.CreateInstance(orderByDefType)!;

            var orderByProp = options.GetType().GetProperty("OrderBy")!;
            var orderByDelegate = (Delegate)orderByProp.GetValue(options)!;
            var filledDef = orderByDelegate.DynamicInvoke(emptyDef)!;

            var valuesProp = orderByDefType.GetProperty("Values")!;
            var values = (System.Collections.IEnumerable)valuesProp.GetValue(filledDef)!;

            IOrderedEnumerable<TRecord>? orderedResult = null;
            bool first = true;
            foreach (var value in values)
            {
                var valueType = value.GetType();
                var selectorExpr = (System.Linq.Expressions.LambdaExpression)valueType.GetProperty("PropertySelector")!.GetValue(value)!;
                var ascending = (bool)valueType.GetProperty("Ascending")!.GetValue(value)!;
                var compiled = selectorExpr.Compile();

                if (first)
                {
                    orderedResult = ascending
                        ? results.OrderBy(r => compiled.DynamicInvoke(r)!)
                        : results.OrderByDescending(r => compiled.DynamicInvoke(r)!);
                    first = false;
                }
                else
                {
                    orderedResult = ascending
                        ? orderedResult!.ThenBy(r => compiled.DynamicInvoke(r)!)
                        : orderedResult!.ThenByDescending(r => compiled.DynamicInvoke(r)!);
                }
            }

            if (orderedResult is not null)
                results = orderedResult.ToList();
        }

        if (options?.Skip > 0)
            results = results.Skip(options.Skip).ToList();

        foreach (var record in results.Take(top))
            yield return record;
    }

    public override async IAsyncEnumerable<global::Microsoft.Extensions.VectorData.VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        global::Microsoft.Extensions.VectorData.VectorSearchOptions<TRecord>? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queryVector = ConvertSearchValueToVector(searchValue);

        var results = _collection.Index is not null
            ? _collection.SearchHnswAsync(queryVector, top, _options.DefaultEfSearch, ct: cancellationToken)
            : _collection.SearchExactAsync(queryVector, top, ct: cancellationToken);

        var filter = options?.Filter?.Compile();
        int skip = options?.Skip ?? 0;
        int yielded = 0;
        int skipped = 0;

        await foreach (var result in results.WithCancellation(cancellationToken))
        {
            var entry = await _collection.GetAsync(result.Id, cancellationToken);
            if (entry is null) continue;

            var record = _mapper.FromEntry(entry, options?.IncludeVectors ?? false);

            if (filter is not null && !filter(record))
                continue;

            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            yield return new global::Microsoft.Extensions.VectorData.VectorSearchResult<TRecord>(record, result.Score);

            if (++yielded >= top) break;
        }
    }

    public override object GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(VectorCollection)) return _collection;
        if (serviceType == typeof(EmbeddedVectorStore)) return _store;
        throw new NotSupportedException($"Service {serviceType} is not supported by WalhallaVectorStoreCollection.");
    }

    private static ulong ConvertKeyToULong(TKey key)
    {
        return key switch
        {
            ulong ul => ul,
            string s when ulong.TryParse(s, out var parsed) => parsed,
            Guid g => BitConverter.ToUInt64(g.ToByteArray(), 0),
            int i => (ulong)i,
            long l => (ulong)l,
            uint ui => ui,
            _ => throw new NotSupportedException(
                $"Key type {typeof(TKey)} is not supported by Walhalla VectorStore. Use ulong, string (parseable), Guid, int, long or uint.")
        };
    }

    private static Vector ConvertSearchValueToVector<TInput>(TInput searchValue)
    {
        float[] data = searchValue switch
        {
            float[] arr => arr,
            ReadOnlyMemory<float> rom => rom.Span.ToArray(),
            Memory<float> mem => mem.Span.ToArray(),
            _ => throw new NotSupportedException(
                $"Search value type {typeof(TInput)} is not supported. Use float[] or ReadOnlyMemory<float>.")
        };
        return new Vector(data);
    }

    private static DistanceMetric ParseDistanceFunction(string? distanceFunction)
    {
        return distanceFunction?.ToLowerInvariant() switch
        {
            "euclidean" or "l2" or "l2distance" => DistanceMetric.Euclidean,
            "cosine" or "cosinesimilarity" => DistanceMetric.Cosine,
            "dotproduct" => DistanceMetric.DotProduct,
            _ => DistanceMetric.Cosine
        };
    }
}
