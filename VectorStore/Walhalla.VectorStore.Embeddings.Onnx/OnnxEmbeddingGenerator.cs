// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Walhalla.VectorStore.Embeddings.Onnx;

/// <summary>
/// Lokaler Embedding-Generator auf Basis von ONNX Runtime + BERT-Tokenizer.
/// Macht intern: Prefix je Rolle → Tokenize → Inferenz → Mean-Pooling → L2-Normalisierung.
/// Implementiert die Standard-Schnittstelle <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly OnnxEmbeddingOptions _options;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly EmbeddingGeneratorMetadata _metadata;

    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private readonly string _outputName;
    private readonly bool _outputIsPooled;

    private OnnxEmbeddingGenerator(
        OnnxEmbeddingOptions options,
        InferenceSession session,
        BertTokenizer tokenizer)
    {
        _options = options;
        _session = session;
        _tokenizer = tokenizer;
        _metadata = new EmbeddingGeneratorMetadata("walhalla-onnx", null, options.ModelId, options.Dimension);

        // Ein-/Ausgabenamen aus dem Modell ableiten statt hart zu kodieren –
        // verschiedene Exporte benennen die Tensoren unterschiedlich.
        _inputIdsName = FindInput(session, "input_ids", n => n.Contains("ids") && !n.Contains("type"));
        _attentionMaskName = FindInput(session, "attention_mask", n => n.Contains("mask"));
        _tokenTypeIdsName = session.InputMetadata.Keys
            .FirstOrDefault(k => k.ToLowerInvariant().Contains("type"));

        var output = session.OutputMetadata.First();
        _outputName = output.Key;
        // Rang 3 ([batch, seq, hidden]) → Token-Embeddings, wir poolen selbst.
        // Rang 2 ([batch, hidden]) → Modell hat bereits gepoolt.
        _outputIsPooled = output.Value.Dimensions.Length == 2;
    }

    /// <summary>
    /// Erstellt den Generator. Lädt Modell + Vocab bei Bedarf aus dem HuggingFace-Repo
    /// (Auto-Download + Cache), sofern keine expliziten Pfade gesetzt sind.
    /// </summary>
    public static async Task<OnnxEmbeddingGenerator> CreateAsync(
        OnnxEmbeddingOptions? options = null,
        IProgress<double>? downloadProgress = null,
        CancellationToken ct = default)
    {
        options ??= new OnnxEmbeddingOptions();

        string modelPath, vocabPath;
        if (options.ModelPath is not null && options.VocabPath is not null)
        {
            modelPath = options.ModelPath;
            vocabPath = options.VocabPath;
        }
        else
        {
            var downloader = new HuggingFaceModelDownloader(options.HuggingFaceRepo, options.CacheDirectory);
            modelPath = options.ModelPath
                ?? await downloader.EnsureFileAsync(options.OnnxFileName, downloadProgress, ct).ConfigureAwait(false);
            vocabPath = options.VocabPath
                ?? await downloader.EnsureFileAsync("vocab.txt", downloadProgress, ct).ConfigureAwait(false);
        }

        var sessionOptions = BuildSessionOptions(options);
        var session = new InferenceSession(modelPath, sessionOptions);

        var bertOptions = new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            RemoveNonSpacingMarks = true,
        };
        var tokenizer = BertTokenizer.Create(vocabPath, bertOptions);

        return new OnnxEmbeddingGenerator(options, session, tokenizer);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputType = EmbeddingInputTypeConvention.GetInputType(options);
        var prefix = inputType == EmbeddingInputType.Query ? _options.QueryPrefix : _options.DocumentPrefix;

        var texts = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>();
        if (texts.Count == 0)
            return Task.FromResult(result);

        // 1. Tokenisieren (mit Rollen-Prefix, Sonderzeichen + Trunkierung über den Tokenizer)
        var idLists = new int[texts.Count][];
        int maxLen = 1;
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = _tokenizer.EncodeToIds(prefix + texts[i], _options.MaxTokens, out _, out _, true, true);
            idLists[i] = ids as int[] ?? ids.ToArray();
            if (idLists[i].Length > maxLen) maxLen = idLists[i].Length;
        }

        // 2. Gepaddte Tensoren bauen
        int batch = texts.Count;
        var inputIds = new DenseTensor<long>(new[] { batch, maxLen });
        var attentionMask = new DenseTensor<long>(new[] { batch, maxLen });
        var tokenTypeIds = _tokenTypeIdsName is not null ? new DenseTensor<long>(new[] { batch, maxLen }) : null;

        for (int b = 0; b < batch; b++)
        {
            var ids = idLists[b];
            for (int s = 0; s < ids.Length; s++)
            {
                inputIds[b, s] = ids[s];
                attentionMask[b, s] = 1;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMask),
        };
        if (_tokenTypeIdsName is not null && tokenTypeIds is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIds));

        // 3. Inferenz + Pooling
        using var outputs = _session.Run(inputs, new[] { _outputName });
        var tensor = outputs.First().AsTensor<float>();

        int hidden = _options.Dimension;
        for (int b = 0; b < batch; b++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var emb = _outputIsPooled
                ? ExtractPooled(tensor, b, hidden)
                : MeanPool(tensor, b, idLists[b].Length, hidden);

            NormalizeL2(emb);
            result.Add(new Embedding<float>(emb) { ModelId = _options.ModelId });
        }

        return Task.FromResult(result);
    }

    private static float[] MeanPool(Tensor<float> tokenEmbeddings, int b, int realTokens, int hidden)
    {
        var emb = new float[hidden];
        if (realTokens <= 0) return emb;

        for (int s = 0; s < realTokens; s++)
            for (int h = 0; h < hidden; h++)
                emb[h] += tokenEmbeddings[b, s, h];

        float inv = 1f / realTokens;
        for (int h = 0; h < hidden; h++)
            emb[h] *= inv;

        return emb;
    }

    private static float[] ExtractPooled(Tensor<float> pooled, int b, int hidden)
    {
        var emb = new float[hidden];
        for (int h = 0; h < hidden; h++)
            emb[h] = pooled[b, h];
        return emb;
    }

    private static void NormalizeL2(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        if (sum <= 0) return;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    private static SessionOptions BuildSessionOptions(OnnxEmbeddingOptions options)
    {
        var so = new SessionOptions();
        if (options.ExecutionProvider == OnnxExecutionProvider.DirectML)
        {
            // DirectML wird per Reflection angesteckt: nur verfügbar, wenn das Paket
            // Microsoft.ML.OnnxRuntime.DirectML referenziert ist (statt der CPU-Variante).
            var method = typeof(SessionOptions).GetMethod(
                "AppendExecutionProvider_DML", new[] { typeof(int) });
            if (method is null)
            {
                so.Dispose();
                throw new InvalidOperationException(
                    "DirectML ist nicht verfügbar. Bitte das Paket " +
                    "'Microsoft.ML.OnnxRuntime.DirectML' referenzieren (anstelle der CPU-Variante).");
            }
            method.Invoke(so, new object[] { options.DeviceId });
        }
        return so;
    }

    private static string FindInput(InferenceSession session, string preferred, Func<string, bool> match)
    {
        if (session.InputMetadata.ContainsKey(preferred))
            return preferred;

        var hit = session.InputMetadata.Keys.FirstOrDefault(k => match(k.ToLowerInvariant()));
        return hit ?? throw new InvalidOperationException(
            $"Konnte den Modell-Input '{preferred}' nicht zuordnen. " +
            $"Verfügbare Inputs: {string.Join(", ", session.InputMetadata.Keys)}.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(EmbeddingGeneratorMetadata)) return _metadata;
        if (serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    public void Dispose() => _session.Dispose();
}
