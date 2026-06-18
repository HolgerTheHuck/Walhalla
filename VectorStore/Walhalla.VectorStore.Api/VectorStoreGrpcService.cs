// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using GrpcCore = global::Grpc.Core;
using Walhalla.Indexes.FullText;
using Walhalla.VectorStore.Filtering;

namespace Walhalla.VectorStore.Api;

/// <summary>
/// gRPC-Service fuer Walhalla.VectorStore.
/// </summary>
public class VectorStoreGrpcService : global::Walhalla.VectorStore.Grpc.VectorStore.VectorStoreBase
{
    private readonly VectorStoreService _store;

    public VectorStoreGrpcService(VectorStoreService store)
    {
        _store = store;
    }

    public override Task<global::Walhalla.VectorStore.Grpc.CollectionInfo> CreateCollection(
        global::Walhalla.VectorStore.Grpc.CreateCollectionRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetOrCreateCollection(
            request.Name,
            request.Dimension,
            Enum.Parse<Walhalla.VectorStore.DistanceMetric>(request.Metric),
            request.EnableHnsw,
            enablePayloadIndex: request.EnablePayloadIndex);

        return Task.FromResult(new global::Walhalla.VectorStore.Grpc.CollectionInfo
        {
            Name = collection.Name,
            Dimension = collection.Dimension,
            Metric = collection.DefaultMetric.ToString(),
            Count = collection.Count,
            HnswEnabled = collection.Index != null
        });
    }

    public override Task<global::Walhalla.VectorStore.Grpc.Empty> DeleteCollection(
        global::Walhalla.VectorStore.Grpc.DeleteCollectionRequest request, GrpcCore.ServerCallContext context)
    {
        _store.DeleteCollection(request.Name);
        return Task.FromResult(new global::Walhalla.VectorStore.Grpc.Empty());
    }

    public override Task<global::Walhalla.VectorStore.Grpc.CollectionList> ListCollections(
        global::Walhalla.VectorStore.Grpc.Empty request, GrpcCore.ServerCallContext context)
    {
        var collections = _store.GetCollections();
        var list = new global::Walhalla.VectorStore.Grpc.CollectionList();
        foreach (var c in collections)
        {
            list.Collections.Add(new global::Walhalla.VectorStore.Grpc.CollectionInfo
            {
                Name = c.Name,
                Dimension = c.Dimension,
                Metric = c.DefaultMetric.ToString(),
                Count = c.Count,
                HnswEnabled = c.Index != null
            });
        }
        return Task.FromResult(list);
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.Empty> Upsert(
        global::Walhalla.VectorStore.Grpc.UpsertRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        var vector = new Walhalla.VectorStore.Vector(request.Vector.ToArray());
        Walhalla.VectorStore.VectorMetadata? metadata = null;
        if (!string.IsNullOrEmpty(request.MetadataJson))
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(request.MetadataJson);
                metadata = new Walhalla.VectorStore.VectorMetadata
                {
                    Id = request.Id,
                    Collection = request.Collection,
                    Payload = payload
                };
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new GrpcCore.RpcException(
                    new GrpcCore.Status(GrpcCore.StatusCode.InvalidArgument, $"Invalid metadata JSON: {ex.Message}"));
            }
        }

        await collection.PutAsync(request.Id, vector, metadata);
        return new global::Walhalla.VectorStore.Grpc.Empty();
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.VectorEntry> Get(
        global::Walhalla.VectorStore.Grpc.GetRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        var entry = await collection.GetAsync(request.Id);
        if (entry == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Vector {request.Id} not found"));

        return new global::Walhalla.VectorStore.Grpc.VectorEntry
        {
            Id = entry.Id,
            Dimension = entry.Vector.Dimension,
            MetadataJson = SerializePayloadMetadata(entry.Metadata)
        };
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.Empty> Delete(
        global::Walhalla.VectorStore.Grpc.DeleteRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        await collection.DeleteAsync(request.Id);
        return new global::Walhalla.VectorStore.Grpc.Empty();
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.SearchResponse> Search(
        global::Walhalla.VectorStore.Grpc.SearchRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        FilterClause? filter = null;
        if (!string.IsNullOrEmpty(request.FilterJson))
        {
            try
            {
                filter = FilterParser.Parse(request.FilterJson);
            }
            catch (FilterParseException ex)
            {
                throw new GrpcCore.RpcException(
                    new GrpcCore.Status(GrpcCore.StatusCode.InvalidArgument, $"Invalid filter: {ex.Message}"));
            }
        }

        var query = new Walhalla.VectorStore.Vector(request.Vector.ToArray());
        var response = new global::Walhalla.VectorStore.Grpc.SearchResponse();
        await foreach (var r in collection.SearchHnswAsync(query, request.TopK, request.HasEf ? request.Ef : null, filter))
        {
            response.Results.Add(new global::Walhalla.VectorStore.Grpc.SearchResult
            {
                Id = r.Id,
                Score = r.Score,
                MetadataJson = SerializePayloadMetadata(r.Metadata)
            });
        }
        return response;
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.SearchResponse> SearchExact(
        global::Walhalla.VectorStore.Grpc.SearchRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        FilterClause? filter = null;
        if (!string.IsNullOrEmpty(request.FilterJson))
        {
            try
            {
                filter = FilterParser.Parse(request.FilterJson);
            }
            catch (FilterParseException ex)
            {
                throw new GrpcCore.RpcException(
                    new GrpcCore.Status(GrpcCore.StatusCode.InvalidArgument, $"Invalid filter: {ex.Message}"));
            }
        }

        var query = new Walhalla.VectorStore.Vector(request.Vector.ToArray());
        var response = new global::Walhalla.VectorStore.Grpc.SearchResponse();
        await foreach (var r in collection.SearchExactAsync(query, request.TopK, filter))
        {
            response.Results.Add(new global::Walhalla.VectorStore.Grpc.SearchResult
            {
                Id = r.Id,
                Score = r.Score,
                MetadataJson = SerializePayloadMetadata(r.Metadata)
            });
        }
        return response;
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.SearchResponse> SearchText(
        global::Walhalla.VectorStore.Grpc.TextSearchRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        var response = new global::Walhalla.VectorStore.Grpc.SearchResponse();
        await foreach (var r in collection.SearchTextAsync(request.Field, request.Query, request.TopK, MapFullTextQueryMode(request.Mode), string.IsNullOrEmpty(request.NotQuery) ? null : request.NotQuery))
        {
            response.Results.Add(new global::Walhalla.VectorStore.Grpc.SearchResult
            {
                Id = r.Id,
                Score = r.Score,
                MetadataJson = SerializePayloadMetadata(r.Metadata)
            });
        }

        return response;
    }

    public override async Task<global::Walhalla.VectorStore.Grpc.SearchResponse> SearchHybrid(
        global::Walhalla.VectorStore.Grpc.HybridSearchRequest request, GrpcCore.ServerCallContext context)
    {
        var collection = _store.GetCollection(request.Collection);
        if (collection == null)
            throw new GrpcCore.RpcException(new GrpcCore.Status(GrpcCore.StatusCode.NotFound, $"Collection '{request.Collection}' not found"));

        var query = new Walhalla.VectorStore.Vector(request.Vector.ToArray());
        var response = new global::Walhalla.VectorStore.Grpc.SearchResponse();
        await foreach (var r in collection.SearchHybridAsync(request.Field, request.TextQuery, query, request.TopK, request.TextCandidateCount, MapFullTextQueryMode(request.Mode), string.IsNullOrEmpty(request.NotQuery) ? null : request.NotQuery))
        {
            response.Results.Add(new global::Walhalla.VectorStore.Grpc.SearchResult
            {
                Id = r.Id,
                Score = r.Score,
                MetadataJson = SerializePayloadMetadata(r.Metadata)
            });
        }

        return response;
    }

    private static FullTextQueryMode MapFullTextQueryMode(global::Walhalla.VectorStore.Grpc.FullTextQueryMode mode)
    {
        return mode switch
        {
            global::Walhalla.VectorStore.Grpc.FullTextQueryMode.Any => FullTextQueryMode.Any,
            _ => FullTextQueryMode.All,
        };
    }

    private static string SerializePayloadMetadata(Walhalla.VectorStore.VectorMetadata? metadata)
    {
        return metadata?.Payload is null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(metadata.Payload);
    }
}
