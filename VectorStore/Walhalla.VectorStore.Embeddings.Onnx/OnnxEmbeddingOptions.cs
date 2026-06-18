// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;

namespace Walhalla.VectorStore.Embeddings.Onnx;

/// <summary>ONNX-Modellvariante – Genauigkeit vs. Größe/Geschwindigkeit.</summary>
public enum OnnxModelVariant
{
    /// <summary>Volle Genauigkeit (~550 MB). Beste Qualität.</summary>
    Fp32,

    /// <summary>Int8-quantisiert (~140 MB). Schnell auf CPU, minimaler Qualitätsverlust. Default.</summary>
    Quantized,

    /// <summary>Float16 (~275 MB). Sinnvoll mit GPU/DirectML.</summary>
    Fp16,
}

/// <summary>Ausführungs-Provider für ONNX Runtime.</summary>
public enum OnnxExecutionProvider
{
    /// <summary>Reine CPU-Ausführung. Läuft überall.</summary>
    Cpu,

    /// <summary>
    /// DirectML (GPU auf Windows). Erfordert, dass statt des CPU-Pakets das Paket
    /// <c>Microsoft.ML.OnnxRuntime.DirectML</c> referenziert wird.
    /// </summary>
    DirectML,
}

/// <summary>
/// Konfiguration für den lokalen ONNX-Embedding-Generator.
/// Defaults sind auf nomic-embed-text-v1.5 abgestimmt.
/// </summary>
public sealed record OnnxEmbeddingOptions
{
    /// <summary>HuggingFace-Repo-ID des Modells.</summary>
    public string HuggingFaceRepo { get; init; } = "nomic-ai/nomic-embed-text-v1.5";

    /// <summary>Modell-Identifier (für <c>EmbeddingGeneratorMetadata</c>).</summary>
    public string ModelId { get; init; } = "nomic-embed-text-v1.5";

    /// <summary>Ausgabedimension des Modells.</summary>
    public int Dimension { get; init; } = 768;

    /// <summary>Welche Modellvariante geladen wird.</summary>
    public OnnxModelVariant Variant { get; init; } = OnnxModelVariant.Quantized;

    /// <summary>Ausführungs-Provider.</summary>
    public OnnxExecutionProvider ExecutionProvider { get; init; } = OnnxExecutionProvider.Cpu;

    /// <summary>Geräte-ID für DirectML (Multi-GPU).</summary>
    public int DeviceId { get; init; } = 0;

    /// <summary>Maximale Tokenlänge pro Eingabe; längere Texte werden abgeschnitten.</summary>
    public int MaxTokens { get; init; } = 2048;

    /// <summary>Prefix für zu indexierende Dokumente (nomic-Konvention).</summary>
    public string DocumentPrefix { get; init; } = "search_document: ";

    /// <summary>Prefix für Suchanfragen (nomic-Konvention).</summary>
    public string QueryPrefix { get; init; } = "search_query: ";

    /// <summary>
    /// Cache-Verzeichnis für heruntergeladene Modelle.
    /// Default: <c>%LOCALAPPDATA%\Walhalla\models</c>.
    /// </summary>
    public string CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Walhalla", "models");

    /// <summary>
    /// Optionaler expliziter Pfad zur .onnx-Datei. Wenn gesetzt, wird kein Download versucht.
    /// </summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// Optionaler expliziter Pfad zur vocab.txt. Wenn gesetzt, wird kein Download versucht.
    /// </summary>
    public string? VocabPath { get; init; }

    /// <summary>Dateiname der ONNX-Datei im HF-Repo je nach Variante.</summary>
    internal string OnnxFileName => Variant switch
    {
        OnnxModelVariant.Fp32 => "onnx/model.onnx",
        OnnxModelVariant.Quantized => "onnx/model_quantized.onnx",
        OnnxModelVariant.Fp16 => "onnx/model_fp16.onnx",
        _ => "onnx/model.onnx",
    };
}
