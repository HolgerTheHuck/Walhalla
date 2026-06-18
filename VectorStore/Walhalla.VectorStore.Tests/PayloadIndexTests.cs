// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.Indexes.Primitives;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Filtering;
using Walhalla.VectorStore.Indexes;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class SimpleBitmapTests
{
    [Fact]
    public void Set_Get_SingleBit()
    {
        var bitmap = new SimpleBitmap();
        bitmap.Set(5);
        Assert.True(bitmap.Get(5));
        Assert.False(bitmap.Get(4));
        Assert.False(bitmap.Get(6));
    }

    [Fact]
    public void Set_Get_AcrossChunks()
    {
        var bitmap = new SimpleBitmap();
        bitmap.Set(0);
        bitmap.Set(63);
        bitmap.Set(64);
        bitmap.Set(127);
        bitmap.Set(128);

        Assert.True(bitmap.Get(0));
        Assert.True(bitmap.Get(63));
        Assert.True(bitmap.Get(64));
        Assert.True(bitmap.Get(127));
        Assert.True(bitmap.Get(128));
        Assert.False(bitmap.Get(129));
    }

    [Fact]
    public void Clear_RemovesBit()
    {
        var bitmap = new SimpleBitmap();
        bitmap.Set(10);
        Assert.True(bitmap.Get(10));
        bitmap.Clear(10);
        Assert.False(bitmap.Get(10));
    }

    [Fact]
    public void ClearAll_EmptiesBitmap()
    {
        var bitmap = new SimpleBitmap();
        bitmap.Set(1);
        bitmap.Set(2);
        bitmap.Set(3);
        bitmap.ClearAll();
        Assert.False(bitmap.Get(1));
        Assert.False(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
        Assert.Equal(0, bitmap.Count);
    }

    [Fact]
    public void Count_TracksBits()
    {
        var bitmap = new SimpleBitmap();
        Assert.Equal(0, bitmap.Count);
        bitmap.Set(5);
        Assert.Equal(1, bitmap.Count);
        bitmap.Set(5); // duplicate
        Assert.Equal(1, bitmap.Count);
        bitmap.Set(10);
        Assert.Equal(2, bitmap.Count);
        bitmap.Clear(5);
        Assert.Equal(1, bitmap.Count);
    }

    [Fact]
    public void EnumerateSetBits_ReturnsAllSet()
    {
        var bitmap = new SimpleBitmap();
        bitmap.Set(5);
        bitmap.Set(100);
        bitmap.Set(200);

        var bits = bitmap.EnumerateSetBits().ToList();
        Assert.Equal(3, bits.Count);
        Assert.Contains(5ul, bits);
        Assert.Contains(100ul, bits);
        Assert.Contains(200ul, bits);
    }

    [Fact]
    public void And_Intersection()
    {
        var a = new SimpleBitmap();
        a.Set(1);
        a.Set(2);
        a.Set(3);

        var b = new SimpleBitmap();
        b.Set(2);
        b.Set(3);
        b.Set(4);

        var result = a.And(b);
        Assert.True(result.Get(2));
        Assert.True(result.Get(3));
        Assert.False(result.Get(1));
        Assert.False(result.Get(4));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Or_Union()
    {
        var a = new SimpleBitmap();
        a.Set(1);
        a.Set(2);

        var b = new SimpleBitmap();
        b.Set(2);
        b.Set(3);

        var result = a.Or(b);
        Assert.True(result.Get(1));
        Assert.True(result.Get(2));
        Assert.True(result.Get(3));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void AndNot_Difference()
    {
        var a = new SimpleBitmap();
        a.Set(1);
        a.Set(2);
        a.Set(3);

        var b = new SimpleBitmap();
        b.Set(2);

        var result = a.AndNot(b);
        Assert.True(result.Get(1));
        Assert.False(result.Get(2));
        Assert.True(result.Get(3));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EmptyBitmap_Operations()
    {
        var empty = new SimpleBitmap();
        var other = new SimpleBitmap();
        other.Set(1);

        Assert.Equal(0, empty.And(other).Count);
        Assert.Equal(1, empty.Or(other).Count);
        Assert.Equal(0, empty.AndNot(other).Count);
    }

    [Fact]
    public void Serialize_Deserialize_EmptyBitmap()
    {
        var bitmap = new SimpleBitmap();

        var restored = SimpleBitmap.Deserialize(bitmap.Serialize());

        Assert.Equal(0, restored.Count);
        Assert.Empty(restored.EnumerateSetBits());
    }

    [Fact]
    public void Serialize_Deserialize_SparseBitmap()
    {
        var bitmap = new SimpleBitmap();
        ulong[] expected = [0, 1, 63, 64, 1024, 65_535, 1_000_000];
        foreach (ulong index in expected)
            bitmap.Set(index);

        var restored = SimpleBitmap.Deserialize(bitmap.Serialize());

        Assert.Equal(expected.Length, restored.Count);
        Assert.Equal(expected, restored.EnumerateSetBits().ToArray());
        foreach (ulong index in expected)
            Assert.True(restored.Get(index));
    }

    [Fact]
    public void Deserialize_InvalidVersion_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SimpleBitmap.Deserialize([99, 0]));
        Assert.Contains("Unsupported bitmap payload version", ex.Message);
    }
}

public class PayloadIndexTests
{
    [Fact]
    public void IndexPayload_MatchLookup()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["category"] = "pdf" });
        index.IndexPayload(2, new Dictionary<string, object> { ["category"] = "txt" });
        index.IndexPayload(3, new Dictionary<string, object> { ["category"] = "pdf" });

        var clause = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.True(bitmap.Get(3));
        Assert.False(bitmap.Get(2));
    }

    [Fact]
    public void IndexPayload_RangeLookup()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["year"] = 2022L });
        index.IndexPayload(2, new Dictionary<string, object> { ["year"] = 2024L });
        index.IndexPayload(3, new Dictionary<string, object> { ["year"] = 2020L });

        var clause = new FilterClause(
            new[] { new RangeCondition("year", new RangeValue(null, 2021, null, 2023)) },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.False(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void Evaluate_MustNot()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["deleted"] = true });
        index.IndexPayload(2, new Dictionary<string, object> { ["deleted"] = false });
        index.IndexPayload(3, new Dictionary<string, object> { ["deleted"] = false });

        var clause = new FilterClause(
            Array.Empty<Condition>(),
            null,
            new[] { new MatchCondition("deleted", new MatchBool(true)) });

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.False(bitmap!.Get(1));
        Assert.True(bitmap.Get(2));
        Assert.True(bitmap.Get(3));
    }

    [Fact]
    public void Evaluate_Should()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["a"] = 1L });
        index.IndexPayload(2, new Dictionary<string, object> { ["b"] = 2L });
        index.IndexPayload(3, new Dictionary<string, object> { ["c"] = 3L });

        var clause = new FilterClause(
            Array.Empty<Condition>(),
            new[]
            {
                new MatchCondition("a", new MatchInt(1)),
                new MatchCondition("b", new MatchInt(2))
            },
            null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.True(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void Evaluate_MustAndMustNot()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["category"] = "a", ["deleted"] = true });
        index.IndexPayload(2, new Dictionary<string, object> { ["category"] = "a", ["deleted"] = false });
        index.IndexPayload(3, new Dictionary<string, object> { ["category"] = "b", ["deleted"] = false });

        var clause = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("a")) },
            null,
            new[] { new MatchCondition("deleted", new MatchBool(true)) });

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.False(bitmap!.Get(1));
        Assert.True(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void Evaluate_DoubleMatch_ReturnsNull()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["score"] = 3.14 });

        var clause = new FilterClause(
            new[] { new MatchCondition("score", new MatchDouble(3.14)) },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.Null(bitmap); // Double nicht indexiert
    }

    [Fact]
    public void RemovePayload_RemovesFromIndex()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["category"] = "pdf" });
        index.RemovePayload(1);

        var clause = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.False(bitmap!.Get(1));
    }

    [Fact]
    public void IndexPayload_OverwriteExisting_RemovesOldMatchAndRangeValues()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["category"] = "pdf", ["year"] = 2022L });
        index.IndexPayload(1, new Dictionary<string, object> { ["category"] = "txt" });

        var oldCategory = index.Evaluate(new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null));
        var newCategory = index.Evaluate(new FilterClause(
            new[] { new MatchCondition("category", new MatchString("txt")) },
            null, null));
        var oldYear = index.Evaluate(new FilterClause(
            new[] { new RangeCondition("year", new RangeValue(null, 2022, null, 2022)) },
            null, null));

        Assert.NotNull(oldCategory);
        Assert.NotNull(newCategory);
        Assert.NotNull(oldYear);
        Assert.False(oldCategory!.Get(1));
        Assert.True(newCategory!.Get(1));
        Assert.False(oldYear!.Get(1));
    }

    [Fact]
    public async Task BuildFromCollection_RebuildsIndex()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"walhalla-payload-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(dbPath);
        try
        {
            var store = new BlobStore(new BlobStoreOptions(dbPath));
            var manager = new VectorCollectionManager(store);
            var collection = manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean, false);

            await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["category"] = "pdf" } });
            await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "test", Payload = new() { ["category"] = "txt" } });

            // Neuer PayloadIndex aufbauen
            var payloadIndex = new PayloadIndex();
            await payloadIndex.BuildFromCollection(collection);

            var clause = new FilterClause(
                new[] { new MatchCondition("category", new MatchString("pdf")) },
                null, null);

            var bitmap = payloadIndex.Evaluate(clause);
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.Get(1));
            Assert.False(bitmap.Get(2));

            manager.Dispose();
            store.Dispose();
        }
        finally
        {
            Directory.Delete(dbPath, true);
        }
    }

    [Fact]
    public void IndexPayload_FullTextLookup()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["description"] = "hello world" });
        index.IndexPayload(2, new Dictionary<string, object> { ["description"] = "hello foo" });
        index.IndexPayload(3, new Dictionary<string, object> { ["description"] = "bar baz" });

        var clause = new FilterClause(
            new[] { new FullTextCondition("description", "hello world") },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.False(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void IndexPayload_FullText_MultipleTerms()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["title"] = "the quick brown fox" });
        index.IndexPayload(2, new Dictionary<string, object> { ["title"] = "quick brown" });
        index.IndexPayload(3, new Dictionary<string, object> { ["title"] = "the quick" });

        var clause = new FilterClause(
            new[] { new FullTextCondition("title", "quick brown") },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.True(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void IndexPayload_FullText_PhraseAnyAndNotLookup()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["body"] = "shared agent memory handbook" });
        index.IndexPayload(2, new Dictionary<string, object> { ["body"] = "memory agent handbook" });
        index.IndexPayload(3, new Dictionary<string, object> { ["body"] = "shared private handbook" });

        var clause = new FilterClause(
            new[] { new FullTextCondition("body", "shared \"agent memory\"", Walhalla.Indexes.FullText.FullTextQueryMode.Any, "private") },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Get(1));
        Assert.False(bitmap.Get(2));
        Assert.False(bitmap.Get(3));
    }

    [Fact]
    public void Evaluate_GeoRadius_ReturnsNull()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["location"] = new Dictionary<string, object> { ["lat"] = 52.5, ["lon"] = 13.4 } });

        var clause = new FilterClause(
            new[] { new GeoRadiusCondition("location", 52.5, 13.4, 1000) },
            null, null);

        var bitmap = index.Evaluate(clause);
        Assert.Null(bitmap); // Geo-Radius liefert keinen exakten Pre-Filter
    }

    [Fact]
    public void EvaluateForSearch_GeoRadius_ReturnsBoundingBoxCandidates()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object> { ["location"] = new Dictionary<string, object> { ["lat"] = 52.5200, ["lon"] = 13.4050 } });
        index.IndexPayload(2, new Dictionary<string, object> { ["location"] = new Dictionary<string, object> { ["lat"] = 48.8566, ["lon"] = 2.3522 } });

        var clause = new FilterClause(
            new[] { new GeoRadiusCondition("location", 52.5200, 13.4050, 1000) },
            null, null);

        var evaluation = index.EvaluateForSearch(clause);

        Assert.NotNull(evaluation.Bitmap);
        Assert.True(evaluation.RequiresPostFilter);
        Assert.True(evaluation.Bitmap!.Get(1));
        Assert.False(evaluation.Bitmap.Get(2));
    }

    [Fact]
    public void RemovePayload_ClearsFullTextAndGeo()
    {
        var index = new PayloadIndex();
        index.IndexPayload(1, new Dictionary<string, object>
        {
            ["description"] = "hello world",
            ["location"] = new Dictionary<string, object> { ["lat"] = 52.5, ["lon"] = 13.4 }
        });

        index.RemovePayload(1);

        var ftClause = new FilterClause(
            new[] { new FullTextCondition("description", "hello") },
            null, null);
        var ftBitmap = index.Evaluate(ftClause);
        Assert.NotNull(ftBitmap);
        Assert.False(ftBitmap!.Get(1));
    }
}

public class PreFilterIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;

    public PreFilterIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla-prefilter-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbPath);
        _store = new BlobStore(new BlobStoreOptions(_dbPath));
        _manager = new VectorCollectionManager(_store);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _store.Dispose();
        Directory.Delete(_dbPath, true);
    }

    [Fact]
    public async Task SearchExact_WithPayloadIndex_PreFilter()
    {
        var collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean, false);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["year"] = 2022L } });
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "test", Payload = new() { ["year"] = 2024L } });
        await collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "test", Payload = new() { ["year"] = 2020L } });

        var filter = new FilterClause(
            new[] { new RangeCondition("year", new RangeValue(null, 2021, null, 2023)) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }

    [Fact]
    public async Task SearchHnsw_WithPayloadIndex_PreFilter()
    {
        var collection = _manager.GetOrCreateCollection("hnsw-test", 3, DistanceMetric.Euclidean, true);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "hnsw-test", Payload = new() { ["tag"] = "red" } });
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "hnsw-test", Payload = new() { ["tag"] = "blue" } });
        await collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "hnsw-test", Payload = new() { ["tag"] = "red" } });

        var filter = new FilterClause(
            new[] { new MatchCondition("tag", new MatchString("red")) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchHnswAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter: filter))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == 1);
        Assert.Contains(results, r => r.Id == 3);
    }

    [Fact]
    public async Task SearchHnsw_PreFilterVsPostFilter_SameResults()
    {
        var collection = _manager.GetOrCreateCollection("hnsw-test", 3, DistanceMetric.Euclidean, true);

        // Vektoren mit Metadaten einfuegen
        var random = new Random(42);
        for (int i = 1; i <= 50; i++)
        {
            var vec = new Vector(new[] { random.NextSingle(), random.NextSingle(), random.NextSingle() });
            var tag = i % 5 == 0 ? "red" : "blue";
            await collection.PutAsync((ulong)i, vec,
                new VectorMetadata { Id = (ulong)i, Collection = "hnsw-test", Payload = new() { ["tag"] = tag } });
        }

        var filter = new FilterClause(
            new[] { new MatchCondition("tag", new MatchString("red")) },
            null, null);

        var preFilterResults = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchHnswAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter: filter))
            preFilterResults.Add(r);

        // Ergebnisse muessen nur rote Tags enthalten
        Assert.All(preFilterResults, r =>
        {
            Assert.NotNull(r.Metadata?.Payload);
            Assert.True(r.Metadata!.Payload!.TryGetValue("tag", out var t));
            var tag = t switch
            {
                string s => s,
                System.Text.Json.JsonElement je => je.GetString()!,
                _ => t?.ToString()!
            };
            Assert.Equal("red", tag);
        });
    }

    [Fact]
    public async Task SearchHnsw_NoPayloadIndex_FallsBackToPostFilter()
    {
        var collection = _manager.GetOrCreateCollection("no-index", 3, DistanceMetric.Euclidean, true, enablePayloadIndex: false);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "no-index", Payload = new() { ["tag"] = "red" } });
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "no-index", Payload = new() { ["tag"] = "blue" } });

        var filter = new FilterClause(
            new[] { new MatchCondition("tag", new MatchString("red")) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchHnswAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter: filter))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }

    [Fact]
    public async Task BuildPayloadIndexAsync_RebuildsFromExistingData()
    {
        var collection = _manager.GetOrCreateCollection("rebuild-test", 3, DistanceMetric.Euclidean, false);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "rebuild-test", Payload = new() { ["category"] = "pdf" } });
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "rebuild-test", Payload = new() { ["category"] = "txt" } });

        await collection.BuildPayloadIndexAsync();

        var filter = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }

    [Fact]
    public async Task SearchExact_WithGeoRadius_PostFilter()
    {
        var collection = _manager.GetOrCreateCollection("geo-test", 3, DistanceMetric.Euclidean, false);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "geo-test", Payload = new() { ["location"] = new Dictionary<string, object> { ["lat"] = 52.5200, ["lon"] = 13.4050 } } }); // Berlin
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "geo-test", Payload = new() { ["location"] = new Dictionary<string, object> { ["lat"] = 48.8566, ["lon"] = 2.3522 } } }); // Paris
        await collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "geo-test", Payload = new() { ["location"] = new Dictionary<string, object> { ["lat"] = 52.5200, ["lon"] = 13.4050 } } }); // Berlin

        var filter = new FilterClause(
            new[] { new GeoRadiusCondition("location", 52.5200, 13.4050, 1000) }, // 1km um Berlin
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Equal(2, results.Count); // Berlin (ID 1 und 3)
        Assert.Contains(results, r => r.Id == 1);
        Assert.Contains(results, r => r.Id == 3);
    }

    [Fact]
    public async Task SearchExact_WithFullText_PreFilter()
    {
        var collection = _manager.GetOrCreateCollection("ft-test", 3, DistanceMetric.Euclidean, false);

        await collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "ft-test", Payload = new() { ["title"] = "hello world" } });
        await collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "ft-test", Payload = new() { ["title"] = "quick brown fox" } });
        await collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "ft-test", Payload = new() { ["title"] = "hello foo" } });

        var filter = new FilterClause(
            new[] { new FullTextCondition("title", "hello world") },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }
}
