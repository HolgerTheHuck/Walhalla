// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Xunit;

namespace Walhalla.VectorStore.Tests;

public class VectorCacheTests
{
    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = new VectorCache(10);

        var found = cache.TryGet(1, out var vector);

        Assert.False(found);
        Assert.Equal(default, vector);
    }

    [Fact]
    public void PutAndTryGet_ReturnsTrueAndCorrectVector()
    {
        var cache = new VectorCache(10);
        var vec = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        cache.Put(1, vec);
        var found = cache.TryGet(1, out var result);

        Assert.True(found);
        Assert.Equal(vec.Dimension, result.Dimension);
        Assert.True(vec.Span.SequenceEqual(result.Span));
    }

    [Fact]
    public void Put_DuplicateId_UpdatesVector()
    {
        var cache = new VectorCache(10);
        var vec1 = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var vec2 = new Vector(new float[] { 4.0f, 5.0f, 6.0f });

        cache.Put(1, vec1);
        cache.Put(1, vec2);
        var found = cache.TryGet(1, out var result);

        Assert.True(found);
        Assert.True(vec2.Span.SequenceEqual(result.Span));
    }

    [Fact]
    public void Remove_ExistingEntry_RemovesFromCache()
    {
        var cache = new VectorCache(10);
        var vec = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        cache.Put(1, vec);
        cache.Remove(1);
        var found = cache.TryGet(1, out _);

        Assert.False(found);
    }

    [Fact]
    public void Remove_NonExistingEntry_DoesNotThrow()
    {
        var cache = new VectorCache(10);

        cache.Remove(999);

        // Should not throw
    }

    [Fact]
    public void Eviction_ExceedsCapacity_RemovesOldest()
    {
        var cache = new VectorCache(2);
        var vec1 = new Vector(new float[] { 1.0f });
        var vec2 = new Vector(new float[] { 2.0f });
        var vec3 = new Vector(new float[] { 3.0f });

        cache.Put(1, vec1);
        cache.Put(2, vec2);
        cache.Put(3, vec3); // Should evict 1

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void Eviction_AccessUpdatesRecency()
    {
        var cache = new VectorCache(2);
        var vec1 = new Vector(new float[] { 1.0f });
        var vec2 = new Vector(new float[] { 2.0f });
        var vec3 = new Vector(new float[] { 3.0f });

        cache.Put(1, vec1);
        cache.Put(2, vec2);
        cache.TryGet(1, out _); // Access 1, making 2 oldest
        cache.Put(3, vec3); // Should evict 2

        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void Eviction_PutUpdatesRecency()
    {
        var cache = new VectorCache(2);
        var vec1 = new Vector(new float[] { 1.0f });
        var vec2 = new Vector(new float[] { 2.0f });
        var vec3 = new Vector(new float[] { 3.0f });

        cache.Put(1, vec1);
        cache.Put(2, vec2);
        cache.Put(1, vec1); // Re-put 1, making 2 oldest
        cache.Put(3, vec3); // Should evict 2

        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void CapacityZero_DoesNotStoreAnything()
    {
        var cache = new VectorCache(0);
        var vec = new Vector(new float[] { 1.0f });

        cache.Put(1, vec);
        var found = cache.TryGet(1, out _);

        Assert.False(found);
    }

    [Fact]
    public void ThreadSafety_ConcurrentPutsAndGets()
    {
        var cache = new VectorCache(100);
        var vectors = new Vector[100];
        for (int i = 0; i < 100; i++)
            vectors[i] = new Vector(new float[] { i });

        // Concurrent puts
        System.Threading.Tasks.Parallel.For(0, 100, i =>
        {
            cache.Put((ulong)i, vectors[i]);
        });

        // Concurrent gets
        System.Threading.Tasks.Parallel.For(0, 100, i =>
        {
            var found = cache.TryGet((ulong)i, out var vec);
            Assert.True(found);
            Assert.Equal(i, vec.Span[0]);
        });
    }
}
