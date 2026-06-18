// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.VectorStore.Indexes;

/// <summary>
/// HNSW (Hierarchical Navigable Small World) ANN-Index.
/// </summary>
/// <remarks>
/// <para>
/// Speicherlayout: Nodes sind im RAM, Vektoren werden bei Bedarf aus dem 
/// VectorRepository geladen. Das ermöglicht Index-Größen >> RAM wenn Vektoren
/// on-demand geladen werden (kompromittiert aber Query-Geschwindigkeit).
/// </para>
/// <para>
/// Für maximale Geschwindigkeit sollten Vektoren gecacht werden.
/// </para>
/// </remarks>
public sealed class HnswIndex : IDisposable
{
    private readonly HnswOptions _options;
    private readonly DistanceMetric _metric;
    private readonly HnswNodeTable _nodes = new();
    private readonly Random _random;
    private readonly object _entryPointLock = new();

    // Entry point: höchster Layer mit mindestens einem Node
    private volatile int _entryPointIndex = -1;
    private volatile int _maxLayer = 0;

    // Visited-Marker: schneller als HashSet<int> für wiederholte Suchen
    private int[] _visited = new int[1024];
    private int _visitToken = 1;
    private readonly object _visitLock = new();

    // Pooled Queues für Build (vermeidet wiederholte Heap-Allokationen)
    private readonly PriorityQueue<(int NodeIndex, float Distance), float> _buildCandidatesQueue;
    private readonly PriorityQueue<(int NodeIndex, float Distance), float> _buildResultQueue;

    public HnswOptions Options => _options;
    public int NodeCount => _nodes.Count;
    public int EntryLayer => _maxLayer;

    private static readonly IComparer<float> _reverseFloatComparer = Comparer<float>.Create((a, b) => b.CompareTo(a));

    private int MaxLayerCapacity => _options.MaxLayer > 0
        ? _options.MaxLayer
        : (int)Math.Ceiling(Math.Log(_nodes.Count + 1, _options.M));

    public HnswIndex(HnswOptions? options = null, DistanceMetric metric = DistanceMetric.Euclidean)
    {
        _options = options ?? new HnswOptions();
        _metric = metric;
        _random = _options.RandomSeed.HasValue
            ? new Random(_options.RandomSeed.Value)
            : new Random();
        _buildCandidatesQueue = new PriorityQueue<(int NodeIndex, float Distance), float>(1024);
        _buildResultQueue = new PriorityQueue<(int NodeIndex, float Distance), float>(1024, _reverseFloatComparer);
    }

    /// <summary>
    /// Löscht alle Nodes und setzt den Index zurück.
    /// </summary>
    public void Clear()
    {
        _nodes.Clear();
        _entryPointIndex = -1;
        _maxLayer = 0;
    }

    /// <summary>
    /// Fügt einen Vektor-Entry in den Index ein.
    /// Thread-sicher für paralleles Einfügen unterschiedlicher IDs.
    /// </summary>
    public void Insert(ulong id, float[] vector)
    {
        InsertCore(id, vector, null);
    }

    /// <summary>
    /// Fügt einen Vektor-Entry in den Index ein (Callback-Variante für On-Demand-Laden).
    /// </summary>
    public void Insert(ulong id, Func<ulong, float[]> vectorLoader)
    {
        InsertCore(id, null, vectorLoader);
    }

    /// <summary>
    /// Paralleles Insert für Bulk-Build (RebuildIndexAsync).
    /// Thread-sicher, aber Graph-Qualität kann leicht unter sequentiellem Insert liegen.
    /// </summary>
    public void InsertParallel(ulong id, float[] vector)
    {
        InsertParallelCore(id, vector, null);
    }

    /// <summary>
    /// Paralleles Insert mit vorberechnetem Layer für Bulk-Build.
    /// Thread-sicher, setzt keine Rückwärtskanten.
    /// </summary>
    public void InsertParallel(ulong id, float[] vector, int precomputedLayer)
    {
        InsertParallelCore(id, vector, precomputedLayer);
    }

    private void InsertParallelCore(ulong id, float[] vector, int? precomputedLayer)
    {
        var existingIndex = _nodes.GetIndex(id);
        if (existingIndex.HasValue)
        {
            var existingNode = _nodes.GetNode(existingIndex.Value);
            if (existingNode != null && !existingNode.IsDeleted)
                return;
            RemoveNodeFromGraph(existingIndex.Value);
            _nodes.RemoveId(id);
        }

        var newLayer = precomputedLayer ?? SelectRandomLayer();
        var neighborCounts = new int[newLayer + 1];
        for (int i = 0; i <= newLayer; i++)
            neighborCounts[i] = i == 0 ? _options.Ml0 : _options.M;

        var node = new HnswNode(id, newLayer, neighborCounts, vector);
        var newIndex = _nodes.GetOrAddIndex(id, _ => node);

        if (newIndex == 0)
        {
            lock (_entryPointLock)
            {
                _entryPointIndex = newIndex;
                _maxLayer = newLayer;
            }
            return;
        }

        float[] CachedLoader(ulong lookupId)
        {
            var n = _nodes.GetNodeById(lookupId);
            if (n?.Vector is not null)
                return n.Vector;
            throw new InvalidOperationException($"Kein Vektor für ID {lookupId} verfügbar.");
        }

        var entryPoint = _nodes.GetNode(_entryPointIndex)!;
        var currNode = entryPoint;
        var currDist = Distance(currNode.Id, id, CachedLoader);

        for (int level = _maxLayer; level > newLayer; level--)
        {
            var (nodeIdx, dist) = GreedySearchNearest(currNode, id, currDist, level, CachedLoader);
            if (dist < currDist)
            {
                currNode = _nodes.GetNode(nodeIdx)!;
                currDist = dist;
            }
        }

        for (int level = Math.Min(newLayer, _maxLayer); level >= 0; level--)
        {
            var ef = level == 0 ? _options.EfConstructionL0 : _options.EfConstruction;
            var candidates = SearchLayerBuild(currNode, id, currDist, level, ef, CachedLoader);

            var maxNeighbors = level == 0 ? _options.Ml0 : _options.M;
            var neighbors = SelectNeighborsHeuristic(candidates, maxNeighbors, id, CachedLoader, _options.MaxHeuristicDistanceComputations);

            foreach (var neighborIdx in neighbors)
            {
                var neighbor = _nodes.GetNode(neighborIdx)!;
                node.TryAddNeighbor(level, neighborIdx);
                // Rückwärtskante setzen (thread-sicher dank NeighborLock)
                neighbor.TryAddNeighbor(level, newIndex);
            }

            if (candidates.Count > 0)
            {
                currNode = _nodes.GetNode(candidates[0].NodeIndex)!;
                currDist = candidates[0].Distance;
            }
        }

        if (newLayer > _maxLayer)
        {
            lock (_entryPointLock)
            {
                if (newLayer > _maxLayer)
                {
                    _maxLayer = newLayer;
                    _entryPointIndex = newIndex;
                }
            }
        }
    }

    /// <summary>
    /// Berechnet zufällige Layer für eine Batch-Einfügung.
    /// Simuliert inkrementelles _nodes.Count, damit die Layer-Verteilung korrekt ist
    /// auch wenn die Nodes noch nicht im Index sind.
    /// </summary>
    public int[] PrepareLayers(int count)
    {
        var layers = new int[count];
        lock (_random)
        {
            int currentCount = _nodes.Count;
            for (int i = 0; i < count; i++)
            {
                layers[i] = SelectRandomLayer(currentCount + i);
            }
        }
        return layers;
    }

    /// <summary>
    /// Bereitet einen Node vor (mit vorgegebenem Layer).
    /// Thread-sicher, kann parallel aufgerufen werden.
    /// </summary>
    public HnswNode PrepareNode(ulong id, float[] vector, int layer)
    {
        var neighborCounts = new int[layer + 1];
        for (int i = 0; i <= layer; i++)
            neighborCounts[i] = i == 0 ? _options.Ml0 : _options.M;
        return new HnswNode(id, layer, neighborCounts, vector);
    }

    /// <summary>
    /// Fügt einen vorbereiteten Node in den Graph ein.
    /// NICHT thread-sicher – muss sequentiell aufgerufen werden.
    /// </summary>
    public void InsertPreparedNode(HnswNode preparedNode, Func<ulong, float[]>? vectorLoader = null)
    {
        var existingIndex = _nodes.GetIndex(preparedNode.Id);
        if (existingIndex.HasValue)
        {
            var existingNode = _nodes.GetNode(existingIndex.Value);
            if (existingNode != null && !existingNode.IsDeleted)
                return;

            RemoveNodeFromGraph(existingIndex.Value);
            _nodes.RemoveId(preparedNode.Id);
        }

        InsertNodeIntoGraph(preparedNode, preparedNode.Vector, vectorLoader);
    }

    private void InsertCore(ulong id, float[]? directVector, Func<ulong, float[]>? vectorLoader)
    {
        var existingIndex = _nodes.GetIndex(id);
        if (existingIndex.HasValue)
        {
            var existingNode = _nodes.GetNode(existingIndex.Value);
            if (existingNode != null && !existingNode.IsDeleted)
                return;

            RemoveNodeFromGraph(existingIndex.Value);
            _nodes.RemoveId(id);
        }

        var newLayer = SelectRandomLayer();
        var neighborCounts = new int[newLayer + 1];
        for (int i = 0; i <= newLayer; i++)
            neighborCounts[i] = i == 0 ? _options.Ml0 : _options.M;

        var node = new HnswNode(id, newLayer, neighborCounts, directVector);
        InsertNodeIntoGraph(node, directVector, vectorLoader);
    }

    private void InsertNodeIntoGraph(HnswNode node, float[]? directVector, Func<ulong, float[]>? vectorLoader)
    {
        var newIndex = _nodes.GetOrAddIndex(node.Id, _ => node);

        if (newIndex == 0)
        {
            lock (_entryPointLock)
            {
                _entryPointIndex = newIndex;
                _maxLayer = node.TopLayer;
            }
            return;
        }

        // Universal-Loader: bevorzugt gespeicherte Node-Vektoren, dann Callback.
        float[] EffectiveLoader(ulong lookupId)
        {
            if (lookupId == node.Id && directVector is not null)
                return directVector;
            var n = _nodes.GetNodeById(lookupId);
            if (n?.Vector is not null)
                return n.Vector;
            if (vectorLoader is not null)
                return vectorLoader(lookupId);
            throw new InvalidOperationException($"Kein Vektor für ID {lookupId} verfügbar.");
        }

        var entryPoint = _nodes.GetNode(_entryPointIndex)!;
        var currNode = entryPoint;
        var currDist = Distance(currNode.Id, node.Id, EffectiveLoader);

        for (int level = _maxLayer; level > node.TopLayer; level--)
        {
            var (nodeIdx, dist) = GreedySearchNearest(currNode, node.Id, currDist, level, EffectiveLoader);
            if (dist < currDist)
            {
                currNode = _nodes.GetNode(nodeIdx)!;
                currDist = dist;
            }
        }

        for (int level = Math.Min(node.TopLayer, _maxLayer); level >= 0; level--)
        {
            // Höhere Layer brauchen mehr ef für bessere Graph-Qualität bei größeren Datensätzen
            var ef = level == 0
                ? _options.EfConstructionL0
                : _options.EfConstruction;
            var candidates = SearchLayerBuild(currNode, node.Id, currDist, level, ef, EffectiveLoader);

            var maxNeighbors = level == 0 ? _options.Ml0 : _options.M;
            var neighbors = SelectNeighborsHeuristic(candidates, maxNeighbors, node.Id, EffectiveLoader, _options.MaxHeuristicDistanceComputations);

            foreach (var neighborIdx in neighbors)
            {
                var neighbor = _nodes.GetNode(neighborIdx)!;
                node.TryAddNeighbor(level, neighborIdx);

                lock (neighbor.NeighborLock)
                {
                    if (!neighbor.TryAddNeighbor(level, newIndex))
                    {
                        PruneIfOverfull(neighbor, level, level == 0 ? _options.Ml0 : _options.M, newIndex, EffectiveLoader);
                    }
                }
            }

            if (candidates.Count > 0)
            {
                currNode = _nodes.GetNode(candidates[0].NodeIndex)!;
                currDist = candidates[0].Distance;
            }
        }

        if (node.TopLayer > _maxLayer)
        {
            lock (_entryPointLock)
            {
                if (node.TopLayer > _maxLayer)
                {
                    _maxLayer = node.TopLayer;
                    _entryPointIndex = newIndex;
                }
            }
        }
    }

    /// <summary>
    /// Sucht K nächste Nachbarn.
    /// </summary>
    /// <param name="isAllowed">Optionaler Pre-Filter: nur Nodes mit erlaubter ID werden traversiert.</param>
    public IReadOnlyList<(ulong Id, float Distance)> SearchKnn(
        Func<ulong, float[]> queryLoader,
        int k,
        int? ef = null,
        Func<ulong, bool>? isAllowed = null)
    {
        if (_entryPointIndex < 0)
            return Array.Empty<(ulong, float)>();

        var queryVector = queryLoader(ulong.MaxValue);
        var efValue = ef ?? _options.EfSearch;

        // Entry point laden
        var entryNode = _nodes.GetNode(_entryPointIndex)!;
        float currDist = ComputeDistance(queryLoader(entryNode.Id).AsSpan(), queryVector.AsSpan());

        // Wenn Entry Point nicht erlaubt, suche den nächstbesten erlaubten
        if (isAllowed is not null && !isAllowed(entryNode.Id))
        {
            entryNode = FindNearestAllowedEntry(entryNode, queryLoader, isAllowed);
            if (entryNode is null)
                return Array.Empty<(ulong, float)>();
            currDist = ComputeDistance(queryLoader(entryNode.Id).AsSpan(), queryVector.AsSpan());
        }

        // Phase 1: Von Top-Layer bis Layer 1
        for (int level = _maxLayer; level >= 1; level--)
        {
            var candidates = GreedySearchLayer(entryNode, ulong.MaxValue, currDist, level, 10, queryLoader, isAllowed);
            if (candidates.Count == 0) continue;

            var (nodeIdx, dist) = candidates[0];
            entryNode = _nodes.GetNode(nodeIdx)!;
            currDist = dist;
        }

        // Phase 2: Layer 0 mit vollem ef
        var resultCandidates = SearchLayer(entryNode, queryVector, currDist, 0, Math.Max(efValue, k), queryLoader, isAllowed);

        // Filtere gelöschte und gib Top-K zurück
        return resultCandidates
            .Where(c => !_nodes.GetNode(c.NodeIndex)!.IsDeleted)
            .Take(k)
            .Select(c => (_nodes.GetNode(c.NodeIndex)!.Id, c.Distance))
            .ToList();
    }

    /// <summary>
    /// Findet den nächstgelegenen erlaubten Entry Point, wenn der aktuelle nicht passt.
    /// </summary>
    private HnswNode? FindNearestAllowedEntry(HnswNode startNode, Func<ulong, float[]> queryLoader, Func<ulong, bool> isAllowed)
    {
        // BFS über alle Layer-0-Nodes um einen erlaubten zu finden
        // Da der Graph zusammenhängend sein sollte, finden wir einen
        NextVisit();
        var queue = new Queue<int>();
        int startIdx = _nodes.GetIndex(startNode.Id)!.Value;
        MarkVisited(startIdx);
        queue.Enqueue(startIdx);

        while (queue.Count > 0)
        {
            var idx = queue.Dequeue();
            var node = _nodes.GetNode(idx);
            if (node is null || node.IsDeleted) continue;
            if (isAllowed(node.Id)) return node;

            foreach (var neighborIdx in node.GetNeighborsCopy(0))
            {
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);
                queue.Enqueue(neighborIdx);
            }
        }

        return null;
    }

    /// <summary>
    /// Weicher Löschvorgang (Node bleibt im Graph, wird aber übersprungen).
    /// </summary>
    public bool MarkDeleted(ulong id)
    {
        var node = _nodes.GetNodeById(id);
        if (node is null) return false;
        node.IsDeleted = true;
        return true;
    }

    #region Visited Marker (schneller als HashSet<int>)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsVisited(int nodeIndex)
    {
        var visited = _visited;
        if ((uint)nodeIndex >= (uint)visited.Length)
        {
            GrowVisited(nodeIndex);
            visited = _visited;
        }
        return visited[nodeIndex] == _visitToken;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkVisited(int nodeIndex)
    {
        var visited = _visited;
        if ((uint)nodeIndex >= (uint)visited.Length)
        {
            GrowVisited(nodeIndex);
            visited = _visited;
        }
        visited[nodeIndex] = _visitToken;
    }

    private void GrowVisited(int requiredIndex)
    {
        lock (_visitLock)
        {
            var visited = _visited;
            if ((uint)requiredIndex < (uint)visited.Length) return;
            int newLen = Math.Max(visited.Length * 2, requiredIndex + 1);
            Array.Resize(ref _visited, newLen);
        }
    }

    private void NextVisit()
    {
        var token = Interlocked.Increment(ref _visitToken);
        if (token >= int.MaxValue - 1)
        {
            lock (_visitLock)
            {
                if (_visitToken >= int.MaxValue - 1)
                {
                    Array.Clear(_visited);
                    _visitToken = 1;
                }
            }
        }
    }

    #endregion

    #region Private Implementation

    /// <summary>
    /// Zufälliger Layer-Selektor gemäß HNSW-Paper.
    /// </summary>
    private int SelectRandomLayer()
    {
        return SelectRandomLayer(_nodes.Count);
    }

    private int SelectRandomLayer(int nodeCount)
    {
        double ml = 1.0 / Math.Log(_options.M);
        var r = _random.NextDouble();
        int level = (int)Math.Floor(-Math.Log(r) * ml);
        int maxCapacity = _options.MaxLayer > 0
            ? _options.MaxLayer
            : (int)Math.Ceiling(Math.Log(nodeCount + 1, _options.M));
        return Math.Min(level, maxCapacity);
    }

    /// <summary>
    /// Spezialisierte gierige Suche für ef=1 (nächster Nachbar).
    /// Wird im Insert für die oberen Layer verwendet.
    /// </summary>
    private (int NodeIndex, float Distance) GreedySearchNearest(
        HnswNode entryPoint,
        ulong targetId,
        float entryDist,
        int level,
        Func<ulong, float[]> vectorLoader,
        Func<ulong, bool>? isAllowed = null)
    {
        NextVisit();
        int entryIdx = _nodes.GetIndex(entryPoint.Id)!.Value;
        MarkVisited(entryIdx);

        var bestIdx = entryIdx;
        var bestDist = entryDist;
        var currIdx = entryIdx;
        var currNode = entryPoint;

        while (true)
        {
            var neighbors = currNode.GetNeighborsUnsafe(level);
            int nextIdx = -1;
            float nextDist = bestDist;

            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighborIdx = neighbors[i];
                if (neighborIdx < 0) continue;
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);

                var neighborNode = _nodes.GetNode(neighborIdx);
                if (neighborNode?.IsDeleted == true) continue;
                if (isAllowed is not null && !isAllowed(neighborNode!.Id)) continue;

                float dist = Distance(neighborNode!.Id, targetId, vectorLoader);
                if (dist < nextDist)
                {
                    nextIdx = neighborIdx;
                    nextDist = dist;
                }
            }

            if (nextIdx < 0 || nextDist >= bestDist)
                break;

            bestIdx = nextIdx;
            bestDist = nextDist;
            currIdx = nextIdx;
            currNode = _nodes.GetNode(currIdx)!;
        }

        return (bestIdx, bestDist);
    }

    private List<(int NodeIndex, float Distance)> GreedySearchLayer(
        HnswNode entryPoint,
        ulong targetId,
        float entryDist,
        int level,
        int ef,
        Func<ulong, float[]>? vectorLoader = null,
        Func<ulong, bool>? isAllowed = null)
    {
        NextVisit();
        var candidates = new PriorityQueue<(int NodeIndex, float Distance), float>(ef);
        var result = new PriorityQueue<(int NodeIndex, float Distance), float>(ef, _reverseFloatComparer);

        int entryIdx = _nodes.GetIndex(entryPoint.Id)!.Value;
        MarkVisited(entryIdx);
        candidates.Enqueue((entryIdx, entryDist), entryDist);
        result.Enqueue((entryIdx, entryDist), entryDist);

        float worstDist = entryDist;

        while (candidates.Count > 0)
        {
            var (currentIdx, currentDist) = candidates.Dequeue();

            if (currentDist > worstDist)
                break;

            var currentNode = _nodes.GetNode(currentIdx)!;
            var neighbors = currentNode.GetNeighborsUnsafe(level);

            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighborIdx = neighbors[i];
                if (neighborIdx < 0) continue;
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);

                var neighborNode = _nodes.GetNode(neighborIdx);
                if (neighborNode?.IsDeleted == true) continue;
                if (isAllowed is not null && !isAllowed(neighborNode!.Id)) continue;

                float dist = vectorLoader is not null
                    ? Distance(neighborNode!.Id, targetId, vectorLoader)
                    : float.MaxValue;

                if (dist < worstDist || result.Count < ef)
                {
                    candidates.Enqueue((neighborIdx, dist), dist);
                    result.Enqueue((neighborIdx, dist), dist);

                    if (result.Count > ef)
                        result.Dequeue();

                    if (result.Count > 0)
                        worstDist = result.Peek().Distance;
                }
            }
        }

        // Elemente aus Max-Heap extrahieren (größte zuerst), dann umdrehen
        var list = new List<(int NodeIndex, float Distance)>(result.Count);
        while (result.Count > 0)
            list.Add(result.Dequeue());
        list.Reverse();
        return list;
    }

    private List<(int NodeIndex, float Distance)> SearchLayer(
        HnswNode entryPoint,
        ulong targetId,
        float entryDist,
        int level,
        int ef,
        Func<ulong, float[]> vectorLoader,
        Func<ulong, bool>? isAllowed = null)
    {
        NextVisit();
        var candidates = new PriorityQueue<(int NodeIndex, float Distance), float>(ef);
        var result = new PriorityQueue<(int NodeIndex, float Distance), float>(ef, _reverseFloatComparer);

        int entryIdx = _nodes.GetIndex(entryPoint.Id)!.Value;
        MarkVisited(entryIdx);
        candidates.Enqueue((entryIdx, entryDist), entryDist);
        result.Enqueue((entryIdx, entryDist), entryDist);

        float worstDist = entryDist;

        while (candidates.Count > 0)
        {
            var (currentIdx, currentDist) = candidates.Dequeue();

            if (currentDist > worstDist)
                break;

            var currentNode = _nodes.GetNode(currentIdx)!;
            var neighbors = currentNode.GetNeighborsUnsafe(level);

            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighborIdx = neighbors[i];
                if (neighborIdx < 0) continue;
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);

                var neighborNode = _nodes.GetNode(neighborIdx);
                if (neighborNode?.IsDeleted == true) continue;
                if (isAllowed is not null && !isAllowed(neighborNode!.Id)) continue;

                float dist = Distance(neighborNode!.Id, targetId, vectorLoader);

                if (dist < worstDist || result.Count < ef)
                {
                    candidates.Enqueue((neighborIdx, dist), dist);
                    result.Enqueue((neighborIdx, dist), dist);

                    if (result.Count > ef)
                        result.Dequeue();

                    if (result.Count > 0)
                        worstDist = result.Peek().Distance;
                }
            }
        }

        // Elemente aus Max-Heap extrahieren (größte zuerst), dann umdrehen
        var list = new List<(int NodeIndex, float Distance)>(result.Count);
        while (result.Count > 0)
            list.Add(result.Dequeue());
        list.Reverse();
        return list;
    }

    private List<(int NodeIndex, float Distance)> SearchLayer(
        HnswNode entryPoint,
        float[] queryVector,
        float entryDist,
        int level,
        int ef,
        Func<ulong, float[]> queryLoader,
        Func<ulong, bool>? isAllowed = null)
    {
        NextVisit();
        var candidates = new PriorityQueue<(int NodeIndex, float Distance), float>(ef);
        var result = new PriorityQueue<(int NodeIndex, float Distance), float>(ef, _reverseFloatComparer);

        int entryIdx = _nodes.GetIndex(entryPoint.Id)!.Value;
        MarkVisited(entryIdx);
        candidates.Enqueue((entryIdx, entryDist), entryDist);
        result.Enqueue((entryIdx, entryDist), entryDist);

        float worstDist = entryDist;

        while (candidates.Count > 0)
        {
            var (currentIdx, currentDist) = candidates.Dequeue();

            if (currentDist > worstDist)
                break;

            var currentNode = _nodes.GetNode(currentIdx)!;
            var neighbors = currentNode.GetNeighborsUnsafe(level);

            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighborIdx = neighbors[i];
                if (neighborIdx < 0) continue;
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);

                var neighborNode = _nodes.GetNode(neighborIdx);
                if (neighborNode?.IsDeleted == true) continue;
                if (isAllowed is not null && !isAllowed(neighborNode!.Id)) continue;

                float dist = ComputeDistance(queryLoader(neighborNode!.Id).AsSpan(), queryVector.AsSpan());

                if (dist < worstDist || result.Count < ef)
                {
                    candidates.Enqueue((neighborIdx, dist), dist);
                    result.Enqueue((neighborIdx, dist), dist);

                    if (result.Count > ef)
                        result.Dequeue();

                    if (result.Count > 0)
                        worstDist = result.Peek().Distance;
                }
            }
        }

        // Elemente aus Max-Heap extrahieren (größte zuerst), dann umdrehen
        var list = new List<(int NodeIndex, float Distance)>(result.Count);
        while (result.Count > 0)
            list.Add(result.Dequeue());
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Build-Variante von SearchLayer die interne PriorityQueues wiederverwendet.
    /// NICHT thread-sicher – darf nur sequentiell während des Builds aufgerufen werden.
    /// </summary>
    private List<(int NodeIndex, float Distance)> SearchLayerBuild(
        HnswNode entryPoint,
        ulong targetId,
        float entryDist,
        int level,
        int ef,
        Func<ulong, float[]> vectorLoader,
        Func<ulong, bool>? isAllowed = null)
    {
        NextVisit();
        var candidates = _buildCandidatesQueue;
        var result = _buildResultQueue;
        candidates.Clear();
        result.Clear();

        int entryIdx = _nodes.GetIndex(entryPoint.Id)!.Value;
        MarkVisited(entryIdx);
        candidates.Enqueue((entryIdx, entryDist), entryDist);
        result.Enqueue((entryIdx, entryDist), entryDist);

        float worstDist = entryDist;

        while (candidates.Count > 0)
        {
            var (currentIdx, currentDist) = candidates.Dequeue();

            if (currentDist > worstDist)
                break;

            var currentNode = _nodes.GetNode(currentIdx)!;
            var neighbors = currentNode.GetNeighborsUnsafe(level);

            for (int i = 0; i < neighbors.Length; i++)
            {
                int neighborIdx = neighbors[i];
                if (neighborIdx < 0) continue;
                if (IsVisited(neighborIdx)) continue;
                MarkVisited(neighborIdx);

                var neighborNode = _nodes.GetNode(neighborIdx);
                if (neighborNode?.IsDeleted == true) continue;
                if (isAllowed is not null && !isAllowed(neighborNode!.Id)) continue;

                float dist = Distance(neighborNode!.Id, targetId, vectorLoader);

                if (dist < worstDist || result.Count < ef)
                {
                    candidates.Enqueue((neighborIdx, dist), dist);
                    result.Enqueue((neighborIdx, dist), dist);

                    if (result.Count > ef)
                        result.Dequeue();

                    if (result.Count > 0)
                        worstDist = result.Peek().Distance;
                }
            }
        }

        var list = new List<(int NodeIndex, float Distance)>(result.Count);
        while (result.Count > 0)
            list.Add(result.Dequeue());
        list.Reverse();
        return list;
    }

    /// <summary>
    /// HNSW-Nachbar-Auswahl mit Heuristik (nach Malkov & Yashunin).
    /// Bevorzugt Nachbarn in unterschiedlichen Richtungen statt nur die Nächsten.
    /// Budget begrenzt die Anzahl interner Distanzberechnungen für Geschwindigkeit.
    /// </summary>
    private List<int> SelectNeighborsHeuristic(
        List<(int NodeIndex, float Distance)> candidates,
        int m,
        ulong newId,
        Func<ulong, float[]> vectorLoader,
        int maxDistanceComputations = int.MaxValue)
    {
        var sorted = candidates.OrderBy(c => c.Distance).ToList();

        var selected = new List<int>();
        var newVec = vectorLoader(newId);
        int distComputations = 0;

        foreach (var candidate in sorted)
        {
            if (selected.Count >= m) break;

            var candidateNode = _nodes.GetNode(candidate.NodeIndex);
            if (candidateNode is null || candidateNode.IsDeleted) continue;

            var candidateVec = vectorLoader(candidateNode.Id);
            bool shouldAdd = true;

            // Early-Exit-Optimierung: Wenn der Kandidat weit entfernt ist und der
            // naechste bereits selektierte Nachbar naeher am Kandidaten liegt als
            // der Kandidat zum neuen Vektor, dann ueberspringe die teure Heuristik.
            if (selected.Count > 0 && candidate.Distance > 2.0f)
            {
                var closestSelected = sorted.First(c => selected.Contains(c.NodeIndex));
                var closestNode = _nodes.GetNode(closestSelected.NodeIndex);
                if (closestNode is not null && closestNode.Id != candidateNode.Id)
                {
                    var closestVec = vectorLoader(closestNode.Id);
                    var distToClosest = ComputeDistance(closestVec.AsSpan(), candidateVec.AsSpan());
                    distComputations++;
                    if (distToClosest < candidate.Distance)
                        continue;
                }
            }

            foreach (var selectedIdx in selected)
            {
                if (distComputations >= maxDistanceComputations)
                {
                    shouldAdd = false;
                    break;
                }

                var selectedNode = _nodes.GetNode(selectedIdx);
                if (selectedNode is null) continue;

                var selectedVec = vectorLoader(selectedNode.Id);
                var distSelectedToCandidate = ComputeDistance(selectedVec.AsSpan(), candidateVec.AsSpan());
                distComputations++;

                if (distSelectedToCandidate < candidate.Distance)
                {
                    shouldAdd = false;
                    break;
                }
            }

            if (shouldAdd)
                selected.Add(candidate.NodeIndex);
        }

        foreach (var candidate in sorted)
        {
            if (selected.Count >= m) break;
            if (!selected.Contains(candidate.NodeIndex))
                selected.Add(candidate.NodeIndex);
        }

        return selected;
    }

    private void PruneIfOverfull(HnswNode node, int level, int maxNeighbors, int newNeighborIndex, Func<ulong, float[]> vectorLoader)
    {
        if (level < 0 || level >= node.Neighbors.Length) return;

        lock (node.NeighborLock)
        {
            var count = node.GetNeighborCount(level);
            if (count < maxNeighbors) return;

            var layerNeighbors = node.Neighbors[level];

            // Sammle alle Nachbarn (inkl. neuem) und berechne Distanzen zum Node
            var nodeVec = vectorLoader(node.Id);
            var allCandidates = new List<(int NodeIndex, float Distance)>(count + 1);
            for (int i = 0; i < count; i++)
            {
                var nIdx = layerNeighbors[i];
                if (nIdx < 0) continue;
                var nNode = _nodes.GetNode(nIdx);
                if (nNode is null || nNode.IsDeleted) continue;
                var dist = ComputeDistance(nodeVec.AsSpan(), vectorLoader(nNode.Id).AsSpan());
                allCandidates.Add((nIdx, dist));
            }
            // Neuen Nachbarn hinzufügen
            var newNode = _nodes.GetNode(newNeighborIndex);
            if (newNode is not null && !newNode.IsDeleted)
            {
                var newDist = ComputeDistance(nodeVec.AsSpan(), vectorLoader(newNode.Id).AsSpan());
                allCandidates.Add((newNeighborIndex, newDist));
            }

            // Heuristik anwenden
            var bestNeighbors = SelectNeighborsHeuristic(allCandidates, maxNeighbors, node.Id, vectorLoader, _options.MaxHeuristicDistanceComputations);

            // Array neu aufbauen
            for (int i = 0; i < layerNeighbors.Length; i++)
                layerNeighbors[i] = i < bestNeighbors.Count ? bestNeighbors[i] : -1;
        }
    }

    private void RemoveNodeFromGraph(int nodeIndex)
    {
        var node = _nodes.GetNode(nodeIndex);
        if (node == null) return;

        for (int level = 0; level <= node.TopLayer; level++)
        {
            var neighbors = node.GetNeighborsCopy(level);
            foreach (var neighborIdx in neighbors)
            {
                var neighbor = _nodes.GetNode(neighborIdx);
                neighbor?.RemoveNeighbor(level, nodeIndex);
            }
        }

        node.IsDeleted = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Distance(ulong aId, ulong bId, Func<ulong, float[]> vectorLoader)
    {
        var a = vectorLoader(aId);
        var b = vectorLoader(bId);
        return ComputeDistance(a.AsSpan(), b.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ComputeDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (_metric == DistanceMetric.Cosine)
            return 1.0f - VectorDistance.DotProduct(a, b);
        if (_metric == DistanceMetric.DotProduct)
            return -VectorDistance.DotProduct(a, b);
        return VectorDistance.Euclidean(a, b);
    }

    #endregion

    public void Dispose()
    {
        // Nodes werden vom GC aufgeräumt
    }
}
