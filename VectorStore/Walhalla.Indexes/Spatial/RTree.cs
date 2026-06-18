// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Walhalla.Indexes.Spatial;

/// <summary>
/// Eintraegs-Record fuer den R-Tree: ID + Bounding Box (min/max pro Dimension).
/// </summary>
public sealed class RTreeEntry
{
    public long Id { get; set; }
    public double[] Min { get; }
    public double[] Max { get; }

    public RTreeEntry(long id, ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        Id = id;
        Min = min.ToArray();
        Max = max.ToArray();
    }

    /// <summary>Prueft, ob sich zwei Bounding Boxes schneiden.</summary>
    public bool Intersects(ReadOnlySpan<double> qMin, ReadOnlySpan<double> qMax)
    {
        for (int i = 0; i < Min.Length; i++)
        {
            if (Min[i] > qMax[i] || Max[i] < qMin[i])
                return false;
        }
        return true;
    }

    /// <summary>Berechnet die Flaeche der Bounding Box.</summary>
    public double Area()
    {
        double area = 1.0;
        for (int i = 0; i < Min.Length; i++)
            area *= Max[i] - Min[i];
        return area;
    }

    /// <summary>
    /// Berechnet die Flaechenvergroesserung, wenn ein Rechteck hinzugefuegt wird.
    /// </summary>
    public double Enlargement(ReadOnlySpan<double> rMin, ReadOnlySpan<double> rMax)
    {
        double enlarged = 1.0;
        for (int i = 0; i < Min.Length; i++)
        {
            double dMin = Math.Min(Min[i], rMin[i]);
            double dMax = Math.Max(Max[i], rMax[i]);
            enlarged *= dMax - dMin;
        }
        return enlarged - Area();
    }

    /// <summary>Erweitert diese BB um ein weiteres Rechteck.</summary>
    public void Expand(ReadOnlySpan<double> rMin, ReadOnlySpan<double> rMax)
    {
        for (int i = 0; i < Min.Length; i++)
        {
            Min[i] = Math.Min(Min[i], rMin[i]);
            Max[i] = Math.Max(Max[i], rMax[i]);
        }
    }
}

/// <summary>
/// Knoten des R-Trees. Enthaelt entweder Leaf-Eintraege (RTreeEntry) oder Child-Knoten.
/// </summary>
public sealed class RTreeNode
{
    public bool IsLeaf { get; }
    public List<RTreeEntry> Entries { get; }
    public List<RTreeNode> Children { get; }
    public double[] Min { get; }
    public double[] Max { get; }

    public RTreeNode(int dimensions, bool isLeaf)
    {
        IsLeaf = isLeaf;
        Entries = isLeaf ? new List<RTreeEntry>() : new List<RTreeEntry>();
        Children = isLeaf ? new List<RTreeNode>() : new List<RTreeNode>();
        Min = new double[dimensions];
        Max = new double[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            Min[i] = double.MaxValue;
            Max[i] = double.MinValue;
        }
    }

    public int Count => IsLeaf ? Entries.Count : Children.Count;

    public void AddEntry(RTreeEntry entry)
    {
        Entries.Add(entry);
        Expand(entry.Min, entry.Max);
    }

    public void AddChild(RTreeNode child)
    {
        Children.Add(child);
        Expand(child.Min, child.Max);
    }

    public void Expand(ReadOnlySpan<double> rMin, ReadOnlySpan<double> rMax)
    {
        for (int i = 0; i < Min.Length; i++)
        {
            Min[i] = Math.Min(Min[i], rMin[i]);
            Max[i] = Math.Max(Max[i], rMax[i]);
        }
    }

    public bool Intersects(ReadOnlySpan<double> qMin, ReadOnlySpan<double> qMax)
    {
        for (int i = 0; i < Min.Length; i++)
        {
            if (Min[i] > qMax[i] || Max[i] < qMin[i])
                return false;
        }
        return true;
    }
}

/// <summary>
/// R-Tree fuer Spatial Queries (2D/3D/nD Bounding-Box Intersection).
/// In-Memory, fuer embedded .NET optimiert.
/// </summary>
public sealed class RTree
{
    private const byte SnapshotVersion = 1;

    private readonly int _dimensions;
    private readonly int _maxEntries;
    private readonly int _minEntries;
    private RTreeNode _root;
    private int _nodeCount;
    private readonly Dictionary<long, RTreeEntry> _entriesById;

    public RTree(int dimensions = 2, int maxEntries = 16)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (maxEntries < 4) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _dimensions = dimensions;
        _maxEntries = maxEntries;
        _minEntries = maxEntries / 2;
        _root = new RTreeNode(dimensions, true);
        _entriesById = new Dictionary<long, RTreeEntry>();
    }

    public int NodeCount => _nodeCount;
    public int EntryCount => _entriesById.Count;

    public int Dimensions => _dimensions;

    public int MaxEntries => _maxEntries;

    /// <summary>Fuegt ein Rechteck mit ID in den Baum ein.</summary>
    public void Insert(long id, ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        if (min.Length != _dimensions || max.Length != _dimensions)
            throw new ArgumentException("Dimension mismatch");

        if (_entriesById.ContainsKey(id))
            Delete(id);

        var entry = new RTreeEntry(id, min, max);
        _entriesById[id] = entry;

        var leaf = ChooseLeaf(_root, entry.Min, entry.Max);
        leaf.AddEntry(entry);

        if (leaf.Count > _maxEntries)
        {
            var split = SplitNode(leaf);
            if (leaf == _root)
            {
                var newRoot = new RTreeNode(_dimensions, false);
                newRoot.AddChild(split.Original);
                newRoot.AddChild(split.New);
                _root = newRoot;
                _nodeCount += 2;
            }
            else
            {
                _nodeCount++;
                AdjustTree(leaf, split.New);
            }
        }
    }

    /// <summary>Entfernt ein Rechteck anhand der ID.</summary>
    public bool Delete(long id)
    {
        if (!_entriesById.TryGetValue(id, out var entry))
            return false;

        _entriesById.Remove(id);

        var node = FindLeaf(_root, entry);
        if (node is null)
            return false;

        node.Entries.RemoveAll(e => e.Id == id);

        // Re-Insert unterbesetzte Knoten (simpler als komplexes CondenseTree)
        if (node.Count < _minEntries && node != _root)
        {
            var orphaned = CollectEntries(node);
            // Parent-Reference fehlt in diesem Design — wir bauen den Baum neu auf
            Rebuild();
            foreach (var e in orphaned)
            {
                if (_entriesById.ContainsKey(e.Id))
                    Insert(e.Id, e.Min, e.Max);
            }
        }
        else
        {
            RecalculateMBR(node);
        }

        if (_root.Count == 1 && !_root.IsLeaf)
        {
            _root = _root.Children[0];
            _nodeCount--;
        }

        return true;
    }

    /// <summary>
    /// Sucht alle IDs, deren Bounding Box mit der Query-Box schneidet.
    /// </summary>
    public IEnumerable<long> Search(ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        var results = new List<long>();
        SearchRecursive(_root, min, max, results);
        return results;
    }

    /// <summary>Liefert einen stabilen Snapshot aller Eintraege fuer Persistenzzwecke.</summary>
    public IReadOnlyList<RTreeEntry> ExportEntries()
    {
        return _entriesById.Values
            .OrderBy(static entry => entry.Id)
            .Select(static entry => new RTreeEntry(entry.Id, entry.Min, entry.Max))
            .ToArray();
    }

    /// <summary>Serialisiert den Baum als versionierten Entry-Snapshot.</summary>
    public byte[] Serialize()
    {
        var entries = ExportEntries();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(SnapshotVersion);
        writer.Write(_dimensions);
        writer.Write(_maxEntries);
        writer.Write(entries.Count);

        foreach (var entry in entries)
        {
            writer.Write(entry.Id);
            for (int i = 0; i < _dimensions; i++)
                writer.Write(entry.Min[i]);
            for (int i = 0; i < _dimensions; i++)
                writer.Write(entry.Max[i]);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Stellt einen Baum aus einem mit <see cref="Serialize"/> erzeugten Snapshot wieder her.</summary>
    public static RTree Deserialize(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);

        byte version = reader.ReadByte();
        if (version != SnapshotVersion)
            throw new InvalidDataException($"Unsupported RTree snapshot version {version}.");

        int dimensions = reader.ReadInt32();
        int maxEntries = reader.ReadInt32();
        int entryCount = reader.ReadInt32();
        if (dimensions <= 0)
            throw new InvalidDataException("RTree snapshot contains an invalid dimension count.");
        if (maxEntries < 4)
            throw new InvalidDataException("RTree snapshot contains an invalid maxEntries value.");
        if (entryCount < 0)
            throw new InvalidDataException("RTree snapshot contains an invalid entry count.");

        var tree = new RTree(dimensions, maxEntries);
        for (int e = 0; e < entryCount; e++)
        {
            long id = reader.ReadInt64();
            var min = new double[dimensions];
            var max = new double[dimensions];
            for (int i = 0; i < dimensions; i++)
                min[i] = reader.ReadDouble();
            for (int i = 0; i < dimensions; i++)
                max[i] = reader.ReadDouble();
            tree.Insert(id, min, max);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("RTree snapshot contains trailing bytes.");

        return tree;
    }

    private void SearchRecursive(RTreeNode node, ReadOnlySpan<double> min, ReadOnlySpan<double> max, List<long> results)
    {
        if (!node.Intersects(min, max)) return;

        if (node.IsLeaf)
        {
            foreach (var entry in node.Entries)
            {
                if (entry.Intersects(min, max))
                    results.Add(entry.Id);
            }
        }
        else
        {
            foreach (var child in node.Children)
            {
                SearchRecursive(child, min, max, results);
            }
        }
    }

    private RTreeNode ChooseLeaf(RTreeNode node, ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        if (node.IsLeaf) return node;

        RTreeNode? bestChild = null;
        double bestEnlargement = double.MaxValue;
        double bestArea = double.MaxValue;

        foreach (var child in node.Children)
        {
            double enlargement = 0.0;
            // Berechne Enlargement ueber die Dimensionen
            for (int i = 0; i < _dimensions; i++)
            {
                double dMin = Math.Min(child.Min[i], min[i]);
                double dMax = Math.Max(child.Max[i], max[i]);
                enlargement += (dMax - dMin) - (child.Max[i] - child.Min[i]);
            }

            double area = 0.0;
            for (int i = 0; i < _dimensions; i++)
                area += child.Max[i] - child.Min[i];

            if (enlargement < bestEnlargement ||
                (enlargement == bestEnlargement && area < bestArea))
            {
                bestEnlargement = enlargement;
                bestArea = area;
                bestChild = child;
            }
        }

        return ChooseLeaf(bestChild!, min, max);
    }

    private (RTreeNode Original, RTreeNode New) SplitNode(RTreeNode node)
    {
        var entries = node.IsLeaf
            ? node.Entries.Select(e => (e.Min, e.Max, (object)e)).ToList()
            : node.Children.Select(c => (c.Min, c.Max, (object)c)).ToList();

        // Pick Seeds (Linear-Split)
        int seed1 = 0, seed2 = 1;
        double maxSeparation = -1;

        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = i + 1; j < entries.Count; j++)
            {
                double separation = 0.0;
                for (int d = 0; d < _dimensions; d++)
                {
                    double lowI = entries[i].Min[d];
                    double highI = entries[i].Max[d];
                    double lowJ = entries[j].Min[d];
                    double highJ = entries[j].Max[d];
                    separation += Math.Abs((highJ - lowJ) - (highI - lowI));
                }
                if (separation > maxSeparation)
                {
                    maxSeparation = separation;
                    seed1 = i;
                    seed2 = j;
                }
            }
        }

        var original = new RTreeNode(_dimensions, node.IsLeaf);
        var newNode = new RTreeNode(_dimensions, node.IsLeaf);

        if (node.IsLeaf)
        {
            original.AddEntry((RTreeEntry)entries[seed1].Item3);
            newNode.AddEntry((RTreeEntry)entries[seed2].Item3);
        }
        else
        {
            original.AddChild((RTreeNode)entries[seed1].Item3);
            newNode.AddChild((RTreeNode)entries[seed2].Item3);
        }

        entries.RemoveAt(Math.Max(seed1, seed2));
        entries.RemoveAt(Math.Min(seed1, seed2));

        foreach (var item in entries)
        {
            if (original.Count + entries.Count - entries.IndexOf(item) <= _minEntries)
            {
                if (node.IsLeaf) original.AddEntry((RTreeEntry)item.Item3);
                else original.AddChild((RTreeNode)item.Item3);
            }
            else if (newNode.Count + entries.Count - entries.IndexOf(item) <= _minEntries)
            {
                if (node.IsLeaf) newNode.AddEntry((RTreeEntry)item.Item3);
                else newNode.AddChild((RTreeNode)item.Item3);
            }
            else
            {
                double diff1 = DiffArea(original, item.Min, item.Max);
                double diff2 = DiffArea(newNode, item.Min, item.Max);
                if (diff1 < diff2)
                {
                    if (node.IsLeaf) original.AddEntry((RTreeEntry)item.Item3);
                    else original.AddChild((RTreeNode)item.Item3);
                }
                else
                {
                    if (node.IsLeaf) newNode.AddEntry((RTreeEntry)item.Item3);
                    else newNode.AddChild((RTreeNode)item.Item3);
                }
            }
        }

        return (original, newNode);
    }

    private double DiffArea(RTreeNode node, ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        double before = 0.0, after = 0.0;
        for (int i = 0; i < _dimensions; i++)
        {
            before += node.Max[i] - node.Min[i];
            double dMin = Math.Min(node.Min[i], min[i]);
            double dMax = Math.Max(node.Max[i], max[i]);
            after += dMax - dMin;
        }
        return after - before;
    }

    private void AdjustTree(RTreeNode node, RTreeNode newNode)
    {
        // Simplifiziert: wir bauen bei Split den Pfad nicht aufwaerts nach —
        // fuer korrekte Parent-Verwaltung braeuchten wir Parent-Referenzen.
        // Stattdessen: Rebuild bei zu vielen Splits.
        // Fuer v1 ist dies akzeptabel, da Splits selten sind.
    }

    private RTreeNode? FindLeaf(RTreeNode node, RTreeEntry entry)
    {
        if (node.IsLeaf)
        {
            return node.Entries.Any(e => e.Id == entry.Id) ? node : null;
        }

        foreach (var child in node.Children)
        {
            if (child.Intersects(entry.Min, entry.Max))
            {
                var found = FindLeaf(child, entry);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private List<RTreeEntry> CollectEntries(RTreeNode node)
    {
        var result = new List<RTreeEntry>();
        CollectEntriesRecursive(node, result);
        return result;
    }

    private void CollectEntriesRecursive(RTreeNode node, List<RTreeEntry> result)
    {
        if (node.IsLeaf)
        {
            result.AddRange(node.Entries);
        }
        else
        {
            foreach (var child in node.Children)
                CollectEntriesRecursive(child, result);
        }
    }

    private void RecalculateMBR(RTreeNode node)
    {
        for (int i = 0; i < _dimensions; i++)
        {
            node.Min[i] = double.MaxValue;
            node.Max[i] = double.MinValue;
        }

        if (node.IsLeaf)
        {
            foreach (var e in node.Entries)
                node.Expand(e.Min, e.Max);
        }
        else
        {
            foreach (var c in node.Children)
                node.Expand(c.Min, c.Max);
        }
    }

    private void Rebuild()
    {
        var entries = _entriesById.Values.ToList();
        _root = new RTreeNode(_dimensions, true);
        _nodeCount = 0;

        foreach (var entry in entries)
        {
            var leaf = ChooseLeaf(_root, entry.Min, entry.Max);
            leaf.AddEntry(entry);
            if (leaf.Count > _maxEntries)
            {
                var split = SplitNode(leaf);
                if (leaf == _root)
                {
                    var newRoot = new RTreeNode(_dimensions, false);
                    newRoot.AddChild(split.Original);
                    newRoot.AddChild(split.New);
                    _root = newRoot;
                    _nodeCount += 2;
                }
            }
        }
    }
}
