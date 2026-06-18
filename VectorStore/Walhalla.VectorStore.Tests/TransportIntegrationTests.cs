// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

extern alias WalhallaVectorStoreApi;

using System.Net.Http.Json;
using System.Text.Json;
using GrpcCore = Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Walhalla.VectorStore.Client;
using Walhalla.VectorStore.Client.Models;

namespace Walhalla.VectorStore.Tests;

public sealed class TransportIntegrationTests
{
    [Fact]
    public async Task Rest_RejectsMissingApiKey()
    {
        await using var factory = new VectorStoreApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/collections");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("Invalid or missing API key", payload!["error"]);
    }

    [Fact]
    public async Task Rest_TextSearch_UsesModeAndNotQuery()
    {
        await using var factory = new VectorStoreApiFactory();
        using var client = factory.CreateApiClient();

        await CreateCollectionAsync(client, "rest-text", 3);
        await PutVectorAsync(client, "rest-text", 1, new[] { 1f, 0f, 0f }, "shared agent memory handbook");
        await PutVectorAsync(client, "rest-text", 2, new[] { 0f, 1f, 0f }, "shared notes only");
        await PutVectorAsync(client, "rest-text", 3, new[] { 0f, 0f, 1f }, "shared private notebook");

        using var response = await client.PostAsJsonAsync("/api/collections/rest-text/search/text", new
        {
            field = "body",
            query = "shared \"agent memory\"",
            topK = 10,
            mode = "any",
            notQuery = "private"
        });
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<SearchResult>>(JsonOptions);

        Assert.NotNull(results);
        Assert.Equal(2, results!.Count);
        Assert.Equal(1ul, results[0].Id);
        Assert.Equal(2ul, results[1].Id);
    }

    [Fact]
    public async Task Grpc_RejectsMissingApiKey()
    {
        await using var factory = new VectorStoreApiFactory();
        using var httpClient = factory.CreateClient();
        using var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });

        var grpc = new global::Walhalla.VectorStore.Grpc.VectorStore.VectorStoreClient(channel);
        var ex = await Assert.ThrowsAsync<GrpcCore.RpcException>(() => grpc.ListCollectionsAsync(new global::Walhalla.VectorStore.Grpc.Empty()).ResponseAsync);

        Assert.Equal(GrpcCore.StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal("Invalid or missing API key", ex.Status.Detail);
    }

    [Fact]
    public async Task Grpc_HybridSearch_ReranksByVectorDistance()
    {
        await using var factory = new VectorStoreApiFactory();
        using var httpClient = factory.CreateApiClient();
        using var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });
        using var client = new WalhallaClient(channel, httpClient, factory.ApiKey);

        await client.CreateCollectionAsync("grpc-hybrid", 3, Walhalla.VectorStore.Client.Models.DistanceMetric.Euclidean, enableHnsw: true);
        await client.UpsertAsync("grpc-hybrid", 1, new[] { 1f, 0f, 0f }, new Dictionary<string, object> { ["body"] = "agent memory handbook" });
        await client.UpsertAsync("grpc-hybrid", 2, new[] { 0f, 1f, 0f }, new Dictionary<string, object> { ["body"] = "agent memory memo" });
        await client.UpsertAsync("grpc-hybrid", 3, new[] { 0f, 0f, 1f }, new Dictionary<string, object> { ["body"] = "gardening notes" });

        var results = await client.SearchHybridAsync(
            "grpc-hybrid",
            "body",
            "agent memory",
            new[] { 0f, 1f, 0f },
            topK: 2,
            textCandidateCount: 10,
            mode: FullTextQueryMode.All);

        Assert.Equal(2, results.Count);
        Assert.Equal(2ul, results[0].Id);
        Assert.Equal(1ul, results[1].Id);
        Assert.True(results[0].Score < results[1].Score);
    }

    [Fact]
    public async Task Grpc_Upsert_RejectsInvalidMetadataJson()
    {
        await using var factory = new VectorStoreApiFactory();
        using var httpClient = factory.CreateApiClient();
        using var channel = GrpcChannel.ForAddress(httpClient.BaseAddress!, new GrpcChannelOptions { HttpClient = httpClient });
        using var client = new WalhallaClient(channel, httpClient, factory.ApiKey);

        await client.CreateCollectionAsync("grpc-invalid-metadata", 3, Walhalla.VectorStore.Client.Models.DistanceMetric.Euclidean, enableHnsw: true);

        var grpc = new global::Walhalla.VectorStore.Grpc.VectorStore.VectorStoreClient(channel);
        var ex = await Assert.ThrowsAsync<GrpcCore.RpcException>(() => grpc.UpsertAsync(
            new global::Walhalla.VectorStore.Grpc.UpsertRequest
            {
                Collection = "grpc-invalid-metadata",
                Id = 1,
                MetadataJson = "{",
                Vector = { 1f, 0f, 0f }
            },
            headers: new GrpcCore.Metadata { { "x-api-key", factory.ApiKey } }).ResponseAsync);

        Assert.Equal(GrpcCore.StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("Invalid metadata JSON", ex.Status.Detail);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task CreateCollectionAsync(HttpClient client, string name, int dimension)
    {
        using var response = await client.PostAsJsonAsync("/api/collections", new
        {
            name,
            dimension,
            metric = "Euclidean",
            enableHnsw = true,
            enablePayloadIndex = true
        });

        response.EnsureSuccessStatusCode();
    }

    private static async Task PutVectorAsync(HttpClient client, string collection, ulong id, float[] vector, string body)
    {
        using var response = await client.PostAsJsonAsync($"/api/collections/{Uri.EscapeDataString(collection)}/vectors", new
        {
            id,
            vector,
            metadata = new Dictionary<string, object> { ["body"] = body }
        });

        response.EnsureSuccessStatusCode();
    }

    private sealed class VectorStoreApiFactory : WebApplicationFactory<WalhallaVectorStoreApi::Program>, IAsyncDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "walhalla-api-tests-" + Guid.NewGuid().ToString("N"));

        public string ApiKey => "walhalla-test-key";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ApiKey"] = ApiKey,
                    ["VectorStore:Path"] = _dbPath,
                });
            });
        }

        public HttpClient CreateApiClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
            return client;
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            Dispose();
            await Task.CompletedTask;
            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, recursive: true);
        }
    }
}