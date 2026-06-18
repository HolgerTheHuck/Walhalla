// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Trees;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;
using Xunit;

namespace Walhalla.VectorStore.Tests;

/// <summary>
/// Tests für <see cref="EmbeddedVectorStore"/> mit <see cref="StorageBackend.MvccBPlusTree"/>.
/// Verifiziert Parität zum Legacy-B+Tree-/BlobStore-Pfad auf identischer Workload.
/// </summary>
public class MvccBackendTests : IDisposable
{
    private readonly string _dbPath;
    private EmbeddedVectorStore? _store;

    public MvccBackendTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_test_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        _store?.Dispose();
        if (Directory.Exists(_dbPath))
        {
            try { Directory.Delete(_dbPath, recursive: true); } catch { /* locked by testhost */ }
        }
    }

    private EmbeddedVectorStore CreateStore()
    {
        var options = new StorageEngineOptions
        {
            RootPath = _dbPath,
            Backend = StorageBackend.MvccBPlusTree,
            OverflowThresholdBytes = 256
        };
        _store = new EmbeddedVectorStore(options);
        return _store;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Konstruktion
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_MvccBPlusTree_CreatesStore()
    {
        using var store = CreateStore();
        Assert.NotNull(store);
    }

    [Fact]
    public void Constructor_MvccBPlusTree_CreatesDataFiles()
    {
        using var store = CreateStore();
        Assert.True(Directory.Exists(_dbPath));
        var files = Directory.EnumerateFiles(_dbPath, "*", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 0, "Es sollten mindestens ODS- oder WAL-Dateien erstellt werden.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Collection-CRUD
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetOrCreateCollection_ReturnsCollection()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);

        Assert.NotNull(collection);
        Assert.Equal("docs", collection.Name);
        Assert.Equal(128, collection.Dimension);
    }

    [Fact]
    public async Task PutAsync_StoresVector()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.True(vector.Span.SequenceEqual(entry.Vector.Span));
    }

    [Fact]
    public async Task PutAsync_WithMetadata_StoresMetadata()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var metadata = new VectorMetadata { Id = 1, Collection = "test", Payload = new() { ["title"] = "Test" } };

        await collection.PutAsync(1, vector, metadata);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.NotNull(entry.Metadata);
        var titleValue = entry.Metadata.Payload!["title"];
        if (titleValue is System.Text.Json.JsonElement element)
            Assert.Equal("Test", element.GetString());
        else
            Assert.Equal("Test", titleValue);
    }

    [Fact]
    public async Task DeleteAsync_RemovesVector()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);
        var vector = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        await collection.PutAsync(1, vector);
        await collection.DeleteAsync(1);
        var entry = await collection.GetAsync(1);

        Assert.Null(entry);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Suche (Exact + HNSW)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchExactAsync_ReturnsCorrectTopK()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 3, DistanceMetric.Euclidean);

        await collection.PutAsync(1, new Vector(new float[] { 1.0f, 0.0f, 0.0f }));
        await collection.PutAsync(2, new Vector(new float[] { 0.0f, 1.0f, 0.0f }));
        await collection.PutAsync(3, new Vector(new float[] { 0.0f, 0.0f, 1.0f }));

        var query = new Vector(new float[] { 1.0f, 0.1f, 0.1f });
        var results = await collection.SearchExactAsync(query, topK: 2).ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1ul, results[0].Id); // Nächster an [1,0,0]
    }

    [Fact]
    public async Task SearchHnswAsync_ReturnsCorrectTopK_WithRecall()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 16, DistanceMetric.Cosine, enableHnsw: true,
            hnswOptions: new HnswOptions { M = 8, EfConstruction = 64 });

        var random = new Random(42);
        for (uint i = 1; i <= 100; i++)
        {
            var data = new float[16];
            for (int j = 0; j < 16; j++)
                data[j] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(data.AsSpan());
            await collection.PutAsync(i, new Vector(data));
        }

        var queryData = new float[16];
        for (int j = 0; j < 16; j++)
            queryData[j] = (float)random.NextDouble();
        VectorDistance.NormalizeL2(queryData.AsSpan());
        var query = new Vector(queryData);

        var exactResults = await collection.SearchExactAsync(query, topK: 5).ToListAsync();
        var hnswResults = await collection.SearchHnswAsync(query, topK: 5).ToListAsync();

        var exactIds = new System.Collections.Generic.HashSet<ulong>(exactResults.Select(r => r.Id));
        var hnswIds = new System.Collections.Generic.HashSet<ulong>(hnswResults.Select(r => r.Id));
        var recall = (double)exactIds.Intersect(hnswIds).Count() / exactIds.Count;

        Assert.True(recall >= 0.8, $"HNSW Recall sollte >= 80% sein, war {recall:P1}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Overflow-Roundtrip (768-dim float32 = 3072 B > 256 B Threshold)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Overflow_Roundtrip_768DimVector()
    {
        using var store = CreateStore();
        // Euclidean vermeidet interne Normalisierung, damit der Roundtrip byte-identisch bleibt.
        var collection = store.GetOrCreateCollection("embeddings", 768, DistanceMetric.Euclidean);

        var random = new Random(42);
        var data = new float[768];
        for (int i = 0; i < 768; i++)
            data[i] = (float)random.NextDouble();
        var vector = new Vector(data);

        await collection.PutAsync(1, vector);
        var entry = await collection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.True(vector.Span.SequenceEqual(entry.Vector.Span), "768-dim Vektor nach Overflow-Roundtrip identisch");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Reopen / Recovery
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reopen_Recovery_RestoresVectorsAndMetadata()
    {
        using (var store = CreateStore())
        {
            var collection = store.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
            var random = new Random(42);
            var data = new float[128];
            for (int i = 0; i < 128; i++)
                data[i] = (float)random.NextDouble();
            VectorDistance.NormalizeL2(data.AsSpan());
            var vector = new Vector(data);
            var metadata = new VectorMetadata { Id = 1, Collection = "docs", Payload = new() { ["key"] = "value" } };

            await collection.PutAsync(1, vector, metadata);
            await store.CheckpointAsync();
        }

        // Reopen auf gleichem Pfad
        using var reopenedStore = CreateStore();
        var reopenedCollection = reopenedStore.GetOrCreateCollection("docs", 128, DistanceMetric.Cosine);
        var entry = await reopenedCollection.GetAsync(1);

        Assert.NotNull(entry);
        Assert.Equal(1ul, entry.Id);
        Assert.NotNull(entry.Metadata);
        var keyValue = entry.Metadata.Payload!["key"];
        if (keyValue is System.Text.Json.JsonElement element)
            Assert.Equal("value", element.GetString());
        else
            Assert.Equal("value", keyValue);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Prefix-Scan / Enumeration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnumerateIdsAsync_ReturnsAllIds()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 8, DistanceMetric.Cosine);

        await collection.PutAsync(1, new Vector(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
        await collection.PutAsync(2, new Vector(new float[] { 0, 1, 0, 0, 0, 0, 0, 0 }));
        await collection.PutAsync(3, new Vector(new float[] { 0, 0, 1, 0, 0, 0, 0, 0 }));

        var ids = await collection.EnumerateIdsAsync().ToListAsync();

        Assert.Equal(3, ids.Count);
        Assert.Contains(1ul, ids);
        Assert.Contains(2ul, ids);
        Assert.Contains(3ul, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. Checkpoint & Vacuum
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CheckpointAsync_CompletesWithoutException()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 8, DistanceMetric.Cosine);
        await collection.PutAsync(1, new Vector(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }));

        await store.CheckpointAsync();
    }

    [Fact]
    public async Task Vacuum_CompletesWithoutException()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 8, DistanceMetric.Cosine);
        await collection.PutAsync(1, new Vector(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
        await collection.PutAsync(2, new Vector(new float[] { 0, 1, 0, 0, 0, 0, 0, 0 }));

        await store.CheckpointAsync();

        // Vacuum auf dem IKeyValueStore – EmbeddedVectorStore exponiert es nicht direkt,
        // aber wir können über Reflection den inneren Store erreichen.
        var storeField = typeof(EmbeddedVectorStore).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(storeField);
        var innerStore = (IKeyValueStore)storeField.GetValue(store)!;
        innerStore.Vacuum();

        var entry = await collection.GetAsync(1);
        Assert.NotNull(entry); // Daten müssen nach Vacuum noch lesbar sein
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. MVCC/Snapshot-Isolation (direkt auf IKeyValueStore)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void MvccSnapshot_Isolation_ReadSnapshotSeesCommittedData()
    {
        using var store = CreateStore();
        var collection = store.GetOrCreateCollection("test", 8, DistanceMetric.Cosine);
        collection.PutAsync(1, new Vector(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })).GetAwaiter().GetResult();

        var storeField = typeof(EmbeddedVectorStore).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(storeField);
        var innerStore = (IKeyValueStore)storeField.GetValue(store)!;

        using var snapshot = innerStore.BeginReadSnapshot();
        var key = System.Text.Encoding.UTF8.GetBytes("c:test:v:1");
        Assert.True(snapshot.TryGet(key, out var value));
        Assert.NotNull(value);

        // Neuer Write nach Snapshot-Erzeugung
        collection.PutAsync(2, new Vector(new float[] { 0, 1, 0, 0, 0, 0, 0, 0 })).GetAwaiter().GetResult();

        // Snapshot sieht weiterhin nur den alten Stand
        var key2 = System.Text.Encoding.UTF8.GetBytes("c:test:v:2");
        Assert.False(snapshot.TryGet(key2, out _));
    }
}
