using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WalhallaSql.AdoNet;

/// <summary>
/// High-level helper for PgWire over WebSocket.
/// Starts a local bridge and exposes a ready-to-use WalhallaSql/Npgsql-style
/// PgWire connection string against the local loopback port.
/// </summary>
public sealed class WalhallaSqlPgWireWebSocketTunnel : IAsyncDisposable
{
    private readonly PgWireWebSocketProxy _proxy;
    private readonly string _database;
    private readonly string _localHost;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string? _extraConnectionStringSegments;

    public WalhallaSqlPgWireWebSocketTunnel(
        string remoteUri,
        string database,
        string localHost = "127.0.0.1",
        string? username = null,
        string? password = null,
        string? extraConnectionStringSegments = null)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("Database name must not be empty.", nameof(database));

        RemoteUri = remoteUri ?? throw new ArgumentNullException(nameof(remoteUri));
        _database = database.Trim();
        _localHost = string.IsNullOrWhiteSpace(localHost) ? "127.0.0.1" : localHost.Trim();
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        _extraConnectionStringSegments = string.IsNullOrWhiteSpace(extraConnectionStringSegments)
            ? null
            : extraConnectionStringSegments.Trim().Trim(';');
        _proxy = new PgWireWebSocketProxy(RemoteUri);
    }

    public string RemoteUri { get; }

    public int BoundPort => _proxy.BoundPort;

    public string ConnectionString => BuildConnectionString();

    public static async Task<WalhallaSqlPgWireWebSocketTunnel> StartAsync(
        string remoteUri,
        string database,
        string localHost = "127.0.0.1",
        string? username = null,
        string? password = null,
        string? extraConnectionStringSegments = null,
        int localPort = 0,
        CancellationToken cancellationToken = default)
    {
        var tunnel = new WalhallaSqlPgWireWebSocketTunnel(remoteUri, database, localHost, username, password, extraConnectionStringSegments);
        await tunnel.StartAsync(localPort, cancellationToken).ConfigureAwait(false);
        return tunnel;
    }

    public Task StartAsync(int localPort = 0, CancellationToken cancellationToken = default)
        => _proxy.StartAsync(localPort, cancellationToken);

    public WalhallaSqlDbConnection CreateConnection()
        => new(ConnectionString);

    public WalhallaSqlDbConnection CreateOpenConnection()
    {
        var connection = CreateConnection();
        connection.Open();
        return connection;
    }

    public async Task<WalhallaSqlDbConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public ValueTask DisposeAsync()
        => _proxy.DisposeAsync();

    private string BuildConnectionString()
    {
        if (BoundPort <= 0)
            throw new InvalidOperationException("The WebSocket tunnel has not been started yet.");

        var builder = new StringBuilder();
        builder.Append("Transport=PgWire");
        builder.Append(';').Append("Host=").Append(_localHost);
        builder.Append(';').Append("Port=").Append(BoundPort);
        builder.Append(';').Append("Database=").Append(_database);

        if (!string.IsNullOrWhiteSpace(_username))
            builder.Append(';').Append("Username=").Append(_username);

        if (!string.IsNullOrWhiteSpace(_password))
            builder.Append(';').Append("Password=").Append(_password);

        if (!string.IsNullOrWhiteSpace(_extraConnectionStringSegments))
            builder.Append(';').Append(_extraConnectionStringSegments);

        return builder.ToString();
    }
}