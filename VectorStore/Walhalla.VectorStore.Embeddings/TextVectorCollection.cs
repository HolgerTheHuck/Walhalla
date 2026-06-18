// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Walhalla.VectorStore.Collections;

namespace Walhalla.VectorStore.Embeddings;

/// <summary>
/// Optionen für die Text-zu-Vektor-Verheiratung.
/// </summary>
public sealed record TextVectorCollectionOptions
{
    /// <summary>
    /// Legt den Originaltext zusätzlich als Payload-Feld ab, damit Treffer den Text
    /// direkt zurückliefern (statt nur die ID). Default: aktiviert.
    /// </summary>
    public bool StoreText { get; init; } = true;

    /// <summary>Payload-Schlüssel, unter dem der Originaltext abgelegt wird.</summary>
    public string TextPayloadKey { get; init; } = "text";
}

/// <summary>
/// Verheiratet eine <see cref="VectorCollection"/> mit einem
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> und kapselt die Naht zwischen
/// "Text rein" und "Vektor gespeichert/gesucht". Die Collection bleibt über
/// <see cref="Collection"/> für alle Nicht-Text-Operationen voll zugänglich.
/// </summary>
/// <remarks>
/// Der Generator ist über die Standard-Schnittstelle austauschbar: lokal (ONNX),
/// remote (Gateway) oder fremd (OpenAI/Ollama). Diese Klasse weiß davon nichts –
/// sie kennt nur Dimension und Input-Rolle.
/// </remarks>
public sealed class TextVectorCollection
{
    private readonly TextVectorCollectionOptions _options;

    /// <summary>Die zugrunde liegende Collection (für CRUD, Filter, Snapshots …).</summary>
    public VectorCollection Collection { get; }

    /// <summary>Der angesteckte Embedding-Generator.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

    internal TextVectorCollection(
        VectorCollection collection,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        TextVectorCollectionOptions? options)
    {
        Collection = collection ?? throw new ArgumentNullException(nameof(collection));
        Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _options = options ?? new TextVectorCollectionOptions();
    }

    /// <summary>Embeddet einen einzelnen Text in der angegebenen Rolle (Escape-Hatch).</summary>
    public async Task<float[]> EmbedAsync(
        string text,
        EmbeddingInputType inputType,
        CancellationToken ct = default)
    {
        var options = EmbeddingInputTypeConvention.CreateOptions(inputType);
        var embeddings = await Generator
            .GenerateAsync(new[] { text }, options, ct)
            .ConfigureAwait(false);
        var data = embeddings[0].Vector.ToArray();
        EnsureDimension(data.Length);
        return data;
    }

    /// <summary>Embeddet einen Text als Dokument und schreibt ihn in die Collection.</summary>
    public async Task UpsertTextAsync(
        ulong id,
        string text,
        IDictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var data = await EmbedAsync(text, EmbeddingInputType.Document, ct).ConfigureAwait(false);
        await Collection.UpsertAsync(id, new Vector(data), BuildPayload(text, metadata), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Batch-Variante: embeddet alle Texte in einem Generator-Aufruf und schreibt sie
    /// gesammelt über <see cref="VectorCollection.PutBatchAsync"/>.
    /// </summary>
    public async Task UpsertTextBatchAsync(
        IEnumerable<(ulong Id, string Text, IDictionary<string, object>? Metadata)> items,
        bool skipHnswIndex = false,
        CancellationToken ct = default)
    {
        var batch = items.ToList();
        if (batch.Count == 0) return;

        var options = EmbeddingInputTypeConvention.CreateOptions(EmbeddingInputType.Document);
        var embeddings = await Generator
            .GenerateAsync(batch.Select(static b => b.Text), options, ct)
            .ConfigureAwait(false);

        var prepared = new (ulong, Vector, VectorMetadata?)[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            var data = embeddings[i].Vector.ToArray();
            EnsureDimension(data.Length);

            var payload = BuildPayload(batch[i].Text, batch[i].Metadata);
            VectorMetadata? meta = payload is null ? null : new VectorMetadata
            {
                Id = batch[i].Id,
                Collection = Collection.Name,
                Payload = payload,
            };

            prepared[i] = (batch[i].Id, new Vector(data), meta);
        }

        await Collection.PutBatchAsync(prepared, skipHnswIndex, ct).ConfigureAwait(false);
    }

    /// <summary>Embeddet die Anfrage als Query und sucht (HNSW → IVF → Exact).</summary>
    public async Task<List<VectorSearchResult>> SearchTextAsync(
        string query,
        int topK = 10,
        int? ef = null,
        int? nprobe = null,
        CancellationToken ct = default)
    {
        var data = await EmbedAsync(query, EmbeddingInputType.Query, ct).ConfigureAwait(false);
        return await Collection.SearchAsync(new Vector(data), topK, ef, nprobe, ct).ConfigureAwait(false);
    }

    private Dictionary<string, object>? BuildPayload(string text, IDictionary<string, object>? metadata)
    {
        if (!_options.StoreText && metadata is null)
            return null;

        var payload = metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(metadata);

        if (_options.StoreText)
            payload[_options.TextPayloadKey] = text;

        return payload.Count == 0 ? null : payload;
    }

    private void EnsureDimension(int produced)
    {
        if (produced != Collection.Dimension)
        {
            throw new InvalidOperationException(
                $"Embedding-Generator lieferte {produced} Dimensionen, " +
                $"Collection '{Collection.Name}' erwartet {Collection.Dimension}. " +
                "Modell und Collection-Dimension müssen übereinstimmen.");
        }
    }
}
