using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Walhalla.Indexes.FullText;
using Walhalla.Storage.Trees;
using Walhalla.Storage.Core.Configuration;
using Walhalla.Storage.Contract;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Api;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Filtering;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Configure CORS for the Svelte UI
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUI", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:4173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Singleton VectorStore service
builder.Services.AddSingleton<VectorStoreService>();
builder.Services.AddTransient<ApiKeyGrpcInterceptor>();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ApiKeyGrpcInterceptor>();
});

// Health Checks
builder.Services.AddHealthChecks().AddCheck<VectorStoreHealthCheck>("vectorstore");

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Problem Details for consistent error responses
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowUI");
app.MapGrpcService<VectorStoreGrpcService>();

// Exception Handling
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred" });
    });
});

// Swagger (Development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health Checks
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready");

// Simple API Key middleware
var apiKey = builder.Configuration["ApiKey"] ?? "walhalla-dev-key";
app.Use(async (context, next) =>
{
    // Skip API key check for CORS preflight requests
    if (context.Request.Method == "OPTIONS")
    {
        await next();
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var providedKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
        if (providedKey != apiKey)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api");

// ── Collections ───────────────────────────────────────────────────────────

api.MapGet("/collections", (VectorStoreService store) =>
{
    var collections = store.GetCollections();
    return Results.Ok(collections.Select(c => new CollectionDto
    {
        Name = c.Name,
        Dimension = c.Dimension,
        Metric = c.DefaultMetric.ToString(),
        Count = c.Count,
        HnswEnabled = c.Index != null,
        IvfEnabled = c.IvfIndex != null,
        PayloadIndexWarm = c.GetManifest().PayloadIndexWarm,
        PayloadIndexVersion = c.GetManifest().PayloadIndexVersion,
        ChangeSequence = c.GetManifest().ChangeSequence
    }));
});

api.MapPost("/collections", (CreateCollectionRequest req, VectorStoreService store) =>
{
    try
    {
        var collection = store.GetOrCreateCollection(req.Name, req.Dimension, Enum.Parse<DistanceMetric>(req.Metric), req.EnableHnsw, enablePayloadIndex: req.EnablePayloadIndex, enableIvf: req.EnableIvf);
        return Results.Ok(new CollectionDto
        {
            Name = collection.Name,
            Dimension = collection.Dimension,
            Metric = collection.DefaultMetric.ToString(),
            Count = collection.Count,
            HnswEnabled = collection.Index != null,
            IvfEnabled = collection.IvfIndex != null,
            PayloadIndexWarm = collection.GetManifest().PayloadIndexWarm,
            PayloadIndexVersion = collection.GetManifest().PayloadIndexVersion,
            ChangeSequence = collection.GetManifest().ChangeSequence
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/collections/{name}", (string name, VectorStoreService store) =>
{
    store.DeleteCollection(name);
    return Results.NoContent();
});

api.MapGet("/collections/{name}/manifest", (string name, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    return Results.Ok(collection.GetManifest());
});

api.MapGet("/collections/{name}/changes", async (string name, long? after, HttpContext context, VectorStoreService store, CancellationToken ct) =>
{
    var collection = store.GetCollection(name);
    if (collection == null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = $"Collection '{name}' not found" }, ct);
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    await foreach (var change in collection.ReadChangesAsync(after ?? 0, ct))
    {
        var json = JsonSerializer.Serialize(change);
        await context.Response.WriteAsync($"id: {change.Sequence}\n", ct);
        await context.Response.WriteAsync("event: change\n", ct);
        await context.Response.WriteAsync($"data: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

// ── Vectors ─────────────────────────────────────────────────────────────────

api.MapGet("/collections/{name}/vectors", async (string name, int limit, int offset, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    var vectors = new List<VectorEntryDto>();
    var count = 0;
    await foreach (var id in collection.EnumerateIdsAsync())
    {
        if (count >= offset + limit) break;
        if (count >= offset)
        {
            var entry = await collection.GetAsync(id);
            if (entry != null)
            {
                vectors.Add(new VectorEntryDto
                {
                    Id = entry.Id,
                    Dimension = entry.Vector.Dimension,
                    Metadata = entry.Metadata?.Payload
                });
            }
        }
        count++;
    }

    return Results.Ok(vectors);
});

api.MapGet("/collections/{name}/vectors/{id:long}", async (string name, long id, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    var entry = await collection.GetAsync((ulong)id);
    if (entry == null) return Results.NotFound(new { error = $"Vector {id} not found" });

    return Results.Ok(new VectorEntryDto
    {
        Id = entry.Id,
        Dimension = entry.Vector.Dimension,
        Metadata = entry.Metadata?.Payload
    });
});

api.MapPost("/collections/{name}/vectors", async (string name, PutVectorRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    try
    {
        var vector = new Vector(req.Vector);
        var metadata = req.Metadata != null ? new VectorMetadata
        {
            Id = req.Id,
            Collection = name,
            Payload = req.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
        } : null;

        await collection.PutAsync(req.Id, vector, metadata);
        return Results.Ok(new { id = req.Id });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/collections/{name}/vectors/{id:long}", async (string name, long id, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    await collection.DeleteAsync((ulong)id);
    return Results.NoContent();
});

api.MapPost("/collections/{name}/vectors/batch", async (string name, PutVectorRequest[] reqs, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    try
    {
        var items = reqs.Select(req =>
        {
            var vector = new Vector(req.Vector);
            var metadata = req.Metadata != null ? new VectorMetadata
            {
                Id = req.Id,
                Collection = name,
                Payload = req.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value)
            } : null;
            return (req.Id, vector, metadata);
        });

        await collection.PutBatchAsync(items);
        return Results.Ok(new { imported = reqs.Length });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Search ──────────────────────────────────────────────────────────────────

api.MapPost("/collections/{name}/search/exact", async (string name, SearchRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    FilterClause? filter = null;
    if (req.Filter is not null)
    {
        try
        {
            filter = FilterParser.Parse(req.Filter);
        }
        catch (FilterParseException ex)
        {
            return Results.BadRequest(new { error = $"Invalid filter: {ex.Message}" });
        }
    }

    try
    {
        var query = new Vector(req.Vector);
        var results = new List<SearchResultDto>();

        await foreach (var result in collection.SearchExactAsync(query, req.TopK, filter))
        {
            results.Add(new SearchResultDto
            {
                Id = result.Id,
                Score = result.Score,
                Metadata = result.Metadata?.Payload
            });
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/collections/{name}/search/hnsw", async (string name, SearchRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    FilterClause? filter = null;
    if (req.Filter is not null)
    {
        try
        {
            filter = FilterParser.Parse(req.Filter);
        }
        catch (FilterParseException ex)
        {
            return Results.BadRequest(new { error = $"Invalid filter: {ex.Message}" });
        }
    }

    try
    {
        var query = new Vector(req.Vector);
        var results = new List<SearchResultDto>();

        await foreach (var result in collection.SearchHnswAsync(query, req.TopK, req.Ef, filter))
        {
            results.Add(new SearchResultDto
            {
                Id = result.Id,
                Score = result.Score,
                Metadata = result.Metadata?.Payload
            });
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/collections/{name}/search/ivf", async (string name, SearchRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    FilterClause? filter = null;
    if (req.Filter is not null)
    {
        try
        {
            filter = FilterParser.Parse(req.Filter);
        }
        catch (FilterParseException ex)
        {
            return Results.BadRequest(new { error = $"Invalid filter: {ex.Message}" });
        }
    }

    try
    {
        var query = new Vector(req.Vector);
        var results = new List<SearchResultDto>();

        await foreach (var result in collection.SearchIvfAsync(query, req.TopK, req.Nprobe, filter))
        {
            results.Add(new SearchResultDto
            {
                Id = result.Id,
                Score = result.Score,
                Metadata = result.Metadata?.Payload
            });
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/collections/{name}/search/text", async (string name, TextSearchRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    if (!TextQueryModeParser.TryParse(req.Mode, out var mode))
        return Results.BadRequest(new { error = $"Invalid text query mode '{req.Mode}'. Expected: all or any." });

    try
    {
        var results = new List<SearchResultDto>();

        await foreach (var result in collection.SearchTextAsync(req.Field, req.Query, req.TopK, mode, req.NotQuery))
        {
            results.Add(new SearchResultDto
            {
                Id = result.Id,
                Score = result.Score,
                Metadata = result.Metadata?.Payload
            });
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/collections/{name}/search/hybrid", async (string name, HybridSearchRequest req, VectorStoreService store) =>
{
    var collection = store.GetCollection(name);
    if (collection == null) return Results.NotFound(new { error = $"Collection '{name}' not found" });

    if (!TextQueryModeParser.TryParse(req.Mode, out var mode))
        return Results.BadRequest(new { error = $"Invalid text query mode '{req.Mode}'. Expected: all or any." });

    try
    {
        var query = new Vector(req.Vector);
        var results = new List<SearchResultDto>();

        await foreach (var result in collection.SearchHybridAsync(req.Field, req.TextQuery, query, req.TopK, req.TextCandidateCount, mode, req.NotQuery))
        {
            results.Add(new SearchResultDto
            {
                Id = result.Id,
                Score = result.Score,
                Metadata = result.Metadata?.Payload
            });
        }

        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Stats ───────────────────────────────────────────────────────────────────

api.MapGet("/stats", (VectorStoreService store) =>
{
    var collections = store.GetCollections();
    return Results.Ok(new
    {
        Collections = collections.Count,
        TotalVectors = collections.Sum(c => c.Count),
        Uptime = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString("c")
    });
});

app.Run();

public partial class Program;

// ── DTOs ────────────────────────────────────────────────────────────────────

public class CollectionDto
{
    public required string Name { get; set; }
    public int Dimension { get; set; }
    public required string Metric { get; set; }
    public int Count { get; set; }
    public bool HnswEnabled { get; set; }
    public bool IvfEnabled { get; set; }
    public bool PayloadIndexWarm { get; set; }
    public int PayloadIndexVersion { get; set; }
    public long ChangeSequence { get; set; }
}

public class VectorEntryDto
{
    public ulong Id { get; set; }
    public int Dimension { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public class SearchResultDto
{
    public ulong Id { get; set; }
    public float Score { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public class CreateCollectionRequest
{
    public required string Name { get; set; }
    public int Dimension { get; set; } = 128;
    public string Metric { get; set; } = "Cosine";
    public bool EnableHnsw { get; set; } = true;
    public bool EnableIvf { get; set; } = false;
    public bool EnablePayloadIndex { get; set; } = true;
}

public class PutVectorRequest
{
    public ulong Id { get; set; }
    public required float[] Vector { get; set; }
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

public class SearchRequest
{
    public required float[] Vector { get; set; }
    public int TopK { get; set; } = 10;
    public int? Ef { get; set; }
    public int? Nprobe { get; set; }
    public Dictionary<string, JsonElement>? Filter { get; set; }
}

public class TextSearchRequest
{
    public required string Field { get; set; }
    public required string Query { get; set; }
    public int TopK { get; set; } = 10;
    public string Mode { get; set; } = "all";
    public string? NotQuery { get; set; }
}

public class HybridSearchRequest
{
    public required string Field { get; set; }
    public required string TextQuery { get; set; }
    public required float[] Vector { get; set; }
    public int TopK { get; set; } = 10;
    public int TextCandidateCount { get; set; } = 50;
    public string Mode { get; set; } = "all";
    public string? NotQuery { get; set; }
}

public static class TextQueryModeParser
{
    public static bool TryParse(string? mode, out FullTextQueryMode parsedMode)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = FullTextQueryMode.All;
            return true;
        }

        if (string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = FullTextQueryMode.Any;
            return true;
        }

        parsedMode = default;
        return false;
    }
}

// ── Service ───────────────────────────────────────────────────────────────

public class VectorStoreService : IDisposable
{
    private readonly IKeyValueStore _store;
    private readonly VectorCollectionManager _manager;
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _disposed;

    public VectorStoreService(IConfiguration config)
    {
        var dbPath = config["VectorStore:Path"] ?? Path.Combine(Path.GetTempPath(), "walhalla-api");
        Directory.CreateDirectory(dbPath);

        var backendString = config["VectorStore:Backend"];
        var backend = Enum.TryParse<StorageBackend>(backendString, out var parsed)
            ? parsed
            : StorageBackend.MvccBPlusTree; // Default in M5 = MvccBPlusTree für neuen API-Host

        if (backend == StorageBackend.MvccBPlusTree)
        {
            var options = new StorageEngineOptions
            {
                RootPath = dbPath,
                Backend = StorageBackend.MvccBPlusTree,
                OverflowThresholdBytes = 256
            };
            _store = new MvccBPlusTreeStore(
                odsPath: Path.Combine(dbPath, "data.ods"),
                walPath: Path.Combine(dbPath, "wal.dat"),
                walSyncMode: WalSyncMode.Fsync);
        }
        else
        {
            // Legacy-Pfad (BPlusTree / BlobStore)
            var blobStore = new BlobStore(new BlobStoreOptions(dbPath));
            _store = new BlobStoreIKeyValueAdapter(blobStore);
        }

        _manager = new VectorCollectionManager(_store);
    }

    public IReadOnlyList<VectorCollection> GetCollections()
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.GetCollections();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public VectorCollection? GetCollection(string name)
    {
        _lock.EnterReadLock();
        try
        {
            return _manager.GetCollection(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public VectorCollection GetOrCreateCollection(string name, int dimension, DistanceMetric metric, bool enableHnsw, bool enablePayloadIndex = true, bool enableIvf = false)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            var existing = _manager.GetCollection(name);
            if (existing != null)
            {
                if (existing.Dimension != dimension)
                    throw new ArgumentException($"Collection '{name}' exists with dimension {existing.Dimension}, requested {dimension}");
                if (existing.DefaultMetric != metric)
                    throw new ArgumentException($"Collection '{name}' exists with metric {existing.DefaultMetric}, requested {metric}");
                return existing;
            }

            _lock.EnterWriteLock();
            try
            {
                return _manager.GetOrCreateCollection(name, dimension, metric, enableHnsw, enablePayloadIndex: enablePayloadIndex, enableIvf: enableIvf);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public void DeleteCollection(string name)
    {
        _lock.EnterWriteLock();
        try
        {
            _manager.DeleteCollection(name);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _manager.Dispose();
        _store.Dispose();
        _lock.Dispose();
        _disposed = true;
    }
}

public class VectorStoreHealthCheck : IHealthCheck
{
    private readonly VectorStoreService _store;

    public VectorStoreHealthCheck(VectorStoreService store)
    {
        _store = store;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = _store.GetCollections();
            return Task.FromResult(HealthCheckResult.Healthy("VectorStore is operational"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"VectorStore failed: {ex.Message}"));
        }
    }
}
