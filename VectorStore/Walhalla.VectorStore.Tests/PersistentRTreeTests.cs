// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Indexes.Spatial;
using Walhalla.Storage.Trees;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Core.Configuration;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class PersistentRTreeTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTripsEntries()
    {
        var tree = new RTree(3, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0 });
        tree.Insert(2, new[] { 2.0, 2.0, 2.0 }, new[] { 3.0, 3.0, 3.0 });

        var restored = RTree.Deserialize(tree.Serialize());

        Assert.Equal(3, restored.Dimensions);
        Assert.Equal(4, restored.MaxEntries);
        Assert.Equal(2, restored.EntryCount);
        var results = restored.Search(new[] { 0.5, 0.5, 0.5 }, new[] { 2.5, 2.5, 2.5 }).ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(1L, results);
        Assert.Contains(2L, results);
    }

    [Fact]
    public void PersistentRTree_Reopen_RestoresEntries()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateBlobStore(path))
            {
                var tree = new PersistentRTree(new BlobStoreIKeyValueAdapter(store), "geo:location", dimensions: 2, maxEntries: 4);
                tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
                tree.Insert(2, new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 });
            }

            using (var store = CreateBlobStore(path))
            {
                var tree = new PersistentRTree(new BlobStoreIKeyValueAdapter(store), "geo:location", dimensions: 2, maxEntries: 4);
                var results = tree.Search(new[] { 0.5, 0.5 }, new[] { 2.5, 2.5 }).ToList();

                Assert.Equal(2, tree.EntryCount);
                Assert.Equal(2, results.Count);
                Assert.Contains(1L, results);
                Assert.Contains(2L, results);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void PersistentRTree_Delete_Reopen_RemovesEntry()
    {
        string path = CreateTempPath();
        try
        {
            using (var store = CreateBlobStore(path))
            {
                var tree = new PersistentRTree(new BlobStoreIKeyValueAdapter(store), "geo:location", dimensions: 2, maxEntries: 4);
                tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
                tree.Insert(2, new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 });
                Assert.True(tree.Delete(1));
            }

            using (var store = CreateBlobStore(path))
            {
                var tree = new PersistentRTree(new BlobStoreIKeyValueAdapter(store), "geo:location", dimensions: 2, maxEntries: 4);
                var results = tree.Search(new[] { 0.0, 0.0 }, new[] { 3.0, 3.0 }).ToList();

                Assert.Single(results);
                Assert.DoesNotContain(1L, results);
                Assert.Contains(2L, results);
            }
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static BlobStore CreateBlobStore(string path)
    {
        return new BlobStore(new BlobStoreOptions(path)
        {
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            MemTableMode = MemTableMode.InMemory
        });
    }

    private static string CreateTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "walhalla-rtree-" + Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempPath(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}