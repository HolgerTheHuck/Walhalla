// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.Indexes.FullText;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Filtering;
using Walhalla.VectorStore.Indexes;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class PersistentPayloadIndexIntegrationTests
{
    [Fact]
    public async Task SearchExact_Reopen_UsesPersistentMatchAndRangeWithoutRebuild()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentMatch = true, PersistentRange = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "persisted-scalars", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-scalars", Payload = new() { ["category"] = "pdf", ["year"] = 2022L } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "persisted-scalars", Payload = new() { ["category"] = "txt", ["year"] = 2024L } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-scalars", options);
            var matchFilter = new FilterClause(
                new[] { new MatchCondition("category", new MatchString("pdf")) },
                null, null);
            var rangeFilter = new FilterClause(
                new[] { new RangeCondition("year", new RangeValue(null, 2021, null, 2023)) },
                null, null);

            var matchResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, matchFilter));
            var rangeResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, rangeFilter));

            Assert.Single(matchResults);
            Assert.Equal(1ul, matchResults[0].Id);
            Assert.Single(rangeResults);
            Assert.Equal(1ul, rangeResults[0].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task SearchExact_Reopen_UsesPersistentFullTextAndGeoWithoutRebuild()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true, PersistentGeo = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "persisted-search", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-search", Payload = new() { ["title"] = "hello world", ["location"] = Geo(52.5200, 13.4050) } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "persisted-search", Payload = new() { ["title"] = "hello paris", ["location"] = Geo(48.8566, 2.3522) } });
            await setup.Collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
                new VectorMetadata { Id = 3, Collection = "persisted-search", Payload = new() { ["title"] = "quick brown", ["location"] = Geo(52.5200, 13.4050) } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-search", options);

            var fullTextFilter = new FilterClause(
                new[] { new FullTextCondition("title", "hello world") },
                null, null);
            var geoFilter = new FilterClause(
                new[] { new GeoRadiusCondition("location", 52.5200, 13.4050, 1000) },
                null, null);

            var fullTextResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, fullTextFilter));
            var geoResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, geoFilter));

            Assert.Single(fullTextResults);
            Assert.Equal(1ul, fullTextResults[0].Id);
            Assert.Equal(2, geoResults.Count);
            Assert.Contains(geoResults, result => result.Id == 1);
            Assert.Contains(geoResults, result => result.Id == 3);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task SearchTextAsync_Reopen_UsesPersistentFullTextRankingWithoutRebuild()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "persisted-text", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-text", Payload = new() { ["body"] = "agent memory agent memory" } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "persisted-text", Payload = new() { ["body"] = "agent memory" } });
            await setup.Collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
                new VectorMetadata { Id = 3, Collection = "persisted-text", Payload = new() { ["body"] = "agent only" } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-text", options);
            var results = await CollectAsync(reopened.Collection.SearchTextAsync("body", "agent memory", topK: 10));

            Assert.Equal(2, results.Count);
            Assert.Equal(1ul, results[0].Id);
            Assert.Equal(2ul, results[1].Id);
            Assert.True(results[0].Score > results[1].Score);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task SearchTextAsync_Reopen_SupportsPhraseAnyAndNot()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "persisted-text-semantics", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-text-semantics", Payload = new() { ["body"] = "shared agent memory handbook" } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "persisted-text-semantics", Payload = new() { ["body"] = "shared notes only" } });
            await setup.Collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
                new VectorMetadata { Id = 3, Collection = "persisted-text-semantics", Payload = new() { ["body"] = "shared private notebook" } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-text-semantics", options);
            var results = await CollectAsync(reopened.Collection.SearchTextAsync("body", "shared \"agent memory\"", 10, FullTextQueryMode.Any, "private"));

            Assert.Equal(2, results.Count);
            Assert.Equal(1ul, results[0].Id);
            Assert.Equal(2ul, results[1].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task SearchHybridAsync_ReranksTextCandidatesByVectorDistance()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "hybrid", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "hybrid", Payload = new() { ["body"] = "agent memory handbook" } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "hybrid", Payload = new() { ["body"] = "agent memory memo" } });
            await setup.Collection.PutAsync(3, new Vector(new[] { 0f, 0f, 1f }),
                new VectorMetadata { Id = 3, Collection = "hybrid", Payload = new() { ["body"] = "gardening notes" } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "hybrid", options);
            var results = await CollectAsync(reopened.Collection.SearchHybridAsync("body", "agent memory", new Vector(new[] { 0f, 1f, 0f }), topK: 2));

            Assert.Equal(2, results.Count);
            Assert.Equal(2ul, results[0].Id);
            Assert.Equal(1ul, results[1].Id);
            Assert.True(results[0].Score < results[1].Score);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task DeleteAfterReopen_RemovesPersistentFullTextAndGeo()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true, PersistentGeo = true };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "persisted-delete", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-delete", Payload = new() { ["title"] = "hello world", ["location"] = Geo(52.5200, 13.4050) } });
            await setup.Collection.PutAsync(2, new Vector(new[] { 0f, 1f, 0f }),
                new VectorMetadata { Id = 2, Collection = "persisted-delete", Payload = new() { ["title"] = "other text", ["location"] = Geo(52.5200, 13.4050) } });
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-delete", options);
            await reopened.Collection.DeleteAsync(1);
            await reopened.DisposeAsync();

            await using var verified = await CreateCollectionAsync(path, "persisted-delete", options);
            var fullTextFilter = new FilterClause(
                new[] { new FullTextCondition("title", "hello world") },
                null, null);
            var geoFilter = new FilterClause(
                new[] { new GeoRadiusCondition("location", 52.5200, 13.4050, 1000) },
                null, null);

            var fullTextResults = await CollectAsync(verified.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, fullTextFilter));
            var geoResults = await CollectAsync(verified.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, geoFilter));

            Assert.Empty(fullTextResults);
            Assert.Single(geoResults);
            Assert.Equal(2ul, geoResults[0].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task BuildPayloadIndexAsync_PersistentCollection_ClearsStaleFullTextAndGeoState()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true, PersistentGeo = true };

        try
        {
            await using var session = await CreateCollectionAsync(path, "persisted-rebuild", options);
            await session.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-rebuild", Payload = new() { ["title"] = "old title", ["location"] = Geo(52.5200, 13.4050) } });

            await session.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "persisted-rebuild", Payload = new() { ["title"] = "new title", ["location"] = Geo(48.8566, 2.3522) } });
            await session.Collection.BuildPayloadIndexAsync();
            await session.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "persisted-rebuild", options);
            var oldTitleFilter = new FilterClause(
                new[] { new FullTextCondition("title", "old title") },
                null, null);
            var newTitleFilter = new FilterClause(
                new[] { new FullTextCondition("title", "new title") },
                null, null);
            var berlinFilter = new FilterClause(
                new[] { new GeoRadiusCondition("location", 52.5200, 13.4050, 1000) },
                null, null);
            var parisFilter = new FilterClause(
                new[] { new GeoRadiusCondition("location", 48.8566, 2.3522, 1000) },
                null, null);

            var oldTitleResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, oldTitleFilter));
            var newTitleResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, newTitleFilter));
            var berlinResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, berlinFilter));
            var parisResults = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, parisFilter));

            Assert.Empty(oldTitleResults);
            Assert.Single(newTitleResults);
            Assert.Equal(1ul, newTitleResults[0].Id);
            Assert.Empty(berlinResults);
            Assert.Single(parisResults);
            Assert.Equal(1ul, parisResults[0].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task Reopen_WithoutExplicitOptions_LoadsManifestAndKeepsPayloadIndexWarm()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentMatch = true, PersistentRange = true, PersistentFullText = true, StoragePath = Path.Combine(path, "custom-indexes") };

        try
        {
            await using var setup = await CreateCollectionAsync(path, "manifested", options);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "manifested", Payload = new() { ["category"] = "pdf", ["year"] = 2022L } });

            var initialManifest = setup.Collection.GetManifest();
            Assert.True(initialManifest.PayloadIndexWarm);
            Assert.True(initialManifest.PersistentMatch);
            Assert.True(initialManifest.PersistentRange);
            Assert.Equal(CollectionManifest.CurrentPayloadIndexVersion, initialManifest.PayloadIndexVersion);
            Assert.Equal(Path.Combine(path, "custom-indexes"), initialManifest.PayloadIndexStoragePath);

            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "manifested", null);
            var reopenedManifest = reopened.Collection.GetManifest();
            var filter = new FilterClause(
                new[] { new MatchCondition("category", new MatchString("pdf")) },
                null, null);

            var results = await CollectAsync(reopened.Collection.SearchExactAsync(new Vector(new[] { 1f, 0f, 0f }), 10, filter));

            Assert.True(reopenedManifest.PayloadIndexWarm);
            Assert.True(reopenedManifest.PersistentMatch);
            Assert.True(reopenedManifest.PersistentRange);
            Assert.True(reopenedManifest.PersistentFullText);
            Assert.Equal(initialManifest.ChangeSequence, reopenedManifest.ChangeSequence);
            Assert.Single(results);
            Assert.Equal(1ul, results[0].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task Reopen_WithOutdatedPayloadIndexVersion_MarksManifestColdAndFallsBack()
    {
        string path = CreateTempPath();
        var options = new PayloadIndexOptions { PersistentFullText = true };

        try
        {
            await using (var setup = await CreateCollectionAsync(path, "stale-manifest", options))
            {
                await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                    new VectorMetadata { Id = 1, Collection = "stale-manifest", Payload = new() { ["body"] = "agent memory handbook" } });

                var staleManifest = setup.Collection.GetManifest();
                staleManifest.PayloadIndexVersion = CollectionManifest.CurrentPayloadIndexVersion - 1;
                await setup.Store.PutAsync(Encoding.UTF8.GetBytes("c:stale-manifest:i"), JsonSerializer.SerializeToUtf8Bytes(staleManifest));
            }

            await using var reopened = await CreateCollectionAsync(path, "stale-manifest", null);
            var manifest = reopened.Collection.GetManifest();
            var results = await CollectAsync(reopened.Collection.SearchTextAsync("body", "\"agent memory\""));

            Assert.False(manifest.PayloadIndexWarm);
            Assert.Equal(CollectionManifest.CurrentPayloadIndexVersion, manifest.PayloadIndexVersion);
            Assert.Single(results);
            Assert.Equal(1ul, results[0].Id);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public async Task ReadChangesAsync_ReplaysPersistedUpsertAndDeleteAfterReopen()
    {
        string path = CreateTempPath();

        try
        {
            await using var setup = await CreateCollectionAsync(path, "changes", null);
            await setup.Collection.PutAsync(1, new Vector(new[] { 1f, 0f, 0f }),
                new VectorMetadata { Id = 1, Collection = "changes", Payload = new() { ["category"] = "pdf" } });
            await setup.Collection.DeleteAsync(1);
            await setup.DisposeAsync();

            await using var reopened = await CreateCollectionAsync(path, "changes", null);
            using var cts = new CancellationTokenSource();
            var changes = await CollectFirstChangesAsync(reopened.Collection.ReadChangesAsync(ct: cts.Token), 2, cts);

            Assert.Equal(2, changes.Count);
            Assert.Equal("upsert", changes[0].Operation);
            Assert.Equal(1L, changes[0].Sequence);
            Assert.Single(changes[0].Items);
            Assert.Equal(1ul, changes[0].Items[0].Id);
            Assert.NotNull(changes[0].Items[0].Vector);
            Assert.NotNull(changes[0].Items[0].Payload);
            Assert.True(changes[0].Items[0].Payload!.ContainsKey("category"));

            Assert.Equal("delete", changes[1].Operation);
            Assert.Equal(2L, changes[1].Sequence);
            Assert.Single(changes[1].Items);
            Assert.Equal(1ul, changes[1].Items[0].Id);
            Assert.Null(changes[1].Items[0].Vector);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static Dictionary<string, object> Geo(double lat, double lon)
    {
        return new Dictionary<string, object> { ["lat"] = lat, ["lon"] = lon };
    }

    private static async Task<List<VectorSearchResult>> CollectAsync(IAsyncEnumerable<VectorSearchResult> results)
    {
        var list = new List<VectorSearchResult>();
        await foreach (var result in results)
            list.Add(result);
        return list;
    }

    private static async Task<List<CollectionChangeEvent>> CollectFirstChangesAsync(IAsyncEnumerable<CollectionChangeEvent> results, int expectedCount, CancellationTokenSource cts)
    {
        var list = new List<CollectionChangeEvent>();

        try
        {
            await foreach (var result in results.WithCancellation(cts.Token))
            {
                list.Add(result);
                if (list.Count >= expectedCount)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        return list;
    }

    private static async Task<CollectionSession> CreateCollectionAsync(string path, string name, PayloadIndexOptions? options)
    {
        var store = new BlobStore(new BlobStoreOptions(path));
        var manager = new VectorCollectionManager(store);
        var collection = manager.GetOrCreateCollection(name, 3, DistanceMetric.Euclidean, false, payloadIndexOptions: options);
        return await Task.FromResult(new CollectionSession(store, manager, collection));
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "walhalla-payload-persist-" + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempPath(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class CollectionSession : IAsyncDisposable
    {
        public CollectionSession(BlobStore store, VectorCollectionManager manager, VectorCollection collection)
        {
            Store = store;
            Manager = manager;
            Collection = collection;
        }

        public BlobStore Store { get; }

        public VectorCollectionManager Manager { get; }

        public VectorCollection Collection { get; }

        public ValueTask DisposeAsync()
        {
            Manager.Dispose();
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}