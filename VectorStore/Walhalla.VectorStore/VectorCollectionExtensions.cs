// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Walhalla.VectorStore.Collections;

namespace Walhalla.VectorStore;

/// <summary>Erweiterungsmethoden für einfachere Embedded-Nutzung.</summary>
public static class VectorCollectionExtensions
{
    /// <summary>Fügt einen Vektor ein oder aktualisiert ihn. Metadata ist optional.</summary>
    public static Task UpsertAsync(
        this VectorCollection collection,
        ulong id,
        Vector vector,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        VectorMetadata? meta = null;
        if (metadata is not null)
        {
            meta = new VectorMetadata
            {
                Id = id,
                Collection = collection.Name,
                Payload = metadata
            };
        }
        return collection.PutAsync(id, vector, meta, ct);
    }

    /// <summary>Sucht mit HNSW (falls aktiviert), sonst IVF, sonst Exact.</summary>
    public static async Task<List<VectorSearchResult>> SearchAsync(
        this VectorCollection collection,
        Vector query,
        int topK = 10,
        int? ef = null,
        int? nprobe = null,
        CancellationToken ct = default)
    {
        var results = new List<VectorSearchResult>();

        if (collection.Index is not null)
        {
            await foreach (var r in collection.SearchHnswAsync(query, topK, ef, ct: ct))
                results.Add(r);
        }
        else if (collection.IvfIndex is not null)
        {
            await foreach (var r in collection.SearchIvfAsync(query, topK, nprobe, ct: ct))
                results.Add(r);
        }
        else
        {
            await foreach (var r in collection.SearchExactAsync(query, topK, ct: ct))
                results.Add(r);
        }

        return results;
    }

    /// <summary>Sucht mit Metadata-Filter (brute-force, da HNSW keine Filterung hat).</summary>
    public static async Task<List<VectorSearchResult>> SearchAsync(
        this VectorCollection collection,
        Vector query,
        int topK,
        Func<Dictionary<string, object>?, bool> filter,
        CancellationToken ct = default)
    {
        var results = new List<VectorSearchResult>();

        await foreach (var r in collection.SearchExactAsync(query, int.MaxValue, ct: ct))
        {
            if (filter(r.Metadata?.Payload))
                results.Add(r);

            if (results.Count >= topK)
                break;
        }

        return results;
    }

    /// <summary>Gibt alle Einträge mit ihren Metadaten zurück.</summary>
    public static async Task<List<VectorEntry>> GetAllAsync(
        this VectorCollection collection,
        CancellationToken ct = default)
    {
        var results = new List<VectorEntry>();
        await foreach (var id in collection.EnumerateIdsAsync(ct))
        {
            var entry = await collection.GetAsync(id, ct);
            if (entry is not null)
                results.Add(entry);
        }
        return results;
    }

    /// <summary>Zählt Einträge, deren Metadata den Filter erfüllt.</summary>
    public static async Task<int> CountAsync(
        this VectorCollection collection,
        Func<Dictionary<string, object>?, bool>? filter = null,
        CancellationToken ct = default)
    {
        if (filter is null)
        {
            var memCount = collection.Count;
            if (memCount > 0)
                return memCount;

            // Fallback nach Store-Reopen ohne persistierten Count (Migration von alten Daten)
            int storeCount = 0;
            await foreach (var _ in collection.EnumerateIdsAsync(ct))
                storeCount++;
            return storeCount;
        }

        int count = 0;
        await foreach (var id in collection.EnumerateIdsAsync(ct))
        {
            var entry = await collection.GetAsync(id, ct);
            if (entry?.Metadata?.Payload is not null && filter(entry.Metadata.Payload))
                count++;
        }
        return count;
    }
}
