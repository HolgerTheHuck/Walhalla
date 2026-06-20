using Npgsql;
using WalhallaSql;
using WalhallaSql.PgWire;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Test helper that spins up a PgWireServer with SCRAM authentication enabled.
/// A single user is pre-created in the AuthIdCatalog.
/// </summary>
internal sealed class WalhallaSqlPgWireAuthTestScope : IAsyncDisposable
{
    private readonly string _tempPath;
    private readonly WalhallaEngine _engine;
    private readonly PgWireServer _server;
    private NpgsqlDataSource? _npgsqlDataSource;
    private readonly string _userName;
    private readonly string _password;

    private WalhallaSqlPgWireAuthTestScope(
        string tempPath,
        WalhallaEngine engine,
        PgWireServer server,
        string userName,
        string password)
    {
        _tempPath = tempPath;
        _engine = engine;
        _server = server;
        _userName = userName;
        _password = password;
    }

    public string ConnectionString =>
        $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id={_userName};Password={_password};Pooling=false;Timeout=5;Command Timeout=10";

    public WalhallaEngine Engine => _engine;

    public NpgsqlConnection OpenConnectionAs(string userName, string password)
    {
        var cs = $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id={userName};Password={password};Pooling=false;Timeout=5;Command Timeout=10";
        return new NpgsqlConnection(cs);
    }

    public static Task<WalhallaSqlPgWireAuthTestScope> CreateAsync(string userName, string password)
        => CreateAsync(userName, password, canLogin: true);

    public static async Task<WalhallaSqlPgWireAuthTestScope> CreateAsync(string userName, string password, bool canLogin)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSqlPgWireTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        var engine = new WalhallaEngine(new WalhallaOptions(tempPath));
        engine.AuthIdCatalog.CreateRole(userName, password, canLogin, isSuperuser: false);

        var backend = new WalhallaSqlPgWireBackend(engine);
        var server = new PgWireServer(backend, host: "127.0.0.1", port: 0);
        await server.StartAsync();

        return new WalhallaSqlPgWireAuthTestScope(tempPath, engine, server, userName, password);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        _npgsqlDataSource ??= NpgsqlDataSource.Create(ConnectionString);
        return await _npgsqlDataSource.OpenConnectionAsync();
    }

    public NpgsqlConnection OpenConnectionWithPassword(string password)
    {
        var cs = $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id={_userName};Password={password};Pooling=false;Timeout=5;Command Timeout=10";
        return new NpgsqlConnection(cs);
    }

    public async ValueTask DisposeAsync()
    {
        _npgsqlDataSource?.Dispose();
        await _server.DisposeAsync();
        _engine.Dispose();

        try { Directory.Delete(_tempPath, recursive: true); } catch { }
    }
}
