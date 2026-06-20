using Npgsql;
using WalhallaSql;
using WalhallaSql.PgWire;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Test helper that spins up a <see cref="PgWireServer"/> on an ephemeral port
/// backed by a temporary on-disk <see cref="WalhallaEngine"/> instance via
/// <see cref="WalhallaSqlPgWireBackend"/>. Dispose to shut everything down.
/// </summary>
internal sealed class WalhallaSqlPgWireTestScope : IAsyncDisposable
{
    private readonly string _tempPath;
    private readonly WalhallaEngine _engine;
    private readonly PgWireServer _server;
    private NpgsqlDataSource? _npgsqlDataSource;

    private WalhallaSqlPgWireTestScope(
        string tempPath,
        WalhallaEngine engine,
        PgWireServer server)
    {
        _tempPath = tempPath;
        _engine = engine;
        _server = server;
    }

    public string ConnectionString =>
        $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id=test;Password=test;Pooling=false;Timeout=5;Command Timeout=10";

    public int Port => _server.BoundPort;

    public WalhallaEngine Engine => _engine;

    public static async Task<WalhallaSqlPgWireTestScope> CreateAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSqlPgWireTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        var engine = new WalhallaEngine(new WalhallaOptions(tempPath));
        engine.AuthIdCatalog.CreateRole("test", "test", canLogin: true, isSuperuser: true);

        var backend = new WalhallaSqlPgWireBackend(engine);

        var server = new PgWireServer(backend, host: "127.0.0.1", port: 0);
        await server.StartAsync();

        return new WalhallaSqlPgWireTestScope(tempPath, engine, server);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        _npgsqlDataSource ??= NpgsqlDataSource.Create(ConnectionString);
        return await _npgsqlDataSource.OpenConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _npgsqlDataSource?.Dispose();
        await _server.DisposeAsync();
        _engine.Dispose();

        try { Directory.Delete(_tempPath, recursive: true); } catch { }
    }
}
