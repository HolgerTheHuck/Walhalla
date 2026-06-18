// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Walhalla.Indexes.FullText;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Filtering;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class FilterParserTests
{
    [Fact]
    public void Parse_Must_MatchString()
    {
        var json = @"{""must"": [{""key"": ""category"", ""match"": {""value"": ""pdf""}}]}";
        var clause = FilterParser.Parse(json);

        Assert.Single(clause.Must);
        var condition = Assert.IsType<MatchCondition>(clause.Must[0]);
        Assert.Equal("category", condition.Key);
        var match = Assert.IsType<MatchString>(condition.Match);
        Assert.Equal("pdf", match.Value);
    }

    [Fact]
    public void Parse_Must_MatchInt()
    {
        var json = @"{""must"": [{""key"": ""year"", ""match"": {""value"": 2024}}]}";
        var clause = FilterParser.Parse(json);

        var condition = Assert.IsType<MatchCondition>(clause.Must[0]);
        var match = Assert.IsType<MatchInt>(condition.Match);
        Assert.Equal(2024, match.Value);
    }

    [Fact]
    public void Parse_Must_MatchBool()
    {
        var json = @"{""must"": [{""key"": ""deleted"", ""match"": {""value"": true}}]}";
        var clause = FilterParser.Parse(json);

        var condition = Assert.IsType<MatchCondition>(clause.Must[0]);
        var match = Assert.IsType<MatchBool>(condition.Match);
        Assert.True(match.Value);
    }

    [Fact]
    public void Parse_Must_MatchDouble()
    {
        var json = @"{""must"": [{""key"": ""score"", ""match"": {""value"": 3.14}}]}";
        var clause = FilterParser.Parse(json);

        var condition = Assert.IsType<MatchCondition>(clause.Must[0]);
        var match = Assert.IsType<MatchDouble>(condition.Match);
        Assert.Equal(3.14, match.Value);
    }

    [Fact]
    public void Parse_Must_Range()
    {
        var json = @"{""must"": [{""key"": ""year"", ""range"": {""gte"": 2020, ""lte"": 2024}}]}";
        var clause = FilterParser.Parse(json);

        var condition = Assert.IsType<RangeCondition>(clause.Must[0]);
        Assert.Equal("year", condition.Key);
        Assert.Equal(2020, condition.Range.Gte);
        Assert.Equal(2024, condition.Range.Lte);
    }

    [Fact]
    public void Parse_MustNot_And_Should()
    {
        var json = @"{""must"": [{""key"": ""a"", ""match"": {""value"": 1}}], ""should"": [{""key"": ""b"", ""match"": {""value"": 2}}], ""must_not"": [{""key"": ""c"", ""match"": {""value"": 3}}]}";
        var clause = FilterParser.Parse(json);

        Assert.Single(clause.Must);
        Assert.NotNull(clause.Should);
        Assert.Single(clause.Should!);
        Assert.NotNull(clause.MustNot);
        Assert.Single(clause.MustNot!);
    }

    [Fact]
    public void Parse_EmptyFilter_Throws()
    {
        var json = @"{}";
        Assert.Throws<FilterParseException>(() => FilterParser.Parse(json));
    }

    [Fact]
    public void Parse_MissingKey_Throws()
    {
        var json = @"{""must"": [{""match"": {""value"": 1}}]}";
        Assert.Throws<FilterParseException>(() => FilterParser.Parse(json));
    }

    [Fact]
    public void Parse_MatchAndRange_Throws()
    {
        var json = @"{""must"": [{""key"": ""x"", ""match"": {""value"": 1}, ""range"": {""gte"": 0}}]}";
        Assert.Throws<FilterParseException>(() => FilterParser.Parse(json));
    }

    [Fact]
    public void Parse_FromDictionary()
    {
        var dict = new Dictionary<string, JsonElement>
        {
            ["must"] = JsonDocument.Parse(@"[{""key"": ""cat"", ""match"": {""value"": ""x""}}]").RootElement
        };
        var clause = FilterParser.Parse(dict);
        Assert.Single(clause.Must);
    }

    [Fact]
    public void Parse_FullText_ModeAndNot()
    {
        var json = @"{""must"": [{""key"": ""body"", ""full_text"": {""query"": ""shared \""agent memory\"""", ""mode"": ""any"", ""not"": ""private""}}]}";
        var clause = FilterParser.Parse(json);

        var condition = Assert.IsType<FullTextCondition>(clause.Must[0]);
        Assert.Equal("body", condition.Key);
        Assert.Equal("shared \"agent memory\"", condition.Query);
        Assert.Equal(FullTextQueryMode.Any, condition.Mode);
        Assert.Equal("private", condition.NotQuery);
    }
}

public class FilterEvaluatorTests
{
    [Fact]
    public void Must_MatchString_True()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null);

        var payload = new Dictionary<string, object> { ["category"] = "pdf" };
        Assert.True(FilterEvaluator.Evaluate(clause, payload));
    }

    [Fact]
    public void Must_MatchString_False()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("pdf")) },
            null, null);

        var payload = new Dictionary<string, object> { ["category"] = "txt" };
        Assert.False(FilterEvaluator.Evaluate(clause, payload));
    }

    [Fact]
    public void Must_MatchInt_True()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("year", new MatchInt(2024)) },
            null, null);

        var payload = new Dictionary<string, object> { ["year"] = 2024L };
        Assert.True(FilterEvaluator.Evaluate(clause, payload));
    }

    [Fact]
    public void Must_Range_Inclusive()
    {
        var clause = new FilterClause(
            new[] { new RangeCondition("year", new RangeValue(null, 2020, null, 2024)) },
            null, null);

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["year"] = 2020L }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["year"] = 2024L }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["year"] = 2022L }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["year"] = 2019L }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["year"] = 2025L }));
    }

    [Fact]
    public void Must_Range_Exclusive()
    {
        var clause = new FilterClause(
            new[] { new RangeCondition("score", new RangeValue(0, null, 100, null)) },
            null, null);

        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 0L }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 50L }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 100L }));
    }

    [Fact]
    public void MustNot_BlocksMatch()
    {
        var clause = new FilterClause(
            Array.Empty<Condition>(),
            null,
            new[] { new MatchCondition("deleted", new MatchBool(true)) });

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["deleted"] = false }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["deleted"] = true }));
    }

    [Fact]
    public void Should_OnlyRelevantWhenMustEmpty()
    {
        var clause = new FilterClause(
            Array.Empty<Condition>(),
            new[] { new MatchCondition("a", new MatchInt(1)), new MatchCondition("b", new MatchInt(2)) },
            null);

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["a"] = 1L }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["b"] = 2L }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["c"] = 3L }));
    }

    [Fact]
    public void Should_IgnoredWhenMustPresent()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("a", new MatchInt(1)) },
            new[] { new MatchCondition("b", new MatchInt(99)) },
            null);

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["a"] = 1L }));
    }

    [Fact]
    public void TypeMismatch_ReturnsFalse()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("x", new MatchInt(1)) },
            null, null);

        // string statt int
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["x"] = "1" }));
    }

    [Fact]
    public void MissingKey_ReturnsFalse()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("x", new MatchInt(1)) },
            null, null);

        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["y"] = 1L }));
    }

    [Fact]
    public void NullPayload_EmptyFilter_ReturnsTrue()
    {
        var clause = new FilterClause(Array.Empty<Condition>(), null, null);
        Assert.True(FilterEvaluator.Evaluate(clause, null));
    }

    [Fact]
    public void NullPayload_NonEmptyFilter_ReturnsFalse()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("x", new MatchInt(1)) },
            null, null);
        Assert.False(FilterEvaluator.Evaluate(clause, null));
    }

    [Fact]
    public void MatchDouble_Equality()
    {
        var clause = new FilterClause(
            new[] { new MatchCondition("score", new MatchDouble(3.14)) },
            null, null);

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 3.14 }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 3.14f }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["score"] = 3.15 }));
    }

    [Fact]
    public void FullText_Phrase_Any_And_Not()
    {
        var clause = new FilterClause(
            new[] { new FullTextCondition("body", "shared \"agent memory\"", FullTextQueryMode.Any, "private") },
            null,
            null);

        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["body"] = "shared agent memory handbook" }));
        Assert.True(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["body"] = "shared notes only" }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["body"] = "memory agent handbook" }));
        Assert.False(FilterEvaluator.Evaluate(clause, new Dictionary<string, object> { ["body"] = "shared private notes" }));
    }
}

public class VectorCollectionFilterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlobStore _store;
    private readonly VectorCollectionManager _manager;
    private readonly VectorCollection _collection;

    public VectorCollectionFilterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla-filter-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbPath);
        _store = new BlobStore(new BlobStoreOptions(_dbPath));
        _manager = new VectorCollectionManager(_store);
        _collection = _manager.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean, false);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _store.Dispose();
        Directory.Delete(_dbPath, true);
    }

    [Fact]
    public async Task SearchExact_WithFilter_Match()
    {
        await _collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["category"] = "a" } });
        await _collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "test", Payload = new() { ["category"] = "b" } });
        await _collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "test", Payload = new() { ["category"] = "a" } });

        var filter = new FilterClause(
            new[] { new MatchCondition("category", new MatchString("a")) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in _collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == 1);
        Assert.Contains(results, r => r.Id == 3);
    }

    [Fact]
    public async Task SearchExact_WithFilter_Range()
    {
        await _collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["year"] = 2022L } });
        await _collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "test", Payload = new() { ["year"] = 2024L } });
        await _collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "test", Payload = new() { ["year"] = 2020L } });

        var filter = new FilterClause(
            new[] { new RangeCondition("year", new RangeValue(null, 2021, null, 2023)) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in _collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal(1ul, results[0].Id);
    }

    [Fact]
    public async Task SearchExact_WithoutFilter_ReturnsAll()
    {
        await _collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["category"] = "a" } });
        await _collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "test", Payload = new() { ["category"] = "b" } });

        var results = new List<VectorSearchResult>();
        await foreach (var r in _collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10))
            results.Add(r);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchHnsw_WithFilter()
    {
        var hnswCollection = _manager.GetOrCreateCollection("hnsw-test", 3, DistanceMetric.Euclidean, true);

        await hnswCollection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "hnsw-test", Payload = new() { ["tag"] = "red" } });
        await hnswCollection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "hnsw-test", Payload = new() { ["tag"] = "blue" } });
        await hnswCollection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
            new VectorMetadata { Id = 3, Collection = "hnsw-test", Payload = new() { ["tag"] = "red" } });

        var filter = new FilterClause(
            new[] { new MatchCondition("tag", new MatchString("red")) },
            null, null);

        var results = new List<VectorSearchResult>();
        await foreach (var r in hnswCollection.SearchHnswAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter: filter))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == 1);
        Assert.Contains(results, r => r.Id == 3);
    }

    [Fact]
    public async Task SearchHnsw_WithoutFilter_ReturnsAll()
    {
        var hnswCollection = _manager.GetOrCreateCollection("hnsw-test2", 3, DistanceMetric.Euclidean, true);

        await hnswCollection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
            new VectorMetadata { Id = 1, Collection = "hnsw-test2", Payload = new() { ["tag"] = "red" } });
        await hnswCollection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
            new VectorMetadata { Id = 2, Collection = "hnsw-test2", Payload = new() { ["tag"] = "blue" } });

        var results = new List<VectorSearchResult>();
        await foreach (var r in hnswCollection.SearchHnswAsync(new Vector(new[] { 1f, 0f, 0f }), 10))
            results.Add(r);

        Assert.Equal(2, results.Count);
    }
}
