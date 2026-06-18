// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Microsoft.Extensions.AI;

namespace Walhalla.VectorStore.Embeddings;

/// <summary>
/// Rolle eines Texts beim Embedding. Asymmetrische Modelle (z. B. nomic-embed-text)
/// erwarten unterschiedliche Prefixe für gespeicherte Dokumente und Suchanfragen
/// (<c>search_document:</c> vs. <c>search_query:</c>). Symmetrische Modelle ignorieren
/// die Unterscheidung.
/// </summary>
public enum EmbeddingInputType
{
    /// <summary>Ein zu indexierendes Dokument (Ingest).</summary>
    Document,

    /// <summary>Eine Suchanfrage (Query).</summary>
    Query,
}

/// <summary>
/// Trägt die <see cref="EmbeddingInputType"/>-Rolle über die Standard-Schnittstelle
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> hinweg – via
/// <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>.
/// Generatoren, die die Konvention kennen, werten sie aus; alle anderen ignorieren sie
/// folgenlos. So bleibt die Rolle aus dem Aufrufer heraus steuerbar, ohne eine eigene
/// Embedder-Schnittstelle erfinden zu müssen.
/// </summary>
public static class EmbeddingInputTypeConvention
{
    /// <summary>Schlüssel in <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>.</summary>
    public const string PropertyKey = "walhalla.input_type";

    /// <summary>Erzeugt Optionen, die die angegebene Rolle transportieren.</summary>
    public static EmbeddingGenerationOptions CreateOptions(EmbeddingInputType inputType)
    {
        var options = new EmbeddingGenerationOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[PropertyKey] = inputType;
        return options;
    }

    /// <summary>Liest die Rolle aus den Optionen; Fallback, wenn nicht gesetzt.</summary>
    public static EmbeddingInputType GetInputType(
        EmbeddingGenerationOptions? options,
        EmbeddingInputType fallback = EmbeddingInputType.Document)
    {
        if (options?.AdditionalProperties is not null
            && options.AdditionalProperties.TryGetValue(PropertyKey, out var raw))
        {
            return raw switch
            {
                EmbeddingInputType typed => typed,
                string s when Enum.TryParse<EmbeddingInputType>(s, ignoreCase: true, out var parsed) => parsed,
                _ => fallback,
            };
        }

        return fallback;
    }
}
