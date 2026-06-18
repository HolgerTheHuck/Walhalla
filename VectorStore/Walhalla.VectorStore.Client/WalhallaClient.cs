// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Grpc.Core;
using Grpc.Net.Client;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Walhalla.VectorStore.Grpc;
using GrpcVectorStore = Walhalla.VectorStore.Grpc.VectorStore;

namespace Walhalla.VectorStore.Client;

/// <summary>
/// gRPC-Client fuer Walhalla.VectorStore.
/// Qdrant-aehnliche API fuer eingebettete Vektor-Datenbanken.
/// </summary>
public sealed class WalhallaClient : IDisposable
{
    private readonly GrpcVectorStore.VectorStoreClient _grpc;
    private readonly GrpcChannel _channel;
    private readonly HttpClient? _http;
    private readonly bool _ownsChannel;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Erstellt einen neuen gRPC-Client.
    /// </summary>
    /// <param name="address">Server-Adresse, z.B. http://localhost:5000</param>
    public WalhallaClient(string address, string apiKey = "walhalla-dev-key")
    {
        _ownsChannel = true;
        _ownsHttpClient = true;
        _apiKey = apiKey;
        _channel = GrpcChannel.ForAddress(address);
        _grpc = new GrpcVectorStore.VectorStoreClient(_channel);
        _http = new HttpClient { BaseAddress = new Uri(address) };
        ConfigureHttpClient(_http);
    }

    /// <summary>
    /// Erstellt einen Client mit einem bestehenden Channel.
    /// </summary>
    public WalhallaClient(GrpcChannel channel)
    {
        _ownsChannel = false;
        _ownsHttpClient = false;
        _apiKey = "walhalla-dev-key";
        _channel = channel;
        _grpc = new GrpcVectorStore.VectorStoreClient(_channel);
    }

    public WalhallaClient(GrpcChannel channel, HttpClient httpClient, string apiKey = "walhalla-dev-key")
    {
        _ownsChannel = false;
        _ownsHttpClient = false;
        _apiKey = apiKey;
        _channel = channel;
        _grpc = new GrpcVectorStore.VectorStoreClient(_channel);
        _http = httpClient;
        ConfigureHttpClient(_http);
    }

    /// <summary>
    /// Erstellt eine neue Collection.
    /// </summary>
    public async Task<Models.CollectionInfo> CreateCollectionAsync(
        string name,
        int dimension,
        Models.DistanceMetric metric,
        bool enableHnsw = true,
        CancellationToken ct = default)
    {
        var request = new CreateCollectionRequest
        {
            Name = name,
            Dimension = dimension,
            Metric = metric.ToString(),
            EnableHnsw = enableHnsw
        };
        var response = await _grpc.CreateCollectionAsync(request, headers: CreateHeaders(), cancellationToken: ct);
        return Map(response);
    }

    /// <summary>
    /// Loescht eine Collection.
    /// </summary>
    public async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await _grpc.DeleteCollectionAsync(new DeleteCollectionRequest { Name = name }, headers: CreateHeaders(), cancellationToken: ct);
    }

    /// <summary>
    /// Listet alle Collections auf.
    /// </summary>
    public async Task<IReadOnlyList<Models.CollectionInfo>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var response = await _grpc.ListCollectionsAsync(new Empty(), headers: CreateHeaders(), cancellationToken: ct);
        return response.Collections.Select(Map).ToList();
    }

    /// <summary>
    /// Fuegt einen Vektor ein oder aktualisiert ihn.
    /// </summary>
    public async Task UpsertAsync(
        string collection,
        ulong id,
        float[] vector,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var request = new UpsertRequest
        {
            Collection = collection,
            Id = id,
            MetadataJson = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : string.Empty
        };
        request.Vector.AddRange(vector);
        await _grpc.UpsertAsync(request, headers: CreateHeaders(), cancellationToken: ct);
    }

    /// <summary>
    /// Ruft einen Vektor ab.
    /// </summary>
    public async Task<Models.VectorEntry?> GetAsync(string collection, ulong id, CancellationToken ct = default)
    {
        try
        {
            var response = await _grpc.GetAsync(
                new GetRequest { Collection = collection, Id = id },
                headers: CreateHeaders(),
                cancellationToken: ct);
            return new Models.VectorEntry
            {
                Id = response.Id,
                Dimension = response.Dimension,
                Metadata = ParseMetadata(response.MetadataJson)
            };
        }
        catch (global::Grpc.Core.RpcException ex) when (ex.StatusCode == global::Grpc.Core.StatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Loescht einen Vektor.
    /// </summary>
    public async Task DeleteAsync(string collection, ulong id, CancellationToken ct = default)
    {
        await _grpc.DeleteAsync(new DeleteRequest { Collection = collection, Id = id }, headers: CreateHeaders(), cancellationToken: ct);
    }

    /// <summary>
    /// HNSW-Approximationssuche.
    /// </summary>
    public async Task<IReadOnlyList<Models.SearchResult>> SearchAsync(
        string collection,
        float[] vector,
        int topK = 10,
        int? ef = null,
        CancellationToken ct = default)
    {
        var request = new SearchRequest { Collection = collection, TopK = topK };
        request.Vector.AddRange(vector);
        if (ef.HasValue) request.Ef = ef.Value;
        var response = await _grpc.SearchAsync(request, headers: CreateHeaders(), cancellationToken: ct);
        return response.Results.Select(Map).ToList();
    }

    /// <summary>
    /// Exakte Brute-Force-Suche.
    /// </summary>
    public async Task<IReadOnlyList<Models.SearchResult>> SearchExactAsync(
        string collection,
        float[] vector,
        int topK = 10,
        CancellationToken ct = default)
    {
        var request = new SearchRequest { Collection = collection, TopK = topK };
        request.Vector.AddRange(vector);
        var response = await _grpc.SearchExactAsync(request, headers: CreateHeaders(), cancellationToken: ct);
        return response.Results.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Models.SearchResult>> SearchTextAsync(
        string collection,
        string field,
        string query,
        int topK = 10,
        Models.FullTextQueryMode mode = Models.FullTextQueryMode.All,
        string? notQuery = null,
        CancellationToken ct = default)
    {
        var request = new TextSearchRequest
        {
            Collection = collection,
            Field = field,
            Query = query,
            TopK = topK,
            Mode = MapFullTextQueryMode(mode),
            NotQuery = notQuery ?? string.Empty
        };

        var response = await _grpc.SearchTextAsync(request, headers: CreateHeaders(), cancellationToken: ct);
        return response.Results.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Models.SearchResult>> SearchHybridAsync(
        string collection,
        string field,
        string textQuery,
        float[] vector,
        int topK = 10,
        int textCandidateCount = 50,
        Models.FullTextQueryMode mode = Models.FullTextQueryMode.All,
        string? notQuery = null,
        CancellationToken ct = default)
    {
        var request = new HybridSearchRequest
        {
            Collection = collection,
            Field = field,
            TextQuery = textQuery,
            TopK = topK,
            TextCandidateCount = textCandidateCount,
            Mode = MapFullTextQueryMode(mode),
            NotQuery = notQuery ?? string.Empty
        };
        request.Vector.AddRange(vector);

        var response = await _grpc.SearchHybridAsync(request, headers: CreateHeaders(), cancellationToken: ct);
        return response.Results.Select(Map).ToList();
    }

    public async Task<Models.CollectionManifest> GetCollectionManifestAsync(string collection, CancellationToken ct = default)
    {
        var http = GetHttpClient();
        var manifest = await http.GetFromJsonAsync<Models.CollectionManifest>($"/api/collections/{Uri.EscapeDataString(collection)}/manifest", _jsonOptions, ct).ConfigureAwait(false);
        if (manifest is null)
            throw new InvalidOperationException($"Manifest for collection '{collection}' could not be read.");

        return manifest;
    }

    public async IAsyncEnumerable<Models.CollectionChangeEvent> WatchChangesAsync(string collection, long afterSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = GetHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/collections/{Uri.EscapeDataString(collection)}/changes?after={afterSequence}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataLines = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                    continue;

                var payload = string.Join("\n", dataLines);
                var change = JsonSerializer.Deserialize<Models.CollectionChangeEvent>(payload, _jsonOptions);
                dataLines.Clear();

                if (change is not null)
                    yield return change;

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Add(line[5..].TrimStart());
        }
    }

    private static Models.CollectionInfo Map(CollectionInfo c) => new()
    {
        Name = c.Name,
        Dimension = c.Dimension,
        Metric = Enum.Parse<Models.DistanceMetric>(c.Metric),
        Count = c.Count,
        HnswEnabled = c.HnswEnabled
    };

    private static Models.SearchResult Map(SearchResult r) => new()
    {
        Id = r.Id,
        Score = r.Score,
        Metadata = ParseMetadata(r.MetadataJson)
    };

    private static Dictionary<string, object>? ParseMetadata(string json) =>
        string.IsNullOrEmpty(json) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);

    private Metadata CreateHeaders()
    {
        return new Metadata { { "x-api-key", _apiKey } };
    }

    private static Walhalla.VectorStore.Grpc.FullTextQueryMode MapFullTextQueryMode(Models.FullTextQueryMode mode)
    {
        return mode switch
        {
            Models.FullTextQueryMode.Any => Walhalla.VectorStore.Grpc.FullTextQueryMode.Any,
            _ => Walhalla.VectorStore.Grpc.FullTextQueryMode.All,
        };
    }

    private void ConfigureHttpClient(HttpClient httpClient)
    {
        if (!httpClient.DefaultRequestHeaders.Contains("X-API-Key"))
            httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    private HttpClient GetHttpClient()
    {
        return _http ?? throw new InvalidOperationException("HTTP features require a client that was created with a server address or HttpClient.");
    }

    public void Dispose()
    {
        if (_ownsChannel) _channel.Dispose();
        if (_ownsHttpClient) _http?.Dispose();
    }
}
