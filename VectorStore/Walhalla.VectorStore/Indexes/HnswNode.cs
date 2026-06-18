// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// Ein Knoten im HNSW-Graphen.
/// Speichert nur Nachbar-IDs, nicht die Vektoren selbst.
/// </summary>
public sealed class HnswNode
{
    /// <summary>Einzigartige ID (entspricht VectorRepository ID).</summary>
    public readonly ulong Id;

    /// <summary>Layer auf dem dieser Knoten beginnt (0 = base layer).</summary>
    public readonly int TopLayer;

    /// <summary>
    /// Nachbarn pro Layer.
    /// neighbors[layer] = Nachbar-IDs auf diesem Layer.
    /// Layer 0 ist der dichteste Graph mit den meisten Verbindungen.
    /// </summary>
    public readonly int[][] Neighbors;

    /// <summary>
    /// Der Vektor dieses Knotens (optional, direkt gespeichert für schnelle Distanzberechnung).
    /// </summary>
    public readonly float[]? Vector;

    /// <summary>
    /// Markiert als gelöscht (Soft-Delete für Online-Index).
    /// </summary>
    public volatile bool IsDeleted;

    internal readonly object NeighborLock = new();

    public HnswNode(ulong id, int topLayer, int[] neighborCounts, float[]? vector = null)
    {
        Id = id;
        TopLayer = topLayer;
        Vector = vector;
        Neighbors = new int[topLayer + 1][];
        for (int i = 0; i <= topLayer; i++)
        {
            Neighbors[i] = new int[neighborCounts[i]];
            Array.Fill(Neighbors[i], -1); // -1 = uninitialisiert
        }
    }

    /// <summary>
    /// Anzahl aktiver Nachbarn auf einem Layer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNeighborCount(int layer)
    {
        var layerNeighbors = Neighbors[layer];
        int count = 0;
        for (int i = 0; i < layerNeighbors.Length && layerNeighbors[i] >= 0; i++)
            count++;
        return count;
    }

    /// <summary>
    /// Fügt einen Nachbarn hinzu (internes Array wird nicht vergrößert).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAddNeighbor(int layer, int neighborIndex)
    {
        if (layer < 0 || layer >= Neighbors.Length)
            return false;
        var layerNeighbors = Neighbors[layer];
        lock (NeighborLock)
        {
            for (int i = 0; i < layerNeighbors.Length; i++)
            {
                if (layerNeighbors[i] < 0)
                {
                    layerNeighbors[i] = neighborIndex;
                    return true;
                }
                if (layerNeighbors[i] == neighborIndex)
                    return true; // Bereits vorhanden
            }
        }
        return false; // Kein Platz
    }

    /// <summary>
    /// Entfernt einen Nachbarn.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveNeighbor(int layer, int neighborIndex)
    {
        var layerNeighbors = Neighbors[layer];
        lock (NeighborLock)
        {
            for (int i = 0; i < layerNeighbors.Length; i++)
            {
                if (layerNeighbors[i] == neighborIndex)
                {
                    // Shift nach links
                    Array.Copy(layerNeighbors, i + 1, layerNeighbors, i, layerNeighbors.Length - i - 1);
                    layerNeighbors[^1] = -1;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Thread-sichere Kopie aller initialisierten Nachbarn auf einem Layer.
    /// </summary>
    public int[] GetNeighborsCopy(int layer)
    {
        if (layer < 0 || layer >= Neighbors.Length)
            return Array.Empty<int>();

        lock (NeighborLock)
        {
            var layerNeighbors = Neighbors[layer];
            int count = 0;
            for (; count < layerNeighbors.Length && layerNeighbors[count] >= 0; count++) { }
            if (count == 0) return Array.Empty<int>();
            var copy = new int[count];
            Array.Copy(layerNeighbors, copy, count);
            return copy;
        }
    }

    /// <summary>
    /// Alle Nachbarn auf einem Layer als Span (nur initialisierte).
    /// NICHT thread-safe – nur unter NeighborLock verwenden.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<int> GetNeighborsUnsafe(int layer)
    {
        if (layer < 0 || layer >= Neighbors.Length)
            return ReadOnlySpan<int>.Empty;
        var layerNeighbors = Neighbors[layer];
        int count = 0;
        for (; count < layerNeighbors.Length && layerNeighbors[count] >= 0; count++) { }
        return layerNeighbors.AsSpan(0, count);
    }
}

/// <summary>
/// Index-basierte Verwaltung für schnelle Node-Lookup.
/// Optimiert für single-writer Szenarien (sequentielles Insert).
/// </summary>
internal sealed class HnswNodeTable
{
    private readonly ConcurrentDictionary<ulong, int> _idToIndex = new();
    private HnswNode?[] _nodes = new HnswNode?[1024];
    private volatile int _count = 0;
    private readonly object _lock = new();

    public int Count => _count;

    public int GetOrAddIndex(ulong id, Func<int, HnswNode> factory)
    {
        if (_idToIndex.TryGetValue(id, out var index))
            return index;

        lock (_lock)
        {
            if (_idToIndex.TryGetValue(id, out index))
                return index;

            index = _count;
            if (index >= _nodes.Length)
            {
                Array.Resize(ref _nodes, _nodes.Length * 2);
            }
            _nodes[index] = factory(index);
            _idToIndex[id] = index;
            _count = index + 1;
            return index;
        }
    }

    public int? GetIndex(ulong id)
    {
        return _idToIndex.TryGetValue(id, out var index) ? index : null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _idToIndex.Clear();
            Array.Clear(_nodes);
            _count = 0;
        }
    }

    public HnswNode? GetNode(int index)
    {
        if ((uint)index >= (uint)_count)
            return null;
        return _nodes[index];
    }

    public HnswNode? GetNodeById(ulong id)
    {
        var idx = GetIndex(id);
        return idx.HasValue ? GetNode(idx.Value) : null;
    }

    public bool RemoveId(ulong id)
    {
        lock (_lock)
        {
            return _idToIndex.TryRemove(id, out _);
        }
    }

    public IEnumerable<HnswNode> GetAllNodes()
    {
        var count = _count;
        for (int i = 0; i < count; i++)
        {
            var node = _nodes[i];
            if (node is { IsDeleted: false })
                yield return node;
        }
    }
}
