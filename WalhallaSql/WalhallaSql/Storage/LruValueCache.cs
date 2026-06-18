using System;
using System.Collections.Generic;
using WalhallaSql.Core;

namespace WalhallaSql.Storage;

internal sealed class LruValueCache
{
    private sealed class Node
    {
        public required byte[] Key { get; init; }
        public required byte[] Value { get; set; }
    }

    private readonly object _sync = new();
    private readonly long _capacityBytes;
    private long _sizeBytes;
    private readonly Dictionary<byte[], LinkedListNode<Node>> _map = new(ByteArrayContentComparer.Instance);
    private readonly LinkedList<Node> _lru = new();

    private const long PerEntryOverheadBytes = 72L;

    public LruValueCache(long capacityBytes)
    {
        _capacityBytes = Math.Max(0, capacityBytes);
    }

    private long _hitCount;
    private long _missCount;
    public long HitCount { get { lock (_sync) return _hitCount; } }
    public long MissCount { get { lock (_sync) return _missCount; } }
    public long CurrentSizeBytes { get { lock (_sync) return _sizeBytes; } }
    public long CapacityBytes => _capacityBytes;

    public bool TryGet(byte[] key, out byte[]? value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hitCount++;
                value = (byte[])node.Value.Value.Clone();
                return true;
            }
            _missCount++;
            value = null;
            return false;
        }
    }

    public bool TryGetBorrowed(byte[] key, out byte[]? value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hitCount++;
                value = node.Value.Value;
                return true;
            }
            _missCount++;
            value = null;
            return false;
        }
    }

    public void Set(byte[] key, byte[] value)
    {
        if (_capacityBytes <= 0) return;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _sizeBytes -= existing.Value.Key.Length + existing.Value.Value.Length + PerEntryOverheadBytes;
                existing.Value.Value = (byte[])value.Clone();
                _sizeBytes += existing.Value.Key.Length + value.Length + PerEntryOverheadBytes;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                Trim();
                return;
            }

            var keyCopy = (byte[])key.Clone();
            var node = new LinkedListNode<Node>(new Node { Key = keyCopy, Value = (byte[])value.Clone() });
            _map[keyCopy] = node;
            _lru.AddFirst(node);
            _sizeBytes += key.Length + value.Length + PerEntryOverheadBytes;
            Trim();
        }
    }

    public void SetWeak(byte[] key, byte[] value)
    {
        if (_capacityBytes <= 0) return;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _sizeBytes -= existing.Value.Key.Length + existing.Value.Value.Length + PerEntryOverheadBytes;
                existing.Value.Value = value;
                _sizeBytes += existing.Value.Key.Length + value.Length + PerEntryOverheadBytes;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                Trim();
                return;
            }

            var keyCopy = (byte[])key.Clone();
            var node = new LinkedListNode<Node>(new Node { Key = keyCopy, Value = value });
            _map[keyCopy] = node;
            _lru.AddFirst(node);
            _sizeBytes += key.Length + value.Length + PerEntryOverheadBytes;
            Trim();
        }
    }

    public void Remove(byte[] key)
    {
        lock (_sync)
        {
            if (!_map.TryGetValue(key, out var node)) return;
            _map.Remove(key);
            _lru.Remove(node);
            _sizeBytes -= node.Value.Key.Length + node.Value.Value.Length + PerEntryOverheadBytes;
        }
    }

    private void Trim()
    {
        while (_sizeBytes > _capacityBytes && _lru.Last is { } tail)
        {
            _map.Remove(tail.Value.Key);
            _sizeBytes -= tail.Value.Key.Length + tail.Value.Value.Length + PerEntryOverheadBytes;
            _lru.RemoveLast();
        }
    }
}
