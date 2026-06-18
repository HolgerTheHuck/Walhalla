// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.Extensions.AI;
using Walhalla.VectorStore.Collections;

namespace Walhalla.VectorStore.Embeddings;

/// <summary>
/// Verbindungs-Helfer zwischen <see cref="VectorCollection"/> und einem
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// </summary>
public static class EmbeddingCollectionExtensions
{
    /// <summary>
    /// Steckt einen Embedding-Generator an eine bestehende Collection und liefert eine
    /// <see cref="TextVectorCollection"/>. Prüft – falls der Generator seine Dimension
    /// annonciert – sofort gegen die Collection-Dimension, damit ein Mismatch nicht erst
    /// tief im HNSW (oder gar nicht) auffällt.
    /// </summary>
    public static TextVectorCollection WithEmbeddings(
        this VectorCollection collection,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        TextVectorCollectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(generator);

        var advertised = GetAdvertisedDimension(generator);
        if (advertised is int dim && dim != collection.Dimension)
        {
            var modelId = GetModelId(generator);
            throw new ArgumentException(
                $"Embedding-Modell '{modelId ?? "(unbekannt)"}' liefert {dim} Dimensionen, " +
                $"Collection '{collection.Name}' erwartet {collection.Dimension}.",
                nameof(generator));
        }

        return new TextVectorCollection(collection, generator, options);
    }

    /// <summary>
    /// Erstellt/öffnet eine Collection, deren Dimension aus dem Generator-Modell abgeleitet
    /// wird, und steckt den Generator gleich an. Wirft, wenn der Generator seine Dimension
    /// nicht annonciert – dann die Überladung mit expliziter <paramref name="dimension"/> nutzen.
    /// </summary>
    public static TextVectorCollection GetOrCreateTextCollection(
        this EmbeddedVectorStore store,
        string name,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        DistanceMetric metric = DistanceMetric.Cosine,
        TextVectorCollectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var advertised = GetAdvertisedDimension(generator)
            ?? throw new ArgumentException(
                "Der Embedding-Generator annonciert keine Dimension. " +
                "Bitte die Überladung mit expliziter 'dimension' verwenden.",
                nameof(generator));

        return store.GetOrCreateTextCollection(name, generator, advertised, metric, options);
    }

    /// <summary>
    /// Erstellt/öffnet eine Collection mit expliziter Dimension und steckt den Generator an.
    /// </summary>
    public static TextVectorCollection GetOrCreateTextCollection(
        this EmbeddedVectorStore store,
        string name,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        int dimension,
        DistanceMetric metric = DistanceMetric.Cosine,
        TextVectorCollectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var collection = store.GetOrCreateCollection(name, dimension, metric);
        return collection.WithEmbeddings(generator, options);
    }

    private static int? GetAdvertisedDimension(IEmbeddingGenerator<string, Embedding<float>> generator)
        => (generator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)
            ?.DefaultModelDimensions;

    private static string? GetModelId(IEmbeddingGenerator<string, Embedding<float>> generator)
        => (generator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata)
            ?.DefaultModelId;
}
