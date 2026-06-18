using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WalhallaSql.Caching;

/// <summary>
/// Thread-safe, bounded LRU cache with 8-shard sharding to reduce lock contention.
/// When a shard reaches its share of the total capacity, the least-recently-used
/// entry in that shard is evicted to make room.
/// </summary>
internal sealed class BoundedLruCache<TValue>
{
    private const int NumShards = 8;

    private sealed class Shard
    {
        public readonly object Lock = new();
        public readonly Dictionary<string, LinkedListNode<(string Key, TValue Value)>> Map
            = new(StringComparer.Ordinal);
        public readonly LinkedList<(string Key, TValue Value)> Lru = new();
    }

    private readonly Shard[] _shards;
    private readonly int _shardCapacity;
    private readonly int _capacity;

    public BoundedLruCache(int capacity = 10_000)
    {
        if (capacity <= 0)
            capacity = 10_000;
        _capacity = capacity;
        _shardCapacity = Math.Max(1, capacity / NumShards);
        _shards = new Shard[NumShards];
        for (var i = 0; i < NumShards; i++)
            _shards[i] = new Shard();
    }

    /// <summary>Total nominal capacity across all shards.</summary>
    public int Capacity => _capacity;

    /// <summary>Current total entry count across all shards (approximate under concurrency).</summary>
    public int Count
    {
        get
        {
            var total = 0;
            foreach (var shard in _shards)
            {
                lock (shard.Lock)
                    total += shard.Map.Count;
            }
            return total;
        }
    }

    /// <summary>
    /// Looks up <paramref name="key"/>. Promotes the entry to MRU position on hit.
    /// Returns <see langword="false"/> on miss.
    /// </summary>
    public bool TryGet(string key, out TValue value)
    {
        var shard = GetShard(key);
        lock (shard.Lock)
        {
            if (shard.Map.TryGetValue(key, out var node))
            {
                // Move to head (most recently used)
                shard.Lru.Remove(node);
                shard.Lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, replacing any
    /// previous entry. Evicts the LRU entry when the shard is at capacity.
    /// </summary>
    public void Set(string key, TValue value)
    {
        var shard = GetShard(key);
        lock (shard.Lock)
        {
            // Remove existing entry so we can re-insert at head
            if (shard.Map.TryGetValue(key, out var existing))
            {
                shard.Lru.Remove(existing);
                shard.Map.Remove(key);
            }

            var node = new LinkedListNode<(string, TValue)>((key, value));
            shard.Map[key] = node;
            shard.Lru.AddFirst(node);

            // Evict LRU entries until within capacity
            while (shard.Map.Count > _shardCapacity && shard.Lru.Last is { } tail)
            {
                shard.Map.Remove(tail.Value.Key);
                shard.Lru.RemoveLast();
            }
        }
    }

    /// <summary>Removes all entries from all shards.</summary>
    public void Clear()
    {
        foreach (var shard in _shards)
        {
            lock (shard.Lock)
            {
                shard.Map.Clear();
                shard.Lru.Clear();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Shard GetShard(string key)
    {
        var hash = StringComparer.Ordinal.GetHashCode(key);
        return _shards[(hash & int.MaxValue) % NumShards];
    }
}
