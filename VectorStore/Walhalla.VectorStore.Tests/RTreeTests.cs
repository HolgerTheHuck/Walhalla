// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Walhalla.Indexes.Spatial;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class RTreeTests
{
    [Fact]
    public void Insert_And_Search_SingleEntry()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });

        var results = tree.Search(new[] { 0.5, 0.5 }, new[] { 1.5, 1.5 }).ToList();
        Assert.Single(results);
        Assert.Contains(1L, results);
    }

    [Fact]
    public void Insert_And_Search_MultipleEntries()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
        tree.Insert(2, new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 });
        tree.Insert(3, new[] { 5.0, 5.0 }, new[] { 6.0, 6.0 });

        var results = tree.Search(new[] { 0.5, 0.5 }, new[] { 2.5, 2.5 }).ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(1L, results);
        Assert.Contains(2L, results);
        Assert.DoesNotContain(3L, results);
    }

    [Fact]
    public void Search_NoIntersection_ReturnsEmpty()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });

        var results = tree.Search(new[] { 10.0, 10.0 }, new[] { 11.0, 11.0 }).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
        tree.Insert(2, new[] { 2.0, 2.0 }, new[] { 3.0, 3.0 });

        Assert.True(tree.Delete(1));
        var results = tree.Search(new[] { 0.0, 0.0 }, new[] { 3.0, 3.0 }).ToList();
        Assert.Single(results);
        Assert.Contains(2L, results);
    }

    [Fact]
    public void Delete_NonExistent_ReturnsFalse()
    {
        var tree = new RTree(2, maxEntries: 4);
        Assert.False(tree.Delete(99));
    }

    [Fact]
    public void Insert_OverwriteExisting_Replaces()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 });
        tree.Insert(1, new[] { 5.0, 5.0 }, new[] { 6.0, 6.0 });

        var oldResults = tree.Search(new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }).ToList();
        Assert.Empty(oldResults);

        var newResults = tree.Search(new[] { 5.0, 5.0 }, new[] { 6.0, 6.0 }).ToList();
        Assert.Single(newResults);
        Assert.Contains(1L, newResults);
    }

    [Fact]
    public void Search_3D()
    {
        var tree = new RTree(3, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0 });
        tree.Insert(2, new[] { 2.0, 2.0, 2.0 }, new[] { 3.0, 3.0, 3.0 });

        var results = tree.Search(new[] { 0.5, 0.5, 0.5 }, new[] { 2.5, 2.5, 2.5 }).ToList();
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void BulkInsert_TriggersSplit()
    {
        var tree = new RTree(2, maxEntries: 4);
        var random = new Random(42);

        for (int i = 1; i <= 100; i++)
        {
            double x = random.NextDouble() * 100;
            double y = random.NextDouble() * 100;
            tree.Insert(i, new[] { x, y }, new[] { x + 1, y + 1 });
        }

        Assert.True(tree.NodeCount > 1);
        Assert.Equal(100, tree.EntryCount);

        var results = tree.Search(new[] { 0.0, 0.0 }, new[] { 50.0, 50.0 }).ToList();
        Assert.True(results.Count > 0);
    }

    [Fact]
    public void OverlappingRectangles_AllFound()
    {
        var tree = new RTree(2, maxEntries: 4);
        tree.Insert(1, new[] { 0.0, 0.0 }, new[] { 2.0, 2.0 });
        tree.Insert(2, new[] { 1.0, 1.0 }, new[] { 3.0, 3.0 });
        tree.Insert(3, new[] { 1.5, 1.5 }, new[] { 4.0, 4.0 });

        var results = tree.Search(new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 }).ToList();
        Assert.Equal(3, results.Count);
        Assert.Contains(1L, results);
        Assert.Contains(2L, results);
        Assert.Contains(3L, results);
    }
}
