// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;

namespace Walhalla.VectorStore;

/// <summary>
/// LRU-Cache für Vektoren zur Beschleunigung von HNSW-Suchanfragen.
/// </summary>
public sealed class VectorCache
{
    private readonly int _capacity;
    private readonly Dictionary<ulong, Node> _cache;
    private readonly LinkedList<ulong> _lru;
    private readonly object _lock = new();

    private long _hits;
    private long _misses;

    private sealed class Node
    {
        public required Vector Vector { get; init; }
        public LinkedListNode<ulong>? ListNode { get; set; }
    }

    public VectorCache(int capacity)
    {
        _capacity = capacity;
        _cache = new Dictionary<ulong, Node>(capacity);
        _lru = new LinkedList<ulong>();
    }

    public bool TryGet(ulong id, out Vector vector)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(id, out var node))
            {
                // Move to front
                if (node.ListNode is not null)
                {
                    _lru.Remove(node.ListNode);
                    _lru.AddFirst(node.ListNode);
                }
                vector = node.Vector;
                _hits++;
                return true;
            }
            vector = default;
            _misses++;
            return false;
        }
    }

    public IEnumerable<ulong> GetAllIds()
    {
        lock (_lock)
        {
            return _cache.Keys.ToList();
        }
    }

    /// <summary>
    /// Batch-Extraktion mehrerer Vektor-IDs in einem Lock-Zyklus.
    /// Vermeidet N einzelne Lock-Acquires fuer bessere Performance bei grossen Scans.
    /// </summary>
    public List<(ulong Id, float[] Data)> TryGetBatch(IEnumerable<ulong> ids)
    {
        var result = new List<(ulong, float[])>();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (_cache.TryGetValue(id, out var node))
                {
                    result.Add((id, node.Vector.Data));
                }
            }
        }
        return result;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    public void Put(ulong id, Vector vector)
    {
        lock (_lock)
        {
            if (_capacity <= 0) return;

            if (_cache.TryGetValue(id, out var existing))
            {
                // Update existing
                if (existing.ListNode is not null)
                {
                    _lru.Remove(existing.ListNode);
                    _lru.AddFirst(existing.ListNode);
                }
                _cache[id] = new Node { Vector = vector, ListNode = existing.ListNode };
                return;
            }

            // Evict if necessary
            while (_cache.Count >= _capacity && _lru.Count > 0)
            {
                var oldest = _lru.Last;
                if (oldest is not null)
                {
                    _lru.RemoveLast();
                    _cache.Remove(oldest.Value);
                }
            }

            // Add new
            var listNode = new LinkedListNode<ulong>(id);
            var node = new Node { Vector = vector, ListNode = listNode };
            _cache[id] = node;
            _lru.AddFirst(listNode);
        }
    }

    public void Remove(ulong id)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(id, out var node))
            {
                if (node.ListNode is not null)
                    _lru.Remove(node.ListNode);
                _cache.Remove(id);
            }
        }
    }

    /// <summary>
    /// Gibt Cache-Statistiken zurück (Hits, Misses, Hit-Rate).
    /// </summary>
    public (long Hits, long Misses, double HitRate) GetStats()
    {
        lock (_lock)
        {
            var total = _hits + _misses;
            var hitRate = total > 0 ? (double)_hits / total : 0;
            return (_hits, _misses, hitRate);
        }
    }
}
