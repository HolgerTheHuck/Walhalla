// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.VectorStore.Embeddings.Onnx;

/// <summary>
/// Lädt Modell- und Tokenizer-Dateien aus einem HuggingFace-Repo und cached sie lokal.
/// Vorhandene Dateien werden übersprungen; Downloads sind atomar (Temp + Rename).
/// </summary>
public sealed class HuggingFaceModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly string _repo;
    private readonly string _cacheRoot;

    public HuggingFaceModelDownloader(string repo, string cacheRoot)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
    }

    /// <summary>Lokales Cache-Verzeichnis für dieses Repo.</summary>
    public string RepoCacheDirectory => Path.Combine(_cacheRoot, _repo.Replace('/', '_'));

    /// <summary>
    /// Stellt sicher, dass <paramref name="repoRelativePath"/> (z. B. <c>onnx/model.onnx</c>)
    /// lokal vorliegt, und liefert den lokalen Pfad zurück.
    /// </summary>
    public async Task<string> EnsureFileAsync(
        string repoRelativePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var localPath = Path.Combine(RepoCacheDirectory, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(localPath))
            return localPath;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var url = $"https://huggingface.co/{_repo}/resolve/main/{repoRelativePath}";
        var tempPath = localPath + ".part";

        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var http = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long read = 0;
                int n;
                while ((n = await http.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total is long t and > 0)
                        progress?.Report(read * 100.0 / t);
                }
            }

            File.Move(tempPath, localPath, overwrite: true);
            progress?.Report(100.0);
            return localPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
