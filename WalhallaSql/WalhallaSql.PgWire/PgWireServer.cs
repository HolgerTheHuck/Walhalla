using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WalhallaSql;
using WalhallaSql.Parsing;

namespace WalhallaSql.PgWire;


/// <summary>
/// Self-contained PostgreSQL wire-protocol server.
/// Start with <see cref="StartAsync"/> and stop with <see cref="Stop"/>.
/// <see cref="BoundPort"/> returns the actual port after binding (useful when port=0 is passed for ephemeral ports).
/// </summary>
public sealed class PgWireServer : IAsyncDisposable
{
    private readonly IPgWireBackendConnection _backend;
    private readonly string _host;
    private readonly int _port;
    // Null = TCP mode; non-null = Unix Domain Socket mode.
    private readonly string? _unixSocketDirectory;
    // True = WebSocket mode: accept raw TCP, then perform HTTP/WS upgrade before PG protocol.
    private readonly bool   _wsMode;
    private readonly string _wsPath = "/pgwire";
    private readonly Tls.PgWireTlsOptions? _tlsOptions;
    // Single server socket for TCP, UDS, and WebSocket modes. � avoids TcpListener wrapper.
    private Socket? _serverSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    // Tracks all fire-and-forget handler tasks so DisposeAsync can await them
    // before the engine is torn down.  Without this, handlers may still call
    // Commit() while WalhallaStore.Dispose() is running � causing a deadlock.
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _handlerTasks = new();

    /// <summary>TCP mode constructor.</summary>
    public PgWireServer(IPgWireBackendConnection backend, string host = "127.0.0.1", int port = 5432)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _host = host ?? "127.0.0.1";
        _port = port;
    }

    /// <summary>TCP mode constructor with TLS.</summary>
    public PgWireServer(IPgWireBackendConnection backend, Tls.PgWireTlsOptions tlsOptions, string host = "127.0.0.1", int port = 5432)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _tlsOptions = tlsOptions ?? throw new ArgumentNullException(nameof(tlsOptions));
        _host = host ?? "127.0.0.1";
        _port = port;
    }

    /// <summary>WebSocket mode constructor (private — use <see cref="CreateWithWebSocket"/>).</summary>
    private PgWireServer(IPgWireBackendConnection backend, string host, int port, string wsPath)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _host   = host;
        _port   = port;
        _wsMode = true;
        _wsPath = wsPath;
    }

    /// <summary>Unix Domain Socket mode constructor (private — use <see cref="CreateWithUnixSocket"/>).</summary>
    private PgWireServer(IPgWireBackendConnection backend, string unixSocketDirectory, bool _)
    {
        _backend               = backend               ?? throw new ArgumentNullException(nameof(backend));
        _unixSocketDirectory   = unixSocketDirectory   ?? throw new ArgumentNullException(nameof(unixSocketDirectory));
        _host = string.Empty;
        _port = 0;
    }

    /// <summary>
    /// Creates a server that listens on a Unix Domain Socket instead of TCP.
    /// Npgsql connects via <c>Host=&lt;socketDirectory&gt;;Port=5432</c>.
    /// Bypasses the TCP stack entirely — significantly lower latency for local connections.
    /// </summary>
    public static PgWireServer CreateWithUnixSocket(IPgWireBackendConnection backend, string socketDirectory)
        => new(backend, socketDirectory, false);

    /// <summary>
    /// Creates a server that accepts WebSocket connections on an HTTP port.
    /// Enables connectivity through firewalls that only
    /// permit HTTP/HTTPS traffic.
    /// </summary>
    /// <param name="host">Bind address; <c>"0.0.0.0"</c> listens on all interfaces.</param>
    /// <param name="port">HTTP port; 80 or 443 require OS-level permission on most systems.</param>
    /// <param name="path">WebSocket upgrade path the client must request, e.g. <c>/pgwire</c>.</param>
    public static PgWireServer CreateWithWebSocket(
        IPgWireBackendConnection backend,
        string host = "0.0.0.0",
        int    port = 8080,
        string path = "/pgwire")
        => new(backend, host, port, path);

    /// <summary>Actual port this server is bound to (set after <see cref="StartAsync"/>).</summary>
    public int BoundPort { get; private set; }

    /// <summary>Full path to the Unix socket file, or <c>null</c> when running in TCP mode.</summary>
    public string? UnixSocketPath { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_unixSocketDirectory != null)
        {
            // Unix Domain Socket mode.
            // Npgsql expects the socket file at {dir}/.s.PGSQL.{port}.
            const int udsPort = 5432;
            UnixSocketPath = Path.Combine(_unixSocketDirectory, $".s.PGSQL.{udsPort}");
            if (File.Exists(UnixSocketPath)) File.Delete(UnixSocketPath);

            _serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _serverSocket.Bind(new UnixDomainSocketEndPoint(UnixSocketPath));
            _serverSocket.Listen(128);
            BoundPort = udsPort;
        }
        else
        {
            // TCP mode � use raw Socket so both modes share the same AcceptAsync path.
            // IPAddress.Parse("0.0.0.0") == Any, so WebSocket mode with host="0.0.0.0" binds all interfaces.
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.Bind(new IPEndPoint(IPAddress.Parse(_host), _port));
            _serverSocket.Listen(128);
            BoundPort = ((IPEndPoint)_serverSocket.LocalEndPoint!).Port;
        }

        _acceptLoop = RunAcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _serverSocket?.Close(); } catch { /* expected on cancellation */ }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();  // cancels _cts, closes server socket
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        // Wait for every active handler to finish.  Handlers observe _cts cancellation
        // so they should wind down quickly; the 10-second timeout is a safety net.
        if (_handlerTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(_handlerTasks)
                          .WaitAsync(TimeSpan.FromSeconds(10))
                          .ConfigureAwait(false);
            }
            catch { /* best-effort � any remaining handlers will fail once the engine disposes */ }
        }

        _cts?.Dispose();

        // Remove the socket file so a subsequent server on the same path can bind cleanly.
        if (UnixSocketPath != null)
            try { File.Delete(UnixSocketPath); } catch { }
    }

    // -----------------------------------------------------------------------------
    // Accept loop
    // -----------------------------------------------------------------------------

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket clientSocket;
            try
            {
                clientSocket = await _serverSocket!.AcceptAsync(ct).ConfigureAwait(false);
                // Disable Nagle for TCP connections; no-op / harmless for UDS.
                if (_unixSocketDirectory == null)
                    clientSocket.NoDelay = true;
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            if (_wsMode)
            {
                // WebSocket mode: perform the HTTP upgrade handshake in the background task,
                // then feed the resulting WebSocket as a Stream into the PG protocol handler.
                _handlerTasks.Add(Task.Run(async () =>
                {
                    using var ns = new NetworkStream(clientSocket, ownsSocket: true);
                    var ws = await TryUpgradeToWebSocketAsync(ns, ct).ConfigureAwait(false);
                    if (ws == null) return; // Not a valid WS upgrade; connection closed.
                    using var wsStream = new WebSocketStream(ws); // owns ws
                    await HandleClientCoreAsync(wsStream, ct).ConfigureAwait(false);
                }, CancellationToken.None));
            }
            else
            {
                _handlerTasks.Add(Task.Run(() => HandleClientAsync(clientSocket, _cts!.Token), CancellationToken.None));
            }
        }
    }

    // -----------------------------------------------------------------------------
    // Per-connection handler
    // -----------------------------------------------------------------------------

    private async Task HandleClientAsync(Socket socket, CancellationToken ct = default)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        await HandleClientCoreAsync(stream, ct);
    }

    // Core PG wire-protocol handler.  Accepts any bidirectional Stream so that
    // TCP, Unix Domain Socket, and WebSocket transports all share one implementation.
    private async Task HandleClientCoreAsync(Stream stream, CancellationToken ct = default)
    {
        var backend = _backend;

        PgDbSessionState session = new();

        try
        {
            var (startupOk, userName, sslAccepted) = await HandleStartupAsync(stream);
            if (!startupOk)
                return;

            // If SSL was requested and accepted, perform TLS handshake before proceeding.
            if (sslAccepted && _tlsOptions?.Certificate != null)
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: true);
                await sslStream.AuthenticateAsServerAsync(
                    _tlsOptions.Certificate,
                    clientCertificateRequired: false,
                    _tlsOptions.MinProtocolVersion,
                    checkCertificateRevocation: false);
                stream = sslStream;

                // After TLS handshake the client sends a fresh startup packet.
                (startupOk, userName, _) = await HandleStartupAsync(stream);
                if (!startupOk)
                    return;
            }

            session.UserName = userName;

            // Authentication
            if (!string.IsNullOrEmpty(userName) && backend.TryGetStoredHash(userName, out var storedHash))
            {
                await SendAuthenticationSaslAsync(stream, new[] { "SCRAM-SHA-256" });
                session.AuthState = PgAuthState.SASL;

                // Wait for SASLInitialResponse (message type 'p')
                var (mechanism, saslInit) = await ReadSaslInitialResponseAsync(stream);
                if (mechanism == null) return;
                if (mechanism != "SCRAM-SHA-256")
                {
                    await SendErrorAsync(stream, "0A000", $"SASL mechanism '{mechanism}' is not supported.");
                    return;
                }

                session.ScramServer = new WalhallaSql.PgWire.Auth.ScramSha256Server(storedHash);
                var serverFirst = session.ScramServer.Begin(saslInit);
                await SendAuthenticationSaslContinueAsync(stream, serverFirst);
                session.AuthState = PgAuthState.SASLContinue;

                // Wait for SASLResponse (message type 'p')
                var saslResponse = await ReadSaslResponseAsync(stream);
                if (saslResponse == null) return;

                try
                {
                    var serverFinal = session.ScramServer.Continue(saslResponse);
                    await SendAuthenticationSaslFinalAsync(stream, serverFinal);
                    await SendAuthenticationOkAsync(stream);
                }
                catch (Exception ex)
                {
                    await SendErrorAsync(stream, "28P01", ex.Message);
                    return;
                }
            }
            else
            {
                await SendAuthenticationOkAsync(stream);
            }
            session.AuthState = PgAuthState.Authenticated;

            await SendParameterStatusAsync(stream, "server_version", "16.0");
            await SendParameterStatusAsync(stream, "server_encoding", "UTF8");
            await SendParameterStatusAsync(stream, "client_encoding", "UTF8");
            await SendParameterStatusAsync(stream, "DateStyle", "ISO, MDY");
            await SendParameterStatusAsync(stream, "integer_datetimes", "on");
            await SendParameterStatusAsync(stream, "standard_conforming_strings", "on");
            await SendBackendKeyDataAsync(stream);
            await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');

            while (true)
            {
                var messageType = await ReadByteAsync(stream);
                if (messageType == null)
                    break;

                var length = await ReadInt32Async(stream);
                if (length < 4)
                    throw new InvalidOperationException("Invalid frontend message length.");

                var payload = await ReadExactlyAsync(stream, length - 4);

                var type = (char)messageType.Value;
                PgWireTrace.Frontend(type, length, payload.Length);

                if (session.IgnoreUntilSync && type != 'S' && type != 'X')
                    continue;

                try
                {
                    switch (type)
                    {
                        case 'Q':
                            await HandleSimpleQueryAsync(stream, backend, session, payload);
                            break;

                        case 'P':
                            HandleParse(session, payload);
                            await SendParseCompleteAsync(stream);
                            break;

                        case 'B':
                            HandleBind(backend, session, payload);
                            await SendBindCompleteAsync(stream);
                            break;

                        case 'D':
                            await HandleDescribeAsync(stream, backend, session, payload);
                            break;

                        case 'E':
                            await HandleExecuteAsync(stream, backend, session, payload);
                            break;

                        case 'C':
                            HandleClose(session, payload);
                            await SendCloseCompleteAsync(stream);
                            break;

                        case 'H':
                            break;

                        case 'p':
                            // SASL/Password messages during auth are handled outside the loop.
                            // If we see 'p' here, the client sent an unexpected password message.
                            await SendErrorAsync(stream, "0A000", "Unexpected password message.");
                            session.IgnoreUntilSync = true;
                            break;

                        case 'd':
                            // CopyData
                            await HandleCopyDataAsync(stream, backend, session, payload);
                            break;

                        case 'c':
                            // CopyDone
                            await HandleCopyDoneAsync(stream, backend, session);
                            break;

                        case 'f':
                            // CopyFail
                            await HandleCopyFailAsync(stream, backend, session, payload);
                            break;

                        case 'S':
                            session.IgnoreUntilSync = false;
                            await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
                            break;

                        case 'X':
                            return;

                        default:
                            await SendErrorAsync(stream, "0A000", $"Frontend message '{type}' is not supported.");
                            session.IgnoreUntilSync = true;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    await SendErrorAsync(stream, ResolveSqlState(ex), ex.Message);
                    session.IgnoreUntilSync = true;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                await SendErrorAsync(stream, ResolveSqlState(ex), ex.Message);
                await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
            }
            catch { /* best-effort */ }
        }
        finally
        {
            if (session.Transaction != null)
            {
                try { session.Transaction.Rollback(); } catch { }
                session.Transaction.Dispose();
            }
        }
    }

    // -----------------------------------------------------------------------------
    // Protocol: startup
    // -----------------------------------------------------------------------------

    private async Task<(bool Success, string? UserName, bool SslAccepted)> HandleStartupAsync(Stream stream)
    {
        while (true)
        {
            var lenBytes = await ReadExactlyAsync(stream, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (length < 8)
                throw new InvalidOperationException("Invalid startup packet length.");

            var body = await ReadExactlyAsync(stream, length - 4);
            var requestCode = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
            PgWireTrace.StartupPacket(requestCode, length);

            const int ProtocolV3 = 196608;
            const int SslRequest = 80877103;
            const int CancelRequest = 80877102;

            if (requestCode == SslRequest)
            {
                if (_tlsOptions?.Certificate != null)
                {
                    await stream.WriteAsync(new byte[] { (byte)'S' });
                    await stream.FlushAsync();
                    PgWireTrace.RawOutbound('S', 1);
                    return (true, null, true);
                }
                else
                {
                    await stream.WriteAsync(new byte[] { (byte)'N' });
                    await stream.FlushAsync();
                    PgWireTrace.RawOutbound('N', 1);
                    continue;
                }
            }

            if (requestCode == CancelRequest)
                return (false, null, false);

            if (requestCode != ProtocolV3)
                throw new InvalidOperationException($"Unsupported protocol version/code: {requestCode}.");

            var parameters = ParseStartupParameters(body.AsSpan(4));
            parameters.TryGetValue("user", out var userName);
            return (true, userName, false);
        }
    }

    private static Dictionary<string, string> ParseStartupParameters(ReadOnlySpan<byte> body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < body.Length)
        {
            // Find null terminator for key
            var keyEnd = offset;
            while (keyEnd < body.Length && body[keyEnd] != 0)
                keyEnd++;
            if (keyEnd >= body.Length) break;

            var key = System.Text.Encoding.UTF8.GetString(body.Slice(offset, keyEnd - offset));
            offset = keyEnd + 1;

            // Find null terminator for value
            var valueEnd = offset;
            while (valueEnd < body.Length && body[valueEnd] != 0)
                valueEnd++;
            if (valueEnd >= body.Length) break;

            var value = System.Text.Encoding.UTF8.GetString(body.Slice(offset, valueEnd - offset));
            offset = valueEnd + 1;

            if (!string.IsNullOrEmpty(key))
                result[key] = value;

            // Double null = end of parameters
            if (offset < body.Length && body[offset] == 0)
                break;
        }
        return result;
    }

    // -----------------------------------------------------------------------------
    // Protocol: Simple Query ('Q')
    // -----------------------------------------------------------------------------

    private static async Task HandleSimpleQueryAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        var sqlText = DecodeCStringPayload(payload);
        var statements = SplitSqlStatements(sqlText)
            .Where(statement => !string.IsNullOrWhiteSpace(statement))
            .ToArray();

        string? currentStatement = null;
        PgWireTrace.Sql("SIMPLE", sqlText);

        try
        {
            foreach (var statement in statements)
            {
                var trimmed = NormalizeSqlForExecution(statement.Trim());
                currentStatement = trimmed;

                if (IsBegin(trimmed))
                {
                    if (session.Transaction == null)
                        session.Transaction = backend.BeginTransaction();

                    await SendCommandCompleteAsync(stream, "BEGIN");
                    continue;
                }

                if (IsCommit(trimmed))
                {
                    if (session.Transaction != null)
                    {
                        session.Transaction.Commit();
                        session.Transaction.Dispose();
                        session.Transaction = null;
                    }

                    await SendCommandCompleteAsync(stream, "COMMIT");
                    continue;
                }

                if (IsRollback(trimmed))
                {
                    if (session.Transaction != null)
                    {
                        session.Transaction.Rollback();
                        session.Transaction.Dispose();
                        session.Transaction = null;
                    }

                    await SendCommandCompleteAsync(stream, "ROLLBACK");
                    continue;
                }

                if (IsSetOrShow(trimmed))
                {
                    if (trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) });
                        await SendDataRowAsync(stream, new[] { "layeredsql" });
                        await SendCommandCompleteAsync(stream, "SHOW 1");
                    }
                    else
                    {
                        await SendCommandCompleteAsync(stream, "SET");
                    }

                    continue;
                }

                if (TryResolveVirtualQuery(trimmed, backend.DatabaseName, DiscoverTableDefinitions(backend, session), out var virtualResult,
                    backend.DatabaseCollation, backend.DatabaseCType, backend.GetPgStatsRows()))
                {
                    await SendVirtualQueryResultAsync(stream, virtualResult);
                    continue;
                }

                // Handle COPY protocol
                if (SqlSyntaxText.StartsWithKeyword(trimmed, "COPY"))
                {
                    try
                    {
                        var copyStmt = WalhallaSql.Parsing.SqlStatementParser.Parse(trimmed) as WalhallaSql.Sql.SqlCopyStatement;
                        if (copyStmt != null)
                        {
                            if (copyStmt.Direction == WalhallaSql.Sql.SqlCopyDirection.FromStdin)
                            {
                                session.CopyState = new PgCopyState
                                {
                                    Statement = copyStmt,
                                    Format = copyStmt.Options.Format
                                };
                                await SendCopyInResponseAsync(stream, copyStmt, backend);
                                return; // Wait for CopyData / CopyDone / CopyFail messages
                            }
                            else if (copyStmt.Direction == WalhallaSql.Sql.SqlCopyDirection.ToStdout)
                            {
                                await HandleCopyToStdoutAsync(stream, backend, session, copyStmt);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await SendErrorAsync(stream, ResolveSqlState(ex), ex.Message);
                        continue;
                    }
                }

                try
                {
                    using var command = backend.CreateCommand();
                    command.CommandText = NormalizeSqlForExecution(trimmed);
                    if (session.Transaction != null)
                        command.Transaction = session.Transaction;

                    if (LooksLikeSelect(trimmed))
                    {
                        using var reader = command.ExecuteReader();

                        if (TryExpandSingleJsonColumnResult(reader, out var expandedFields, out var expandedRows))
                        {
                            await SendRowDescriptionAsync(stream, expandedFields);

                            foreach (var expandedRow in expandedRows)
                                await SendDataRowAsync(stream, expandedRow);

                            await SendCommandCompleteAsync(stream, $"SELECT {expandedRows.Count}");
                            continue;
                        }

                        // Build field descriptors from reader metadata (types come from table
                        // metadata for streaming results, or from first-row inference for
                        // materialized results). No row buffering needed.
                        var fieldCount = reader.FieldCount;
                        var schemaTypes = TryLoadSchemaColumnTypes(backend, session, trimmed);

                        var fields = new (string Name, Type ClrType)[fieldCount];
                        for (var i = 0; i < fieldCount; i++)
                        {
                            var name = reader.GetName(i);
                            var readerType = reader.GetFieldType(i);
                            var clrType = readerType != typeof(object)
                                ? readerType
                                : (schemaTypes.TryGetValue(name, out var schemaType) && schemaType != typeof(object)
                                    ? schemaType
                                    : typeof(string));
                            fields[i] = (name, clrType);
                        }

                        await SendRowDescriptionAsync(stream, fields);

                        // Stream rows: read and send one at a time without buffering
                        var rowCount = 0;
                        while (reader.Read())
                        {
                            var raw = new object?[fieldCount];
                            for (var i = 0; i < fieldCount; i++)
                                raw[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            await SendDataRowAsync(stream, raw, fields);
                            rowCount++;
                        }

                        await SendCommandCompleteAsync(stream, $"SELECT {rowCount}");
                    }
                    else
                    {
                        var affected = command.ExecuteNonQuery();
                        if (IsDdlStatement(trimmed))
                            session.InvalidateTableDefinitionCache();
                        await SendCommandCompleteAsync(stream, BuildCommandTag(trimmed, affected));
                    }
                }
                catch (Exception ex) when (LooksLikeSelect(trimmed) && IsCannotInferCollectionError(ex))
                {
                    await SendRowDescriptionAsync(stream, Array.Empty<(string Name, Type ClrType)>());
                    await SendCommandCompleteAsync(stream, "SELECT 0");
                }
            }
        }
        catch (Exception ex)
        {
            var sqlContext = string.IsNullOrWhiteSpace(currentStatement) ? string.Empty : $" SQL=[{currentStatement}]";
            await SendErrorAsync(stream, ResolveSqlState(ex), ex.Message + sqlContext);
        }

        await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
    }

    // -----------------------------------------------------------------------------
    // Protocol: COPY
    // -----------------------------------------------------------------------------

    private static async Task HandleCopyDataAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        if (session.CopyState == null)
        {
            await SendErrorAsync(stream, "0A000", "Received CopyData outside of COPY operation.");
            return;
        }

        if (session.CopyState.Statement.Direction == WalhallaSql.Sql.SqlCopyDirection.FromStdin)
        {
            // Buffer the incoming data; parse on CopyDone.
            if (session.CopyState.Statement.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary)
            {
                session.CopyState.BinaryRows.Add(payload);
            }
            else
            {
                var text = Encoding.UTF8.GetString(payload);
                session.CopyState.TextBuffer.Append(text);
            }
        }
    }

    private static async Task HandleCopyDoneAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session)
    {
        if (session.CopyState == null)
        {
            await SendErrorAsync(stream, "0A000", "Received CopyDone outside of COPY operation.");
            return;
        }

        try
        {
            if (session.CopyState.Statement.Direction == WalhallaSql.Sql.SqlCopyDirection.FromStdin)
            {
                var copyStmt = session.CopyState.Statement;
                var rowsInserted = await ExecuteCopyInAsync(backend, session, copyStmt);
                session.CopyState = null;
                await SendCommandCompleteAsync(stream, $"COPY {rowsInserted}");
            }
        }
        catch (Exception ex)
        {
            session.CopyState = null;
            await SendErrorAsync(stream, ResolveSqlState(ex), ex.Message);
        }
        finally
        {
            await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
        }
    }

    private static async Task HandleCopyFailAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        session.CopyState = null;
        var errorMessage = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        await SendErrorAsync(stream, "57014", $"COPY failed: {errorMessage}");
        await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
    }

    private static async Task<int> ExecuteCopyInAsync(IPgWireBackendConnection backend, PgDbSessionState session, WalhallaSql.Sql.SqlCopyStatement copyStmt)
    {
        var walhallaBackend = backend as WalhallaSqlPgWireBackend;
        if (walhallaBackend == null)
            throw new NotSupportedException("COPY is only supported with WalhallaSqlPgWireBackend.");

        if (copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary)
            return await ExecuteBinaryCopyInAsync(walhallaBackend, session, copyStmt);

        return await ExecuteTextCopyInAsync(walhallaBackend, session, copyStmt);
    }

    private static async Task<int> ExecuteTextCopyInAsync(WalhallaSqlPgWireBackend backend, PgDbSessionState session, WalhallaSql.Sql.SqlCopyStatement copyStmt)
    {
        var delimiter = copyStmt.Options.Delimiter ?? "\t";
        if (copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Csv)
            delimiter = copyStmt.Options.Delimiter ?? ",";

        var nullMarker = copyStmt.Options.NullMarker ?? "\\N";
        var text = session.CopyState!.TextBuffer.ToString();
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var rows = new List<object?[]>();
        var engine = backend.GetEngine();
        var tableDef = engine.GetTableDefinition(copyStmt.TableName)
            ?? throw new WalhallaSql.WalhallaException($"Table '{copyStmt.TableName}' not found.");

        var columnNames = copyStmt.ColumnNames?.ToList()
            ?? tableDef.Columns.Select(c => c.Name).ToList();

        var columnIndices = new List<int>();
        foreach (var colName in columnNames)
        {
            var idx = -1;
            for (var i = 0; i < tableDef.Columns.Count; i++)
            {
                if (string.Equals(tableDef.Columns[i].Name, colName, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
                throw new WalhallaSql.WalhallaException($"Column '{colName}' not found in table '{copyStmt.TableName}'.");
            columnIndices.Add(idx);
        }

        bool skipHeader = copyStmt.Options.Header;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
                continue;

            if (skipHeader)
            {
                skipHeader = false;
                continue;
            }

            var cells = copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Csv
                ? ParseCsvLine(line, delimiter, copyStmt.Options.Quote ?? "\"", copyStmt.Options.Escape ?? "\"")
                : line.Split(new[] { delimiter }, StringSplitOptions.None);

            if (cells.Length != columnNames.Count)
                throw new WalhallaSql.WalhallaException($"COPY row column count mismatch: expected {columnNames.Count}, got {cells.Length}.");

            var row = new object?[tableDef.Columns.Count];
            for (int i = 0; i < columnIndices.Count; i++)
            {
                var cell = cells[i];
                var colIdx = columnIndices[i];
                var colDef = tableDef.Columns[colIdx];
                row[colIdx] = cell == nullMarker ? null : ConvertCellValue(cell, colDef.Type);
            }
            rows.Add(row);
        }

        if (rows.Count > 0)
        {
            engine.InsertBatch(copyStmt.TableName, rows);
        }

        return rows.Count;
    }

    private static async Task<int> ExecuteBinaryCopyInAsync(WalhallaSqlPgWireBackend backend, PgDbSessionState session, WalhallaSql.Sql.SqlCopyStatement copyStmt)
    {
        // PostgreSQL binary COPY format: header + per-row length-prefixed values + trailer
        var data = session.CopyState!.BinaryRows.SelectMany(r => r).ToArray();
        // For now, binary COPY is not fully implemented; treat as unsupported.
        throw new NotSupportedException("Binary COPY FROM STDIN is not yet supported.");
    }

    private static string[] ParseCsvLine(string line, string delimiter, string quote, string escape)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        var quoteChar = quote[0];
        var escapeChar = escape[0];
        var delimChar = delimiter[0];

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == escapeChar && i + 1 < line.Length && line[i + 1] == quoteChar)
                {
                    sb.Append(quoteChar);
                    i++;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == quoteChar)
                {
                    inQuotes = true;
                }
                else if (c == delimChar)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static object? ConvertCellValue(string cell, WalhallaSql.Sql.SqlScalarType type)
    {
        if (string.IsNullOrEmpty(cell))
            return null;

        return type switch
        {
            WalhallaSql.Sql.SqlScalarType.Int32 => int.Parse(cell, CultureInfo.InvariantCulture),
            WalhallaSql.Sql.SqlScalarType.Int64 => long.Parse(cell, CultureInfo.InvariantCulture),
            WalhallaSql.Sql.SqlScalarType.Double => double.Parse(cell, CultureInfo.InvariantCulture),
            WalhallaSql.Sql.SqlScalarType.Decimal => decimal.Parse(cell, CultureInfo.InvariantCulture),
            WalhallaSql.Sql.SqlScalarType.Boolean => bool.Parse(cell),
            WalhallaSql.Sql.SqlScalarType.DateTime => DateTime.Parse(cell, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WalhallaSql.Sql.SqlScalarType.Guid => Guid.Parse(cell),
            WalhallaSql.Sql.SqlScalarType.Binary => Convert.FromBase64String(cell),
            _ => cell
        };
    }

    private static string FormatTextLine(IReadOnlyList<string?> values, WalhallaSql.Sql.SqlCopyOptions options)
    {
        var delimiter = options.Delimiter ?? "\t";
        var nullMarker = options.NullMarker ?? @"\N";
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var v = values[i];
            sb.Append(v == null ? nullMarker : v);
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string FormatCsvLine(IReadOnlyList<string?> values, WalhallaSql.Sql.SqlCopyOptions options)
    {
        var delimiter = options.Delimiter ?? ",";
        var quote = options.Quote ?? "\"";
        var escape = options.Escape ?? "\"";
        var nullMarker = options.NullMarker; // Postgres CSV default: empty string means NULL
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var v = values[i];
            if (v == null)
            {
                if (nullMarker != null) sb.Append(nullMarker);
            }
            else if (v.Contains(quote) || v.Contains(delimiter) || v.Contains("\n") || v.Contains("\r"))
            {
                sb.Append(quote);
                foreach (var c in v)
                {
                    if (c.ToString() == quote)
                        sb.Append(escape).Append(quote);
                    else
                        sb.Append(c);
                }
                sb.Append(quote);
            }
            else
            {
                sb.Append(v);
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string EscapeIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task HandleCopyToStdoutAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, WalhallaSql.Sql.SqlCopyStatement copyStmt)
    {
        await SendCopyOutResponseAsync(stream, copyStmt, backend);

        // Build SELECT query
        var columnNames = copyStmt.ColumnNames;
        string query;
        if (columnNames != null && columnNames.Count > 0)
        {
            query = $"SELECT {string.Join(", ", columnNames.Select(EscapeIdentifier))} FROM {EscapeIdentifier(copyStmt.TableName)}";
        }
        else
        {
            query = $"SELECT * FROM {EscapeIdentifier(copyStmt.TableName)}";
        }

        int rowCount = 0;
        using var cmd = backend.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();

        // Header row for CSV
        if (copyStmt.Options.Header && copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Csv)
        {
            var headerCols = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                headerCols.Add(reader.GetName(i));
            var headerLine = FormatCsvLine(headerCols, copyStmt.Options);
            await SendCopyDataAsync(stream, Encoding.UTF8.GetBytes(headerLine));
        }

        while (reader.Read())
        {
            rowCount++;
            var values = new List<string?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                    values.Add(null);
                else
                    values.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }

            string line = copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Csv
                ? FormatCsvLine(values, copyStmt.Options)
                : FormatTextLine(values, copyStmt.Options);

            await SendCopyDataAsync(stream, Encoding.UTF8.GetBytes(line));
        }

        await SendCopyDoneAsync(stream);
        await SendCommandCompleteAsync(stream, $"COPY {rowCount}");
        await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
    }

    // -----------------------------------------------------------------------------
    // Protocol: Extended Query � Parse ('P'), Bind ('B'), Describe ('D'), Execute ('E'), Close ('C')
    // -----------------------------------------------------------------------------

    private static void HandleParse(PgDbSessionState session, byte[] payload)
    {
        var reader = new PgPayloadReader(payload);
        var statementName = reader.ReadCString();
        var sql = reader.ReadCString();


        var parameterTypeCount = reader.ReadInt16();
        var parameterTypes = new int[parameterTypeCount];
        PgWireTrace.Sql("PARSE", sql);
        for (var i = 0; i < parameterTypeCount; i++)
            parameterTypes[i] = reader.ReadInt32();

        var key = NormalizeName(statementName);
        session.PreparedStatements[key] = new PgPreparedStatement(sql, parameterTypes);
    }

    private static void HandleBind(IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        var reader = new PgPayloadReader(payload);
        var portalName = NormalizeName(reader.ReadCString());
        var statementName = NormalizeName(reader.ReadCString());

        if (!session.PreparedStatements.TryGetValue(statementName, out var prepared))
            throw new InvalidOperationException($"Prepared statement '{statementName}' not found.");

        var formatCodes = ReadFormatCodes(reader);

        var parameterCount = reader.ReadInt16();
        var parameterLiterals = new string?[parameterCount];
        var parameterValues = new object?[parameterCount];
        for (var i = 0; i < parameterCount; i++)
        {
            var len = reader.ReadInt32();
            if (len < 0)
            {
                parameterLiterals[i] = "NULL";
                continue;
            }

            var raw = reader.ReadBytes(len);
            var formatCode = ResolveFormatCode(formatCodes, i);
            var parameterTypeOid = i < prepared.ParameterTypeOids.Count ? prepared.ParameterTypeOids[i] : 0;
            parameterLiterals[i] = DecodeBindParameterLiteral(raw, formatCode, parameterTypeOid);
            parameterValues[i] = DecodeBindParameterValue(raw, formatCode, parameterTypeOid);
        }

        var resultFormatCodes = ReadFormatCodes(reader);

        var literalSql = NormalizeSqlForExecution(RenderSqlWithParameters(prepared.Sql, parameterLiterals));
        var isQuery = ReturnsRows(literalSql);

        // Try to compile a reusable engine-side prepared statement (SELECTs only, no transaction fallback).
        var parameterizedSql = NormalizeSqlForExecution(RenderSqlWithParameterMarkers(prepared.Sql, parameterCount));
        var compiled = isQuery && parameterCount >= 0
            ? TryCompilePgStatement(backend, prepared, parameterizedSql, parameterCount)
            : null;

        // Inherit MetadataDescribed from statement so repeated executions don't send a second RowDescription
        session.Portals[portalName] = new PgBoundPortal(literalSql, isQuery, prepared.MetadataDescribed, prepared)
        {
            ParameterizedSql = parameterizedSql,
            ParameterValues = parameterValues,
            PreparedStatement = compiled,
            DescribedFields = prepared.DescribedFields,
            ResultFormatCodes = resultFormatCodes
        };
    }

    private static async Task HandleDescribeAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        var reader = new PgPayloadReader(payload);
        var targetType = (char)reader.ReadByte();
        var name = NormalizeName(reader.ReadCString());


        switch (targetType)
        {
            case 'S':
                if (!session.PreparedStatements.TryGetValue(name, out var statement))
                    throw new InvalidOperationException($"Prepared statement '{name}' not found.");

                await SendParameterDescriptionAsync(stream, statement.ParameterTypeOids);

                if (!ReturnsRows(statement.Sql))
                {
                    await SendNoDataAsync(stream);
                    return;
                }

                    var statementKnownTables = DiscoverTableDefinitions(backend, session);
                    var statementDescribeSql = RewriteSelectStarWithKnownColumns(statement.Sql, statementKnownTables);

                    if (IsSetOrShow(statementDescribeSql) && statementDescribeSql.TrimStart().StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    statement.DescribedFields = new[] { ("setting", typeof(string)) };
                    await SendRowDescriptionAsync(stream, statement.DescribedFields);
                    statement.MetadataDescribed = true;
                    return;
                }

                    if (TryResolveVirtualQuery(statementDescribeSql, backend.DatabaseName, statementKnownTables, out var statementVirtualResult,
                        backend.DatabaseCollation, backend.DatabaseCType, backend.GetPgStatsRows()))
                {
                    statement.DescribedFields = statementVirtualResult.Fields;
                    await SendRowDescriptionAsync(stream, statement.DescribedFields);
                    statement.MetadataDescribed = true;
                    return;
                }

                IReadOnlyList<(string Name, Type ClrType)> statementFields;
                try
                {
                    statementFields = TryReadFields(backend, session, statementDescribeSql);
                }
                catch
                {
                    statementFields = InferFieldsFromSelect(statementDescribeSql);
                }

                if (statementFields.Count == 1 && string.Equals(statementFields[0].Name, "?column?", StringComparison.OrdinalIgnoreCase))
                {
                    var inferredFromTable = InferFieldsFromKnownTables(statementDescribeSql, statementKnownTables);
                    if (inferredFromTable.Count > 0)
                        statementFields = inferredFromTable;
                    else
                        statementFields = InferFieldsFromSampleJsonRow(backend, session, statementDescribeSql);
                }

                if (statementFields.Count == 0)
                    statementFields = InferFallbackQueryFields(statement.Sql);

                // Refine column CLR types using catalog schema
                statementFields = ApplySchemaTypes(statementFields, TryLoadSchemaColumnTypes(backend, session, statementDescribeSql));

                statement.DescribedFields = statementFields;
                await SendRowDescriptionAsync(stream, statement.DescribedFields);
                statement.MetadataDescribed = true;
                return;

            case 'P':
                if (!session.Portals.TryGetValue(name, out var portal))
                    throw new InvalidOperationException($"Portal '{name}' not found.");

                var knownTables = DiscoverTableDefinitions(backend, session);
                var describeSql = RewriteSelectStarWithKnownColumns(portal.Sql, knownTables);

                if (!portal.IsQuery)
                {
                    await SendNoDataAsync(stream);
                    return;
                }

                if (IsSetOrShow(describeSql) && describeSql.TrimStart().StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    portal.DescribedFields = new[] { ("setting", typeof(string)) };
                    await SendRowDescriptionAsync(stream, portal.DescribedFields, portal.ResultFormatCodes);
                    MarkPortalDescribed(portal);
                    return;
                }

                if (TryResolveVirtualQuery(describeSql, backend.DatabaseName, knownTables, out var virtualResult,
                    backend.DatabaseCollation, backend.DatabaseCType, backend.GetPgStatsRows()))
                {
                    portal.DescribedFields = virtualResult.Fields;
                    await SendRowDescriptionAsync(stream, portal.DescribedFields, portal.ResultFormatCodes);
                    MarkPortalDescribed(portal);
                    return;
                }

                IReadOnlyList<(string Name, Type ClrType)> fields;
                try
                {
                    fields = TryReadFields(backend, session, describeSql);
                }
                catch
                {
                    fields = InferFieldsFromSelect(describeSql);
                }

                if (fields.Count == 0)
                {
                    fields = Array.Empty<(string Name, Type ClrType)>();
                }

                if (fields.Count == 1 && string.Equals(fields[0].Name, "?column?", StringComparison.OrdinalIgnoreCase))
                {
                    var inferredFromTable = InferFieldsFromKnownTables(describeSql, knownTables);
                    if (inferredFromTable.Count > 0)
                        fields = inferredFromTable;
                    else
                        fields = InferFieldsFromSampleJsonRow(backend, session, describeSql);
                }

                if (fields.Count == 0)
                    fields = InferFallbackQueryFields(portal.Sql);

                // Refine column CLR types using catalog schema (fixes OID for INT/BIGINT etc.)
                fields = ApplySchemaTypes(fields, TryLoadSchemaColumnTypes(backend, session, describeSql));

                portal.DescribedFields = fields;
                await SendRowDescriptionAsync(stream, portal.DescribedFields, portal.ResultFormatCodes);
                MarkPortalDescribed(portal);
                return;

            default:
                throw new InvalidOperationException($"Describe target '{targetType}' is not supported.");
        }
    }

    private static void MarkPortalDescribed(PgBoundPortal portal)
    {
        portal.MetadataDescribed = true;
        if (portal.SourceStatement != null)
        {
            portal.SourceStatement.MetadataDescribed = true;
            portal.SourceStatement.DescribedFields = portal.DescribedFields;
        }
    }

    private static async Task HandleExecuteAsync(Stream stream, IPgWireBackendConnection backend, PgDbSessionState session, byte[] payload)
    {
        var reader = new PgPayloadReader(payload);
        var portalName = NormalizeName(reader.ReadCString());
        var maxRows = reader.ReadInt32();
        _ = maxRows;

        if (!session.Portals.TryGetValue(portalName, out var portal))
            throw new InvalidOperationException($"Portal '{portalName}' not found.");

        var knownTables = DiscoverTableDefinitions(backend, session);
        var trimmedSql = RewriteSelectStarWithKnownColumns(portal.Sql, knownTables).Trim();

        if (IsBegin(trimmedSql))
        {
            if (session.Transaction == null)
                session.Transaction = backend.BeginTransaction();

            await SendCommandCompleteAsync(stream, "BEGIN");
            return;
        }

        if (IsCommit(trimmedSql))
        {
            if (session.Transaction != null)
            {
                session.Transaction.Commit();
                session.Transaction.Dispose();
                session.Transaction = null;
            }

            await SendCommandCompleteAsync(stream, "COMMIT");
            return;
        }

        if (IsRollback(trimmedSql))
        {
            if (session.Transaction != null)
            {
                session.Transaction.Rollback();
                session.Transaction.Dispose();
                session.Transaction = null;
            }

            await SendCommandCompleteAsync(stream, "ROLLBACK");
            return;
        }

        if (IsSetOrShow(trimmedSql))
        {
            if (trimmedSql.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
            {
                if (!portal.MetadataDescribed)
                    await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) }, portal.ResultFormatCodes);
                await SendDataRowAsync(stream, new[] { "layeredsql" });
                await SendCommandCompleteAsync(stream, "SHOW 1");
                MarkPortalDescribed(portal);
            }
            else
            {
                await SendCommandCompleteAsync(stream, "SET");
            }

            return;
        }

        if (portal.IsQuery && TryResolveVirtualQuery(trimmedSql, backend.DatabaseName, knownTables, out var virtualResult,
            backend.DatabaseCollation, backend.DatabaseCType, backend.GetPgStatsRows()))
        {
            await SendVirtualExecuteResultAsync(stream, virtualResult, !portal.MetadataDescribed, portal.ResultFormatCodes);
            MarkPortalDescribed(portal);
            return;
        }

        try
        {
            using var command = backend.CreateCommand();
            command.CommandText = NormalizeSqlForExecution(trimmedSql);
            if (session.Transaction != null)
                command.Transaction = session.Transaction;

            if (portal.IsQuery)
            {
                using var dbReader = portal.PreparedStatement != null && session.Transaction == null
                    ? ExecutePreparedReader(portal)
                    : command.ExecuteReader();

                if (TryExpandSingleJsonColumnResult(dbReader, out var expandedFields, out var expandedRows))
                {
                    if (!portal.MetadataDescribed)
                        await SendRowDescriptionAsync(stream, expandedFields, portal.ResultFormatCodes);

                    foreach (var expandedRow in expandedRows)
                        await SendDataRowAsync(stream, expandedRow);

                    await SendCommandCompleteAsync(stream, $"SELECT {expandedRows.Count}");
                    MarkPortalDescribed(portal);
                    return;
                }

                // Build field descriptors from reader metadata without buffering rows.
                var fieldCount = dbReader.FieldCount;
                var rowFieldsFromDescribe = InferFieldsFromSelect(trimmedSql);
                if (rowFieldsFromDescribe.Count == 0)
                    rowFieldsFromDescribe = Array.Empty<(string Name, Type ClrType)>();
                var schemaTypes = TryLoadSchemaColumnTypes(backend, session, trimmedSql);
                rowFieldsFromDescribe = ApplySchemaTypes(rowFieldsFromDescribe, schemaTypes);

                var rowFields = new (string Name, Type ClrType)[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    var name = dbReader.GetName(i);
                    var readerType = dbReader.GetFieldType(i);
                    Type clrType;
                    var describeField = rowFieldsFromDescribe.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (readerType != typeof(object))
                        clrType = readerType;
                    else if (schemaTypes.TryGetValue(name, out var schemaType) && schemaType != typeof(object))
                        clrType = schemaType;
                    else if (describeField.Name != null && describeField.ClrType != typeof(object))
                        clrType = describeField.ClrType;
                    else
                        clrType = typeof(string);
                    rowFields[i] = (name, clrType);
                }

                var resultFields = portal.MetadataDescribed && portal.DescribedFields is { Count: > 0 }
                    ? portal.DescribedFields
                    : rowFields;

                if (!portal.MetadataDescribed)
                {
                    portal.DescribedFields = rowFields;
                    resultFields = portal.DescribedFields;
                    await SendRowDescriptionAsync(stream, resultFields, portal.ResultFormatCodes);
                }

                // Stream rows: read and send one at a time without buffering
                var rowCount = 0;
                while (dbReader.Read())
                {
                    var raw = new object?[fieldCount];
                    for (var i = 0; i < fieldCount; i++)
                        raw[i] = dbReader.IsDBNull(i) ? null : dbReader.GetValue(i);

                    if (PgWireTrace.Enabled)
                    {
                        var preview = string.Join(", ",
                            raw.Select((value, index) => $"{(index < resultFields.Count ? resultFields[index].Name : $"col{index}")}={(value is null ? "<null>" : value)}"));
                        Console.WriteLine($"[PGWIRE][ROW] {preview}");
                    }

                    await SendDataRowAsync(stream, raw, resultFields, portal.ResultFormatCodes);
                    rowCount++;
                }

                await SendCommandCompleteAsync(stream, $"SELECT {rowCount}");
                MarkPortalDescribed(portal);
                return;
            }

            var affected = command.ExecuteNonQuery();
            if (IsDdlStatement(trimmedSql))
                session.InvalidateTableDefinitionCache();
            await SendCommandCompleteAsync(stream, BuildCommandTag(trimmedSql, affected));
        }
        catch (Exception ex) when (portal.IsQuery && IsCannotInferCollectionError(ex))
        {
            if (!portal.MetadataDescribed)
                await SendRowDescriptionAsync(stream, Array.Empty<(string Name, Type ClrType)>(), portal.ResultFormatCodes);
            await SendCommandCompleteAsync(stream, "SELECT 0");
            MarkPortalDescribed(portal);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ex.Message} SQL=[{trimmedSql}]", ex);
        }
    }

    private static void HandleClose(PgDbSessionState session, byte[] payload)
    {
        var reader = new PgPayloadReader(payload);
        var targetType = (char)reader.ReadByte();
        var name = NormalizeName(reader.ReadCString());

        if (targetType == 'S')
        {
            session.PreparedStatements.Remove(name);
            if (name == string.Empty)
                session.Portals.Remove(string.Empty);
            return;
        }

        if (targetType == 'P')
        {
            session.Portals.Remove(name);
            return;
        }

        throw new InvalidOperationException($"Close target '{targetType}' is not supported.");
    }

    // -----------------------------------------------------------------------------
    // SQL utilities
    // -----------------------------------------------------------------------------

    private static string NormalizeSqlForExecution(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;

        var normalized = UnquoteDelimitedIdentifiers(sql);

        normalized = Regex.Replace(
            normalized,
            @"(?<prefix>\b(?:from|join|into|update|table)\s+)(?:""?public""?\s*\.\s*)(?<name>""[^""]+""|[a-zA-Z_][a-zA-Z0-9_]*)",
            "${prefix}${name}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"(?<prefix>\b(?:from|join|into|update|table)\s+)""(?<name>[^""]+)""",
            "${prefix}${name}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\b""?public""?\s*\.\s*""?(?<name>[a-zA-Z_][a-zA-Z0-9_]*)""?",
            "${name}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"^\s*select\s+(?<alias>[a-zA-Z_][a-zA-Z0-9_]*)\.\*\s+from\s+(?<table>[a-zA-Z_][a-zA-Z0-9_]*)(?:\s+as)?\s+\k<alias>(?<tail>.*)$",
            "SELECT * FROM ${table}${tail}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        return normalized;
    }

    private static string UnquoteDelimitedIdentifiers(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var builder = new StringBuilder(sql.Length);
        var inSingleQuotedString = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current == '\'')
            {
                builder.Append(current);
                if (inSingleQuotedString && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append(sql[index + 1]);
                    index++;
                    continue;
                }

                inSingleQuotedString = !inSingleQuotedString;
                continue;
            }

            if (!inSingleQuotedString && current == '"')
            {
                var token = new StringBuilder();
                var closingQuoteIndex = -1;
                for (var innerIndex = index + 1; innerIndex < sql.Length; innerIndex++)
                {
                    if (sql[innerIndex] == '"')
                    {
                        if (innerIndex + 1 < sql.Length && sql[innerIndex + 1] == '"')
                        {
                            token.Append('"');
                            innerIndex++;
                            continue;
                        }

                        closingQuoteIndex = innerIndex;
                        break;
                    }

                    token.Append(sql[innerIndex]);
                }

                if (closingQuoteIndex > index && IsSimpleDelimitedIdentifier(token.ToString()))
                {
                    builder.Append(token);
                    index = closingQuoteIndex;
                    continue;
                }
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool IsSimpleDelimitedIdentifier(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return Regex.IsMatch(token, @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);
    }

    private static bool IsCannotInferCollectionError(Exception exception) =>
        exception.Message.Contains("Cannot infer collection for SQL command", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string name) => name ?? string.Empty;

    private static string BuildCommandTag(string sql, int affected)
    {
        var keyword = FirstKeyword(sql);
        return keyword switch
        {
            "INSERT" => $"INSERT 0 {affected}",
            "UPDATE" => $"UPDATE {affected}",
            "DELETE" => $"DELETE {affected}",
            "CREATE" => "CREATE",
            "DROP" => "DROP",
            "ALTER" => "ALTER",
            _ => $"{keyword} {affected}"
        };
    }

    private static string FirstKeyword(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "OK";

        var match = Regex.Match(
            sql,
            @"^\s*(?:(?:--[^\r\n]*(?:[\r\n]+|$))|(?:/\*.*?\*/\s*)|(?:\(\s*))*([A-Za-z]+)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        var parts = sql.TrimStart().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "OK" : parts[0].ToUpperInvariant();
    }

    private static bool LooksLikeSelect(string sql)
    {
        var keyword = FirstKeyword(sql);
        return keyword is "SELECT" or "WITH";
    }

    /// <summary>Returns true for DDL statements that change the schema (table/index/view mutations).
    /// Used to invalidate the per-session DiscoverTableDefinitions cache.</summary>
    private static bool IsDdlStatement(string sql)
    {
        var keyword = FirstKeyword(sql);
        return keyword is "CREATE" or "DROP" or "ALTER";
    }

    private static bool ReturnsRows(string sql)
    {
        var keyword = FirstKeyword(sql);
        return keyword is "SELECT" or "WITH" or "SHOW" or "VALUES" or "TABLE";
    }

    private static bool IsBegin(string sql)
    {
        var keyword = FirstKeyword(sql);
        return keyword == "BEGIN" || (keyword == "START" && sql.Contains("TRANSACTION", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommit(string sql) => FirstKeyword(sql) == "COMMIT";
    private static bool IsRollback(string sql) => FirstKeyword(sql) == "ROLLBACK";

    private static bool IsSetOrShow(string sql)
    {
        var keyword = FirstKeyword(sql);
        return keyword is "SET" or "SHOW";
    }

    private static string DecodeCStringPayload(byte[] payload)
    {
        var len = payload.Length;
        if (len > 0 && payload[len - 1] == 0)
            len--;

        return Encoding.UTF8.GetString(payload, 0, len);
    }

    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            yield break;

        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;
        var start = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // End of line comment ends at newline
            if (inLineComment)
            {
                if (c == '\n')
                    inLineComment = false;
                continue;
            }

            // Block comment ends at */
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++; // skip '/'
                }
                continue;
            }

            // Detect start of line comment
            if (!inString && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                inLineComment = true;
                i++; // skip second '-'
                continue;
            }

            // Detect start of block comment
            if (!inString && c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                inBlockComment = true;
                i++; // skip '*'
                continue;
            }

            // String literal: toggle on unescaped single quote
            if (c == '\'' && !inBlockComment && !inLineComment)
            {
                // Handle doubled quotes ('' = escaped single quote)
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                    i++; // skip second quote, stay in string
                else
                    inString = !inString;
                continue;
            }

            if (inString || c != ';')
                continue;

            var statement = sql[start..i].Trim();
            if (statement.Length > 0)
                yield return statement;

            start = i + 1;
        }

        if (start < sql.Length)
        {
            var last = sql[start..].Trim();
            if (last.Length > 0)
                yield return last;
        }
    }

    private static string ToPgText(object value) =>
        value switch
        {
            null => string.Empty,
            bool b => b ? "t" : "f",
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

    // -----------------------------------------------------------------------------
    // Extended Query helpers
    // -----------------------------------------------------------------------------

    private static short[] ReadFormatCodes(PgPayloadReader reader)
    {
        var count = reader.ReadInt16();
        var codes = new short[count];
        for (var i = 0; i < count; i++)
            codes[i] = reader.ReadInt16();

        return codes;
    }

    private static short ResolveFormatCode(short[] codes, int index)
    {
        if (codes.Length == 0) return 0;
        if (codes.Length == 1) return codes[0];
        if (index < 0 || index >= codes.Length) return 0;
        return codes[index];
    }

    /// <summary>
    /// Ersetzt <c>$N</c> und <c>?</c>-Platzhalter durch <c>@p{N-1}</c>,
    /// damit <see cref="WalhallaEngine.Prepare"/> das Statement kompilieren kann.
    /// Zeichenkettenliterale werden dabei nicht verändert.
    /// </summary>
    private static string RenderSqlWithParameterMarkers(string sql, int parameterCount)
    {
        var markerSql = Regex.Replace(
            sql,
            @"\$(\d+)",
            match =>
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                    return match.Value;

                if (ordinal < 1 || ordinal > parameterCount)
                    throw new InvalidOperationException($"Parameter ${ordinal} out of range.");

                return "@p" + (ordinal - 1);
            },
            RegexOptions.CultureInvariant);

        if (markerSql.IndexOf('?', StringComparison.Ordinal) < 0)
            return markerSql;

        var sb = new StringBuilder(markerSql.Length + (parameterCount * 4));
        var inString = false;
        var nextIndex = 0;

        for (var i = 0; i < markerSql.Length; i++)
        {
            var ch = markerSql[i];

            if (ch == '\'')
            {
                if (inString && i + 1 < markerSql.Length && markerSql[i + 1] == '\'')
                {
                    sb.Append("''");
                    i++;
                    continue;
                }

                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (!inString && ch == '?')
            {
                if (nextIndex >= parameterCount)
                    throw new InvalidOperationException($"Missing parameter marker ? at position {nextIndex + 1}.");

                sb.Append("@p").Append(nextIndex);
                nextIndex++;
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string RenderSqlWithParameters(string sql, IReadOnlyList<string?> parameterLiterals)
    {
        var rendered = Regex.Replace(
            sql,
            @"\$(\d+)",
            match =>
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                    return match.Value;

                var index = ordinal - 1;
                if (index < 0 || index >= parameterLiterals.Count)
                    throw new InvalidOperationException($"Missing bind value for parameter ${ordinal}.");

                return parameterLiterals[index] ?? "NULL";
            },
            RegexOptions.CultureInvariant);

        if (rendered.IndexOf('?', StringComparison.Ordinal) < 0)
            return rendered;

        var sb = new StringBuilder(rendered.Length + (parameterLiterals.Count * 8));
        var inString = false;
        var nextIndex = 0;

        for (var i = 0; i < rendered.Length; i++)
        {
            var ch = rendered[i];

            if (ch == '\'')
            {
                if (inString && i + 1 < rendered.Length && rendered[i + 1] == '\'')
                {
                    sb.Append("''");
                    i++;
                    continue;
                }

                inString = !inString;
                sb.Append(ch);
                continue;
            }

            if (!inString && ch == '?')
            {
                if (nextIndex >= parameterLiterals.Count)
                    throw new InvalidOperationException($"Missing bind value for parameter ? at position {nextIndex + 1}.");

                sb.Append(parameterLiterals[nextIndex] ?? "NULL");
                nextIndex++;
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static WalhallaPreparedStatement? TryCompilePgStatement(
        IPgWireBackendConnection backend,
        PgPreparedStatement prepared,
        string parameterizedSql,
        int parameterCount)
    {
        if (prepared.Compiled != null)
        {
            // Stelle sicher, dass das kompilierte Statement dieselbe Parametrisierung hat.
            if (prepared.ParameterizedSql == parameterizedSql
                && prepared.Compiled.GetPlan().ParameterCount == parameterCount)
            {
                return prepared.Compiled;
            }

            prepared.Compiled = null;
        }

        prepared.ParameterizedSql = parameterizedSql;

        if (backend is not WalhallaSqlPgWireBackend walhallaBackend)
            return null;

        try
        {
            var compiled = walhallaBackend.GetEngine().Prepare(parameterizedSql);
            if (compiled.GetPlan().ParameterCount != parameterCount)
                return null;

            prepared.Compiled = compiled;
            return compiled;
        }
        catch (Exception ex)
        {
            PgWireTrace.Sql("PREPARE-FALLBACK", $"{parameterizedSql} [{ex.Message}]");
            return null;
        }
    }

    private static IPgWireBackendReader ExecutePreparedReader(PgBoundPortal portal)
    {
        var prepared = portal.PreparedStatement!;
        prepared.ClearBindings();

        var values = portal.ParameterValues;
        if (values != null)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value != null)
                    prepared.Bind(i, value);
            }
        }

        return new WalhallaSqlPgWireBackend.WalhallaBackendReader(prepared.Execute());
    }

    private static string ToSqlLiteral(string input)
    {
        if (string.Equals(input, "null", StringComparison.OrdinalIgnoreCase))
            return "NULL";

        if (string.Equals(input, "t", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "true", StringComparison.OrdinalIgnoreCase))
            return "TRUE";

        if (string.Equals(input, "f", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "false", StringComparison.OrdinalIgnoreCase))
            return "FALSE";

        if (decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return input;

        return $"'{input.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string DecodeBindParameterLiteral(byte[] raw, short formatCode, int parameterTypeOid)
    {
        if (formatCode == 0)
            return ToSqlLiteral(Encoding.UTF8.GetString(raw));

        if (TryDecodeBinaryParameter(raw, parameterTypeOid, out var literal))
            return literal;

        if (TryDecodeUtf8Text(raw, out var utf8Text))
            return ToSqlLiteral(utf8Text);

        return ToSqlLiteral("\\x" + Convert.ToHexString(raw).ToLowerInvariant());
    }

    /// <summary>
    /// Wandelt einen Bind-Parameter in einen typisierten CLR-Wert um, der an
    /// <see cref="WalhallaPreparedStatement.Bind(int, object?)"/> übergeben werden kann.
    /// </summary>
    private static object? DecodeBindParameterValue(byte[] raw, short formatCode, int parameterTypeOid)
    {
        if (formatCode == 1)
        {
            if (TryDecodeBinaryParameterValue(raw, parameterTypeOid, out var value))
                return value;
        }

        if (!TryDecodeUtf8Text(raw, out var text))
            text = Convert.ToHexString(raw).ToLowerInvariant();

        return ParseTextParameterValue(text, parameterTypeOid);
    }

    private static object? ParseTextParameterValue(string text, int parameterTypeOid)
    {
        switch (parameterTypeOid)
        {
            case 16:
                return text.Equals("t", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text == "1";

            case 21:
                if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                    return s;
                break;

            case 23 or 26:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return i;
                break;

            case 20:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;
                break;

            case 700:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return f;
                break;

            case 701:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                break;

            case 1700:
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m))
                    return m;
                break;

            case 1114 or 1184:
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    return dt;
                break;

            case 2950:
                if (Guid.TryParse(text, out var g))
                    return g;
                break;

            case 17:
                if (text.StartsWith("\\\\x", StringComparison.Ordinal) && text.Length > 2)
                {
                    try { return Convert.FromHexString(text.AsSpan(2)); }
                    catch { /* fall through to string */ }
                }
                break;
        }

        return InferTextParameterValue(text);
    }

    private static object? InferTextParameterValue(string text)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("t", StringComparison.OrdinalIgnoreCase))
            return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("f", StringComparison.OrdinalIgnoreCase))
            return false;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m))
            return m;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        if (Guid.TryParse(text, out var g))
            return g;
        return text;
    }

    private static bool TryDecodeBinaryParameter(byte[] raw, int parameterTypeOid, out string literal)
    {
        switch (parameterTypeOid)
        {
            case 16 when raw.Length == 1:
                literal = raw[0] == 0 ? "FALSE" : "TRUE";
                return true;

            case 21 when raw.Length == 2:
                literal = BinaryPrimitives.ReadInt16BigEndian(raw).ToString(CultureInfo.InvariantCulture);
                return true;

            case 23 or 26 when raw.Length == 4:
                literal = BinaryPrimitives.ReadInt32BigEndian(raw).ToString(CultureInfo.InvariantCulture);
                return true;

            case 20 when raw.Length == 8:
                literal = BinaryPrimitives.ReadInt64BigEndian(raw).ToString(CultureInfo.InvariantCulture);
                return true;

            case 700 when raw.Length == 4:
                literal = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(raw)).ToString("R", CultureInfo.InvariantCulture);
                return true;

            case 701 when raw.Length == 8:
                literal = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(raw)).ToString("R", CultureInfo.InvariantCulture);
                return true;

            case 18 or 19 or 25 or 1042 or 1043:
                if (TryDecodeUtf8Text(raw, out var text))
                {
                    literal = ToSqlLiteral(text);
                    return true;
                }
                break;
        }

        if (parameterTypeOid == 0)
        {
            if (raw.Length == 1 && (raw[0] == 0 || raw[0] == 1)) { literal = raw[0] == 0 ? "FALSE" : "TRUE"; return true; }
            if (raw.Length == 2) { literal = BinaryPrimitives.ReadInt16BigEndian(raw).ToString(CultureInfo.InvariantCulture); return true; }
            if (raw.Length == 4) { literal = BinaryPrimitives.ReadInt32BigEndian(raw).ToString(CultureInfo.InvariantCulture); return true; }
            if (raw.Length == 8) { literal = BinaryPrimitives.ReadInt64BigEndian(raw).ToString(CultureInfo.InvariantCulture); return true; }
            if (TryDecodeUtf8Text(raw, out var text)) { literal = ToSqlLiteral(text); return true; }
        }

        literal = string.Empty;
        return false;
    }

    private static bool TryDecodeBinaryParameterValue(byte[] raw, int parameterTypeOid, out object? value)
    {
        switch (parameterTypeOid)
        {
            case 16 when raw.Length == 1:
                value = raw[0] != 0;
                return true;

            case 21 when raw.Length == 2:
                value = BinaryPrimitives.ReadInt16BigEndian(raw);
                return true;

            case 23 or 26 when raw.Length == 4:
                value = BinaryPrimitives.ReadInt32BigEndian(raw);
                return true;

            case 20 when raw.Length == 8:
                value = BinaryPrimitives.ReadInt64BigEndian(raw);
                return true;

            case 700 when raw.Length == 4:
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(raw));
                return true;

            case 701 when raw.Length == 8:
                value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(raw));
                return true;
        }

        if (parameterTypeOid == 0)
        {
            if (raw.Length == 1) { value = raw[0] != 0; return true; }
            if (raw.Length == 2) { value = BinaryPrimitives.ReadInt16BigEndian(raw); return true; }
            if (raw.Length == 4) { value = BinaryPrimitives.ReadInt32BigEndian(raw); return true; }
            if (raw.Length == 8) { value = BinaryPrimitives.ReadInt64BigEndian(raw); return true; }
        }

        value = null;
        return false;
    }

    private static bool TryDecodeUtf8Text(byte[] raw, out string text)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(raw);
            for (var i = 0; i < decoded.Length; i++)
            {
                var ch = decoded[i];
                if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                {
                    text = string.Empty;
                    return false;
                }
            }

            text = decoded;
            return true;
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    // -----------------------------------------------------------------------------
    // Field / schema inference
    // -----------------------------------------------------------------------------

    private static bool TryExpandSingleJsonColumnResult(
        IPgWireBackendReader reader,
        out (string Name, Type ClrType)[] fields,
        out List<string?[]> rows)
    {
        fields = Array.Empty<(string Name, Type ClrType)>();
        rows = new List<string?[]>();

        if (reader.FieldCount != 1)
            return false;

        var fieldName = reader.GetName(0);
        if (!string.Equals(fieldName, "?column?", StringComparison.OrdinalIgnoreCase))
            return false;

        var columnOrder = new List<string>();
        var objectRows = new List<Dictionary<string, string?>>(16);

        while (reader.Read())
        {
            var rowMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            if (!reader.IsDBNull(0))
            {
                var cellValue = reader.GetValue(0);
                var raw = cellValue switch
                {
                    string text => text,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => Convert.ToString(cellValue, CultureInfo.InvariantCulture)
                };

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (!columnOrder.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                                    columnOrder.Add(prop.Name);

                                rowMap[prop.Name] = prop.Value.ValueKind switch
                                {
                                    JsonValueKind.Null => null,
                                    JsonValueKind.String => prop.Value.GetString(),
                                    JsonValueKind.True => "t",
                                    JsonValueKind.False => "f",
                                    JsonValueKind.Number => prop.Value.GetRawText(),
                                    _ => prop.Value.GetRawText()
                                };
                            }
                        }
                        else
                        {
                            rowMap["value"] = raw;
                            if (!columnOrder.Contains("value", StringComparer.OrdinalIgnoreCase))
                                columnOrder.Add("value");
                        }
                    }
                    catch
                    {
                        rowMap["value"] = raw;
                        if (!columnOrder.Contains("value", StringComparer.OrdinalIgnoreCase))
                            columnOrder.Add("value");
                    }
                }
            }

            objectRows.Add(rowMap);
        }

        if (columnOrder.Count == 0)
            columnOrder.Add("value");

        fields = columnOrder.Select(name => (name, typeof(string))).ToArray();

        foreach (var objectRow in objectRows)
        {
            var values = new string?[columnOrder.Count];
            for (var i = 0; i < columnOrder.Count; i++)
                values[i] = objectRow.TryGetValue(columnOrder[i], out var value) ? value : null;

            rows.Add(values);
        }

        return true;
    }

    private static IReadOnlyList<(string Name, Type ClrType)> TryReadFields(IPgWireBackendConnection backend, PgDbSessionState session, string sql)
    {
        using var command = backend.CreateCommand();
        command.CommandText = sql;
        if (session.Transaction != null)
            command.Transaction = session.Transaction;

        using var reader = command.ExecuteReader();
        var fields = new (string Name, Type ClrType)[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            fields[i] = (reader.GetName(i), reader.GetFieldType(i));

        return fields;
    }

    private static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromKnownTables(string sql, IReadOnlyList<PgVirtualTableDefinition> tables)
    {
        if (string.IsNullOrWhiteSpace(sql) || tables.Count == 0)
            return Array.Empty<(string Name, Type ClrType)>();

        var normalizedSql = NormalizeSqlForExecution(sql);
        var match = Regex.Match(
            normalizedSql,
            @"\bfrom\s+(?<table>""[^""]+""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\s+as)?(?:\s+[a-zA-Z_][a-zA-Z0-9_]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
            return Array.Empty<(string Name, Type ClrType)>();

        var tableToken = match.Groups["table"].Value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(tableToken))
            return Array.Empty<(string Name, Type ClrType)>();

        var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableToken, StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return Array.Empty<(string Name, Type ClrType)>();

        if (table.Columns.Count == 0)
            return new[] { ("Id", typeof(int)), ("Name", typeof(string)) };

        return table.Columns.Select(c => (c.Name, MapInformationSchemaTypeToClrType(c.DataType))).ToArray();
    }

    private static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromSampleJsonRow(IPgWireBackendConnection backend, PgDbSessionState session, string sql)
    {
        try
        {
            using var command = backend.CreateCommand();
            command.CommandText = NormalizeSqlForExecution(sql);
            if (session.Transaction != null)
                command.Transaction = session.Transaction;

            using var reader = command.ExecuteReader();
            if (reader.FieldCount != 1 || !reader.Read() || reader.IsDBNull(0))
                return Array.Empty<(string Name, Type ClrType)>();

            var raw = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<(string Name, Type ClrType)>();

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<(string Name, Type ClrType)>();

            var fields = new List<(string Name, Type ClrType)>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var clrType = prop.Value.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False => typeof(bool),
                    _ => typeof(string)
                };
                fields.Add((prop.Name, clrType));
            }

            return fields;
        }
        catch
        {
            return Array.Empty<(string Name, Type ClrType)>();
        }
    }

    private static Type MapInformationSchemaTypeToClrType(string? dataType)
    {
        var normalized = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "smallint" => typeof(short),
            "integer" or "int" => typeof(int),
            "bigint" => typeof(long),
            "decimal" or "numeric" => typeof(decimal),
            "double precision" or "real" => typeof(double),
            "boolean" => typeof(bool),
            "bytea" => typeof(byte[]),
            "timestamp with time zone" or "timestamp" => typeof(DateTime),
            "uuid" => typeof(Guid),
            _ => typeof(string)
        };
    }

    private static string RewriteSelectStarWithKnownColumns(string sql, IReadOnlyList<PgVirtualTableDefinition> tables)
    {
        if (string.IsNullOrWhiteSpace(sql) || tables.Count == 0)
            return sql;

        var normalized = NormalizeSqlForExecution(sql);
        var match = Regex.Match(
            normalized,
            @"^\s*select\s+(?<proj>\*|[a-zA-Z_][a-zA-Z0-9_]*\.\*)\s+from\s+(?<table>[a-zA-Z_][a-zA-Z0-9_]*)(?<tail>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        if (!match.Success)
            return normalized;

        var tableName = match.Groups["table"].Value;
        var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return normalized;

        var columns = table.Columns.Count == 0
            ? new[] { "Id", "Name" }
            : table.Columns.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

        if (columns.Length == 0)
            columns = new[] { "Id", "Name" };

        var projection = string.Join(", ", columns.Select(static c => $"\"{c.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
        var tail = match.Groups["tail"].Value;

        return $"SELECT {projection} FROM {tableName}{tail}";
    }

    private static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromSelect(string sql)
    {
        var names = ExtractSelectedColumns(sql);
        if (names.Count == 0)
            return Array.Empty<(string Name, Type ClrType)>();

        return names.Select(name => (string.IsNullOrWhiteSpace(name) ? "?column?" : name, typeof(string))).ToList();
    }

    /// <summary>
    /// Overrides the ClrType in a fields list using catalog-derived schema types.
    /// Fields that are not in schemaTypes keep their original type.
    /// </summary>
    private static IReadOnlyList<(string Name, Type ClrType)> ApplySchemaTypes(
        IReadOnlyList<(string Name, Type ClrType)> fields,
        Dictionary<string, Type> schemaTypes)
    {
        if (schemaTypes.Count == 0) return fields;
        return fields.Select(f =>
        {
            if (TryResolveSchemaTypeForField(f.Name, schemaTypes, out var t) && t != typeof(object))
                return (f.Name, t);
            return f;
        }).ToList();
    }

    private static bool TryResolveSchemaTypeForField(string fieldName, IReadOnlyDictionary<string, Type> schemaTypes, out Type type)
    {
        if (schemaTypes.TryGetValue(fieldName, out var directType))
        {
            type = directType;
            return true;
        }

        var dotIndex = fieldName.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < fieldName.Length)
        {
            var unqualified = fieldName[(dotIndex + 1)..];
            if (schemaTypes.TryGetValue(unqualified, out var unqualifiedType))
            {
                type = unqualifiedType;
                return true;
            }
        }

        type = typeof(object);
        return false;
    }

    /// <summary>
    /// Tries to refine column types using the table catalog discovered from the database engine.
    /// Returns a dictionary mapping column name (case-insensitive) to CLR type.
    /// Falls back gracefully if schema lookup fails.
    /// </summary>
    private static Dictionary<string, Type> TryLoadSchemaColumnTypes(IPgWireBackendConnection backend, PgDbSessionState session, string sql)
    {
        var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Extract table name from "FROM <table>"
            var match = Regex.Match(sql, @"\bfrom\s+(?<table>""?[a-zA-Z_][a-zA-Z0-9_]*""?)(?:\s+(?:as\s+)?[a-zA-Z_][a-zA-Z0-9_]*)?(?:\s+(?:where|order|group|limit|left|right|inner|join|on|having|\z))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) { return result; }

            var tableName = match.Groups["table"].Value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(tableName)) return result;

            // Discover table definitions from the engine catalog
            var knownTables = DiscoverTableDefinitions(backend, session);
            var table = knownTables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null) { return result; }

            foreach (var col in table.Columns)
            {
                if (!string.IsNullOrWhiteSpace(col.Name) && !string.IsNullOrWhiteSpace(col.DataType))
                    result[col.Name] = MapInformationSchemaTypeToClrType(col.DataType);
            }
        }
        catch { }
        return result;
    }

    private static IReadOnlyList<(string Name, Type ClrType)> InferFallbackQueryFields(string sql)
    {
        var names = ExtractSelectedColumns(sql);
        if (names.Count > 0)
            return names.Select(name => (string.IsNullOrWhiteSpace(name) ? "?column?" : name, typeof(string))).ToList();

        return new[] { ("?column?", typeof(string)) };
    }

    private static string NormalizeSelectSql(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;
        // Remove trailing semicolon
        sql = sql.AsSpan().TrimEnd().TrimEnd(';').ToString();
        // Strip [] quoting from identifiers (e.g. [Customers] → Customers)
        if (sql.Contains('['))
            sql = sql.Replace("[", "").Replace("]", "");
        return sql;
    }

    private static List<string> ExtractSelectedColumns(string sql)
    {
        sql = NormalizeSelectSql(sql);

        // Collapse all whitespace (including newlines/tabs) to single spaces
        // so that "SELECT col\nFROM table" is found correctly.
        var collapsed = Regex.Replace(sql, @"\s+", " ", RegexOptions.CultureInvariant);
        var lower = collapsed.ToLowerInvariant();
        var selectIndex = lower.IndexOf("select", StringComparison.Ordinal);
        var fromIndex = lower.IndexOf(" from ", StringComparison.Ordinal);
        if (selectIndex < 0 || fromIndex <= selectIndex)
            return new List<string>();

        var projection = collapsed[(selectIndex + 6)..fromIndex].Trim();
        if (projection == "*" || projection.Length == 0)
            return new List<string>();

        var parts = projection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var normalizedColumns = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            if (token == "*" || token.EndsWith(".*", StringComparison.Ordinal)) return new List<string>();

            var asIndex = token.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
            if (asIndex >= 0 && asIndex + 4 < token.Length)
                token = token[(asIndex + 4)..].Trim();
            else
            {
                var ws = token.LastIndexOf(' ');
                if (ws > 0 && ws + 1 < token.Length && token.IndexOf('(') < 0)
                    token = token[(ws + 1)..].Trim();
            }

            var dot = token.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < token.Length)
                token = token[(dot + 1)..];

            var cast = token.IndexOf("::", StringComparison.Ordinal);
            if (cast > 0)
                token = token[..cast];

            token = token.Trim('"');
            if (token.Length > 0)
                normalizedColumns.Add(token);
        }

        return normalizedColumns;
    }

    private static string NormalizeQualifiedProjectionToken(string projection)
    {
        var token = projection.Trim();

        var asIndex = token.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0 && asIndex + 4 < token.Length)
            token = token[..asIndex].Trim();

        var cast = token.IndexOf("::", StringComparison.Ordinal);
        if (cast > 0)
            token = token[..cast];

        var segments = token
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => segment.Trim('"'))
            .ToArray();

        return segments.Length == 0
            ? token.Trim('"')
            : string.Join('.', segments);
    }

    // -----------------------------------------------------------------------------
    // Virtual catalog / system tables
    // -----------------------------------------------------------------------------

    private static bool TryResolveVirtualQuery(string sql, string? databaseName, IReadOnlyList<PgVirtualTableDefinition>? tables, out PgVirtualQueryResult result,
        string databaseCollation = "C", string databaseCType = "C",
        IReadOnlyList<Dictionary<string, object?>>? pgStatsRows = null)
    {
        var normalized = NormalizeSqlForCatalogDetection(sql);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            result = new PgVirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
            return false;
        }

        if (TryResolveScalarStartupQuery(sql, normalized, databaseName, out result))
        {
            PgWireTrace.Virtual("scalar", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.StartsWith("select version()", StringComparison.Ordinal))
        {
            result = new PgVirtualQueryResult(
                new[] { ("version", typeof(string)) },
                new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["version"] = "LayeredSql PgWire 16.0-layeredsql" } });
            PgWireTrace.Virtual("version", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("current_database()", StringComparison.Ordinal))
        {
            result = new PgVirtualQueryResult(
                new[] { ("current_database", typeof(string)) },
                new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["current_database"] = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName } });
            PgWireTrace.Virtual("current_database", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("current_schema()", StringComparison.Ordinal))
        {
            result = new PgVirtualQueryResult(
                new[] { ("current_schema", typeof(string)) },
                new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["current_schema"] = "public" } });
            PgWireTrace.Virtual("current_schema", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("information_schema.schemata", StringComparison.Ordinal))
        {
            var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
            var rows = new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["catalog_name"] = dbName, ["schema_name"] = "pg_catalog", ["schema_owner"] = "postgres" },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["catalog_name"] = dbName, ["schema_name"] = "public", ["schema_owner"] = "postgres" },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["catalog_name"] = dbName, ["schema_name"] = "information_schema", ["schema_owner"] = "postgres" }
            };
            result = BuildProjectedVirtualResult(sql, rows, new[] { ("catalog_name", typeof(string)), ("schema_name", typeof(string)), ("schema_owner", typeof(string)) });
            PgWireTrace.Virtual("information_schema.schemata", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_namespace", StringComparison.Ordinal))
        {
            var rows = new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["oid"] = 11, ["nspname"] = "pg_catalog", ["nspowner"] = 10, ["nspacl"] = null, ["description"] = null },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["oid"] = 2200, ["nspname"] = "public", ["nspowner"] = 10, ["nspacl"] = null, ["description"] = null },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["oid"] = 13207, ["nspname"] = "information_schema", ["nspowner"] = 10, ["nspacl"] = null, ["description"] = null }
            };
            result = BuildProjectedVirtualResult(sql, rows, new[] { ("oid", typeof(int)), ("nspname", typeof(string)), ("nspowner", typeof(int)), ("nspacl", typeof(string)), ("description", typeof(string)) });
            PgWireTrace.Virtual("pg_namespace", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_collation", StringComparison.Ordinal))
        {
            var rows = new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = 100, ["collname"] = "default", ["collnamespace"] = 11,
                    ["collowner"] = 10, ["collprovider"] = "c", ["collencoding"] = 6,
                    ["collcollate"] = "C", ["collctype"] = "C", ["colliculocale"] = "C",
                    ["collversion"] = null, ["collisdeterministic"] = true
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = 950, ["collname"] = "de-DE-x-icu", ["collnamespace"] = 11,
                    ["collowner"] = 10, ["collprovider"] = "i", ["collencoding"] = 6,
                    ["collcollate"] = "de_DE.utf8", ["collctype"] = "de_DE.utf8",
                    ["colliculocale"] = "de-DE", ["collversion"] = null, ["collisdeterministic"] = true
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = 951, ["collname"] = "en-US-x-icu", ["collnamespace"] = 11,
                    ["collowner"] = 10, ["collprovider"] = "i", ["collencoding"] = 6,
                    ["collcollate"] = "en_US.utf8", ["collctype"] = "en_US.utf8",
                    ["colliculocale"] = "en-US", ["collversion"] = null, ["collisdeterministic"] = true
                },
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = 952, ["collname"] = "tr-TR-x-icu", ["collnamespace"] = 11,
                    ["collowner"] = 10, ["collprovider"] = "i", ["collencoding"] = 6,
                    ["collcollate"] = "tr_TR.utf8", ["collctype"] = "tr_TR.utf8",
                    ["colliculocale"] = "tr-TR", ["collversion"] = null, ["collisdeterministic"] = true
                }
            };
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("oid", typeof(int)), ("collname", typeof(string)), ("collnamespace", typeof(int)),
                ("collowner", typeof(int)), ("collprovider", typeof(string)), ("collencoding", typeof(int)),
                ("collcollate", typeof(string)), ("collctype", typeof(string)), ("colliculocale", typeof(string)),
                ("collversion", typeof(string)), ("collisdeterministic", typeof(bool))
            });
            PgWireTrace.Virtual("pg_collation", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_database", StringComparison.Ordinal))
        {
            var rows = new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = 16384, ["datname"] = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName,
                    ["datdba"] = 10, ["encoding"] = 6,
                    ["datcollate"] = databaseCollation, ["datctype"] = databaseCType,
                    ["datistemplate"] = false, ["datallowconn"] = true
                }
            };
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("oid", typeof(int)), ("datname", typeof(string)), ("datdba", typeof(int)),
                ("encoding", typeof(int)), ("datcollate", typeof(string)), ("datctype", typeof(string)),
                ("datistemplate", typeof(bool)), ("datallowconn", typeof(bool))
            });
            return true;
        }

        if (Regex.IsMatch(normalized, @"\bpg_catalog\.pg_tablespace\b", RegexOptions.CultureInvariant))
        {
            var rows = new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["oid"] = 1663, ["spcname"] = "pg_default", ["spcowner"] = 10, ["spcacl"] = null, ["spcoptions"] = null, ["loc"] = string.Empty } };
            result = BuildProjectedVirtualResult(sql, rows, new[] { ("oid", typeof(int)), ("spcname", typeof(string)), ("spcowner", typeof(int)), ("spcacl", typeof(string)), ("spcoptions", typeof(string)), ("loc", typeof(string)) });
            PgWireTrace.Virtual("pg_tablespace", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("information_schema.tables", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"\bpg_catalog\.pg_tables\b", RegexOptions.CultureInvariant))
        {
            var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var onlyBaseTables = Regex.IsMatch(normalized, @"\bpg_catalog\.pg_tables\b", RegexOptions.CultureInvariant);
            var rows = resolvedTables
                .Where(relation => !string.IsNullOrWhiteSpace(relation.Name))
                .Where(relation => !onlyBaseTables || relation.RelationKind == PgVirtualRelationKind.Table)
                .DistinctBy(static relation => relation.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static relation => relation.RelationKind == PgVirtualRelationKind.View ? 1 : 0)
                .ThenBy(static relation => relation.Name, StringComparer.OrdinalIgnoreCase)
                .Select(relation => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = dbName, ["table_schema"] = "public", ["table_name"] = relation.Name,
                    ["table_type"] = relation.RelationKind == PgVirtualRelationKind.View ? "VIEW" : "BASE TABLE",
                    ["schemaname"] = "public", ["tablename"] = relation.Name,
                    ["tableowner"] = "postgres", ["hasindexes"] = relation.RelationKind == PgVirtualRelationKind.Table,
                    ["hasrules"] = relation.RelationKind == PgVirtualRelationKind.View,
                    ["hastriggers"] = false, ["rowsecurity"] = false
                }).ToArray();
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("table_catalog", typeof(string)), ("table_schema", typeof(string)), ("table_name", typeof(string)),
                ("table_type", typeof(string)), ("schemaname", typeof(string)), ("tablename", typeof(string)),
                ("tableowner", typeof(string)), ("hasindexes", typeof(bool)), ("hasrules", typeof(bool)),
                ("hastriggers", typeof(bool)), ("rowsecurity", typeof(bool))
            });
            PgWireTrace.Virtual("tables", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("information_schema.views", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"\bpg_catalog\.pg_views\b", RegexOptions.CultureInvariant))
        {
            var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var rows = resolvedTables
                .Where(static relation => relation.RelationKind == PgVirtualRelationKind.View)
                .OrderBy(static relation => relation.Name, StringComparer.OrdinalIgnoreCase)
                .Select(relation => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = dbName,
                    ["table_schema"] = "public",
                    ["table_name"] = relation.Name,
                    ["viewname"] = relation.Name,
                    ["schemaname"] = "public",
                    ["definition"] = null
                })
                .ToArray();
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("table_catalog", typeof(string)), ("table_schema", typeof(string)), ("table_name", typeof(string)),
                ("viewname", typeof(string)), ("schemaname", typeof(string)), ("definition", typeof(string))
            });
            PgWireTrace.Virtual("views", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("information_schema.columns", StringComparison.Ordinal))
        {
            var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var table in resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var columns = table.Columns.Count == 0
                    ? table.RelationKind == PgVirtualRelationKind.Table
                        ? new[]
                        {
                            new PgVirtualColumnDefinition("Id", "integer", IsNullable: false, IsPrimaryKey: true),
                            new PgVirtualColumnDefinition("Name", "text", IsNullable: true, IsPrimaryKey: false)
                        }
                        : Array.Empty<PgVirtualColumnDefinition>()
                    : table.Columns;
                for (var i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];
                    rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["table_catalog"] = dbName, ["table_schema"] = "public", ["table_name"] = table.Name,
                        ["column_name"] = col.Name, ["ordinal_position"] = i + 1,
                        ["is_nullable"] = col.IsNullable ? "YES" : "NO", ["data_type"] = col.DataType,
                        ["is_primary_key"] = col.IsPrimaryKey ? "YES" : "NO"
                    });
                }
            }
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("table_catalog", typeof(string)), ("table_schema", typeof(string)), ("table_name", typeof(string)),
                ("column_name", typeof(string)), ("ordinal_position", typeof(int)), ("is_nullable", typeof(string)),
                ("data_type", typeof(string)), ("is_primary_key", typeof(string))
            });
            PgWireTrace.Virtual("columns", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.StartsWith("select ", StringComparison.Ordinal) && normalized.Contains("information_schema.", StringComparison.Ordinal))
        {
            var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var rows = resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(table => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = dbName, ["table_schema"] = "public", ["table_name"] = table.Name,
                    ["column_name"] = table.Columns.Count > 0 ? table.Columns[0].Name : "Id",
                    ["data_type"] = table.Columns.Count > 0 ? table.Columns[0].DataType : "integer"
                }).ToArray();
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("table_catalog", typeof(string)), ("table_schema", typeof(string)), ("table_name", typeof(string)),
                ("column_name", typeof(string)), ("data_type", typeof(string))
            });
            PgWireTrace.Virtual("information_schema.generic", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_class", StringComparison.Ordinal) || normalized.Contains("from pg_class", StringComparison.Ordinal))
        {
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var rows = resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(table => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["oid"] = StableOid(table.Name), ["relname"] = table.Name, ["relnamespace"] = 2200,
                    ["relowner"] = 10, ["relkind"] = table.RelationKind == PgVirtualRelationKind.View ? "v" : "r", ["relam"] = 0,
                    ["relhasindex"] = table.RelationKind == PgVirtualRelationKind.Table,
                    ["relnatts"] = table.Columns.Count == 0 ? 2 : table.Columns.Count, ["relpersistence"] = "p"
                }).ToArray();
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("oid", typeof(int)), ("relname", typeof(string)), ("relnamespace", typeof(int)),
                ("relowner", typeof(int)), ("relkind", typeof(string)), ("relam", typeof(int)),
                ("relhasindex", typeof(bool)), ("relnatts", typeof(int)), ("relpersistence", typeof(string))
            });
            PgWireTrace.Virtual("pg_class", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_inherits", StringComparison.Ordinal) || normalized.Contains("from pg_inherits", StringComparison.Ordinal))
        {
            result = BuildProjectedVirtualResult(sql, Array.Empty<Dictionary<string, object?>>(), new[] { ("inhrelid", typeof(int)), ("inhparent", typeof(int)), ("inhseqno", typeof(int)), ("relnamespace", typeof(int)) });
            PgWireTrace.Virtual("pg_inherits", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_constraint", StringComparison.Ordinal) || normalized.Contains("from pg_constraint", StringComparison.Ordinal))
        {
            result = BuildProjectedVirtualResult(sql, Array.Empty<Dictionary<string, object?>>(), new[]
            {
                ("oid", typeof(int)), ("conname", typeof(string)), ("connamespace", typeof(int)), ("contype", typeof(string)),
                ("condeferrable", typeof(bool)), ("condeferred", typeof(bool)), ("convalidated", typeof(bool)),
                ("conrelid", typeof(int)), ("contypid", typeof(int)), ("conindid", typeof(int)), ("conparentid", typeof(int)),
                ("confrelid", typeof(int)), ("confupdtype", typeof(string)), ("confdeltype", typeof(string)),
                ("confmatchtype", typeof(string)), ("conislocal", typeof(bool)), ("coninhcount", typeof(int)),
                ("connoinherit", typeof(bool)), ("conkey", typeof(string)), ("confkey", typeof(string)),
                ("conpfeqop", typeof(string)), ("conppeqop", typeof(string)), ("conffeqop", typeof(string)),
                ("confdelsetcols", typeof(string)), ("conexclop", typeof(string)), ("conbin", typeof(string)),
                ("tabrelname", typeof(string)), ("refnamespace", typeof(int)), ("description", typeof(string)), ("src", typeof(string))
            });
            PgWireTrace.Virtual("pg_constraint", normalized, result.Rows.Count);
            return true;
        }

        // -- Npgsql bootstrap: composite-type fields query (FROM pg_type JOIN pg_attribute)
        // Returns 3 columns (oid, attname, atttypid); we have no composite types ? empty set.
        if ((normalized.Contains("from pg_catalog.pg_type", StringComparison.Ordinal) || normalized.Contains("from pg_type", StringComparison.Ordinal))
            && normalized.Contains("join pg_attribute", StringComparison.Ordinal))
        {
            result = BuildProjectedVirtualResult(sql, Array.Empty<Dictionary<string, object?>>(),
                new[] { ("oid", typeof(int)), ("attname", typeof(string)), ("atttypid", typeof(int)) });
            PgWireTrace.Virtual("pg_type_composite_empty", normalized, 0);
            return true;
        }

        // -- Npgsql bootstrap: enum labels (FROM pg_enum)
        // Returns 2 columns (oid, enumlabel); we have no enum types ? empty set.
        if (normalized.Contains("from pg_enum", StringComparison.Ordinal))
        {
            result = BuildProjectedVirtualResult(sql, Array.Empty<Dictionary<string, object?>>(),
                new[] { ("oid", typeof(int)), ("enumlabel", typeof(string)) });
            PgWireTrace.Virtual("pg_enum_empty", normalized, 0);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_type", StringComparison.Ordinal) || normalized.Contains("from pg_type", StringComparison.Ordinal))
        {
            var pgTypeRows = new[]
            {
                BuildPgTypeRow(16, "bool", 1, true, "B", "b", 0),
                BuildPgTypeRow(17, "bytea", -1, false, "U", "b", 0),
                BuildPgTypeRow(20, "int8", 8, true, "N", "b", 0),
                BuildPgTypeRow(21, "int2", 2, true, "N", "b", 0),
                BuildPgTypeRow(23, "int4", 4, true, "N", "b", 0),
                BuildPgTypeRow(25, "text", -1, false, "S", "b", 0, typcollation: 100),
                BuildPgTypeRow(700, "float4", 4, true, "N", "b", 0),
                BuildPgTypeRow(701, "float8", 8, true, "N", "b", 0),
                BuildPgTypeRow(1043, "varchar", -1, false, "S", "b", 0, typcollation: 100),
                BuildPgTypeRow(1114, "timestamp", 8, true, "D", "b", 0),
                BuildPgTypeRow(1700, "numeric", -1, false, "N", "b", 0),
                BuildPgTypeRow(2950, "uuid", 16, false, "U", "b", 0),
                BuildPgTypeRow(1184, "timestamptz", 8, true, "D", "b", 0)
            };
            result = BuildProjectedVirtualResult(sql, pgTypeRows, new[]
            {
                ("nspname", typeof(string)),
                ("oid", typeof(int)), ("typname", typeof(string)), ("typnamespace", typeof(int)), ("typowner", typeof(int)),
                ("typlen", typeof(short)), ("typbyval", typeof(bool)), ("typtype", typeof(string)), ("typcategory", typeof(string)),
                ("typispreferred", typeof(bool)), ("typisdefined", typeof(bool)), ("typdelim", typeof(string)),
                ("typrelid", typeof(int)), ("typelem", typeof(int)), ("elemtypoid", typeof(int)), ("typarray", typeof(int)),
                ("typinput", typeof(string)), ("typoutput", typeof(string)), ("typreceive", typeof(string)),
                ("typsend", typeof(string)), ("typmodin", typeof(string)), ("typmodout", typeof(string)),
                ("typanalyze", typeof(string)), ("typalign", typeof(string)), ("typstorage", typeof(string)),
                ("typnotnull", typeof(bool)), ("typbasetype", typeof(int)), ("typtypmod", typeof(int)),
                ("typndims", typeof(int)), ("typcollation", typeof(int)), ("typdefaultbin", typeof(string)),
                ("typdefault", typeof(string)), ("typacl", typeof(string)), ("base_type_name", typeof(string)),
                ("description", typeof(string)), ("relkind", typeof(string))
            });
            PgWireTrace.Virtual("pg_type", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_catalog.pg_attribute", StringComparison.Ordinal) || normalized.Contains("from pg_attribute", StringComparison.Ordinal))
        {
            var resolvedTables = tables ?? Array.Empty<PgVirtualTableDefinition>();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var table in resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tableOid = StableOid(table.Name);
                var tableColumns = table.Columns.Count == 0
                    ? table.RelationKind == PgVirtualRelationKind.Table
                        ? new[]
                        {
                            new PgVirtualColumnDefinition("Id", "integer", IsNullable: false, IsPrimaryKey: true),
                            new PgVirtualColumnDefinition("Name", "text", IsNullable: true, IsPrimaryKey: false)
                        }
                        : Array.Empty<PgVirtualColumnDefinition>()
                    : table.Columns;
                for (var i = 0; i < tableColumns.Count; i++)
                {
                    var column = tableColumns[i];
                    var typeOid = MapInformationSchemaTypeToPgTypeOid(column.DataType);
                    rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["relname"] = table.Name, ["tabrelname"] = table.Name, ["attrelid"] = tableOid,
                        ["attname"] = column.Name, ["atttypid"] = typeOid, ["attstattarget"] = -1,
                        ["attlen"] = MapPgTypeSizeFromOid(typeOid), ["attnum"] = i + 1, ["attndims"] = 0,
                        ["attcacheoff"] = -1, ["atttypmod"] = -1, ["attbyval"] = false, ["attalign"] = "i",
                        ["attstorage"] = "p", ["attcompression"] = string.Empty, ["attnotnull"] = !column.IsNullable,
                        ["atthasdef"] = false, ["atthasmissing"] = false, ["attidentity"] = string.Empty,
                        ["attgenerated"] = string.Empty, ["attisdropped"] = false, ["attislocal"] = true,
                        ["attinhcount"] = 0, ["attcollation"] = GetCollationOid(column.Collation), ["attacl"] = null, ["attoptions"] = null,
                        ["attfdwoptions"] = null, ["attmissingval"] = null, ["def_value"] = null,
                        ["description"] = null, ["objid"] = tableOid
                    });
                }
            }
            result = BuildProjectedVirtualResult(sql, rows, new[]
            {
                ("relname", typeof(string)), ("tabrelname", typeof(string)), ("attrelid", typeof(int)),
                ("attname", typeof(string)), ("atttypid", typeof(int)), ("attstattarget", typeof(int)),
                ("attlen", typeof(short)), ("attnum", typeof(int)), ("attndims", typeof(int)),
                ("attcacheoff", typeof(int)), ("atttypmod", typeof(int)), ("attbyval", typeof(bool)),
                ("attalign", typeof(string)), ("attstorage", typeof(string)), ("attcompression", typeof(string)),
                ("attnotnull", typeof(bool)), ("atthasdef", typeof(bool)), ("atthasmissing", typeof(bool)),
                ("attidentity", typeof(string)), ("attgenerated", typeof(string)), ("attisdropped", typeof(bool)),
                ("attislocal", typeof(bool)), ("attinhcount", typeof(int)), ("attcollation", typeof(int)),
                ("attacl", typeof(string)), ("attoptions", typeof(string)), ("attfdwoptions", typeof(string)),
                ("attmissingval", typeof(string)), ("def_value", typeof(string)), ("description", typeof(string)), ("objid", typeof(int))
            });
            PgWireTrace.Virtual("pg_attribute", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.StartsWith("select ", StringComparison.Ordinal)
            && (normalized.Contains("pg_catalog.", StringComparison.Ordinal)
                || normalized.Contains("information_schema.", StringComparison.Ordinal)
                || normalized.Contains("from pg_type", StringComparison.Ordinal)
                || normalized.Contains("from pg_class", StringComparison.Ordinal)
                || normalized.Contains("from pg_attribute", StringComparison.Ordinal)
                || normalized.Contains("from pg_index", StringComparison.Ordinal)))
        {
            result = BuildProjectedVirtualResult(sql, Array.Empty<Dictionary<string, object?>>(), new[]
            {
                ("oid", typeof(int)), ("nspname", typeof(string)), ("relname", typeof(string)),
                ("typname", typeof(string)), ("attname", typeof(string)), ("table_schema", typeof(string)),
                ("table_name", typeof(string)), ("column_name", typeof(string)), ("data_type", typeof(string))
            });
            PgWireTrace.Virtual("catalog.generic", normalized, result.Rows.Count);
            return true;
        }

        if (normalized.Contains("from pg_stats", StringComparison.Ordinal))
        {
            var allRows = pgStatsRows ?? Array.Empty<Dictionary<string, object?>>();
            var tablenameFilter = Regex.Match(normalized, @"tablename\s*=\s*'([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            IReadOnlyList<Dictionary<string, object?>> filteredRows = tablenameFilter.Success
                ? allRows.Where(r => string.Equals(r.TryGetValue("tablename", out var v) ? v?.ToString() : null, tablenameFilter.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).ToList()
                : allRows;

            var pgStatsFallbackColumns = new (string Name, Type ClrType)[]
            {
                ("schemaname", typeof(string)), ("tablename", typeof(string)), ("attname", typeof(string)),
                ("inherited", typeof(bool)), ("null_frac", typeof(float)), ("avg_width", typeof(int)),
                ("n_distinct", typeof(float)), ("most_common_vals", typeof(string)),
                ("most_common_freqs", typeof(string)), ("histogram_bounds", typeof(string)),
                ("correlation", typeof(float))
            };
            result = BuildProjectedVirtualResult(sql, filteredRows, pgStatsFallbackColumns);
            PgWireTrace.Virtual("pg_stats", normalized, result.Rows.Count);
            return true;
        }

        result = new PgVirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
        return false;
    }

    private static bool TryResolveScalarStartupQuery(string sql, string normalized, string? databaseName, out PgVirtualQueryResult result)
    {
        if (!normalized.StartsWith("select ", StringComparison.Ordinal) || normalized.Contains(" from ", StringComparison.Ordinal))
        {
            result = new PgVirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
            return false;
        }

        var body = sql.Trim();
        if (body.EndsWith(";", StringComparison.Ordinal))
            body = body[..^1].Trim();

        body = body.StartsWith("select", StringComparison.OrdinalIgnoreCase) ? body[6..].Trim() : body;

        var compactBody = Regex.Replace(body, "\\s+", string.Empty, RegexOptions.CultureInvariant)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (compactBody is "current_schema(),session_user" or "session_user,current_schema()"
            or "current_schema(),current_user" or "current_user,current_schema()")
        {
            result = new PgVirtualQueryResult(
                new[] { ("current_schema", typeof(string)), ("session_user", typeof(string)) },
                new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["current_schema"] = "public", ["session_user"] = "postgres" } });
            return true;
        }

        if (body.Contains(',', StringComparison.Ordinal))
        {
            result = new PgVirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
            return false;
        }

        string expression = body;
        string? alias = null;

        var asMatch = Regex.Match(body, @"^(?<expr>.+?)\s+as\s+(?<alias>""?[_a-zA-Z][_a-zA-Z0-9]*""?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (asMatch.Success)
        {
            expression = asMatch.Groups["expr"].Value.Trim();
            alias = asMatch.Groups["alias"].Value.Trim().Trim('"');
        }

        var normalizedExpr = expression.Trim().ToLowerInvariant();

        if (normalizedExpr is "1" or "1::int4" or "1::integer") { result = OneCell(alias ?? "?column?", typeof(int), 1); return true; }
        if (normalizedExpr is "current_database()" or "pg_catalog.current_database()") { result = OneCell(alias ?? "current_database", typeof(string), string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName); return true; }
        if (normalizedExpr is "current_schema()" or "pg_catalog.current_schema()") { result = OneCell(alias ?? "current_schema", typeof(string), "public"); return true; }
        if (normalizedExpr is "version()" or "pg_catalog.version()") { result = OneCell(alias ?? "version", typeof(string), "LayeredSql PgWire 16.0-layeredsql"); return true; }
        if (normalizedExpr is "current_user" or "session_user" or "user") { result = OneCell(alias ?? "current_user", typeof(string), "postgres"); return true; }

        var currentSettingMatch = Regex.Match(expression, @"^current_setting\('(?<name>[^']+)'(?:\s*,\s*(?:true|false))?\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (currentSettingMatch.Success)
        {
            var settingName = currentSettingMatch.Groups["name"].Value;
            var settingValue = settingName.ToLowerInvariant() switch
            {
                "server_version" => "16.0-layeredsql",
                "server_version_num" => "160000",
                "client_encoding" => "UTF8",
                "standard_conforming_strings" => "on",
                "integer_datetimes" => "on",
                "is_superuser" => "on",
                "datestyle" => "ISO, MDY",
                _ => ""
            };
            result = OneCell(alias ?? "current_setting", typeof(string), settingValue);
            return true;
        }

        var setConfigMatch = Regex.Match(expression, @"^(?:pg_catalog\.)?set_config\('(?<name>[^']*)'\s*,\s*'(?<value>[^']*)'\s*,\s*(?:true|false)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (setConfigMatch.Success)
        {
            result = OneCell(alias ?? "set_config", typeof(string), setConfigMatch.Groups["value"].Value);
            return true;
        }

        result = new PgVirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
        return false;
    }

    private static PgVirtualQueryResult OneCell(string columnName, Type columnType, object? value) =>
        new(
            new[] { (columnName, columnType) },
            new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [columnName] = value } });

    private static PgVirtualQueryResult BuildProjectedVirtualResult(
        string sql,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<(string Name, Type ClrType)> fallbackColumns)
    {
        var selectedColumns = ExtractSelectedColumns(sql);
        var columns = selectedColumns.Count == 0
            ? fallbackColumns.ToList()
            : selectedColumns
                .Select(column =>
                {
                    var fallback = fallbackColumns.FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
                    return fallback == default ? (column, typeof(string)) : fallback;
                })
                .ToList();

        // Aggregat COUNT(*) / COUNT(1) / COUNT(col) ohne GROUP BY muss immer eine Zeile liefern,
        // auch wenn die zugrundeliegende virtuelle Tabelle leer ist. PostgreSQL-Verhalten.
        if (selectedColumns.Count == 1 && IsCountAggregate(selectedColumns[0]) && !SqlContainsGroupBy(sql))
        {
            var countColumnName = string.Equals(selectedColumns[0], "count(*)", StringComparison.OrdinalIgnoreCase)
                ? "count"
                : selectedColumns[0];
            var aggregateRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [countColumnName] = (long)rows.Count
            };
            return new PgVirtualQueryResult(new[] { (countColumnName, typeof(long)) }, new[] { aggregateRow });
        }

        var projectedRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in columns)
                projected[name] = row.TryGetValue(name, out var value) ? value : null;
            projectedRows.Add(projected);
        }

        return new PgVirtualQueryResult(columns, projectedRows);
    }

    private static bool IsCountAggregate(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length < 6)
            return false;

        var paren = trimmed.IndexOf('(');
        if (paren < 0)
            return false;

        var name = trimmed[..paren].Trim();
        if (!string.Equals(name, "count", StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmed.EndsWith(")", StringComparison.Ordinal);
    }

    private static bool SqlContainsGroupBy(string sql)
    {
        var normalized = Regex.Replace(sql, "\\s+", " ").Trim().ToLowerInvariant();
        return Regex.IsMatch(normalized, @"\bgroup\s+by\b", RegexOptions.CultureInvariant);
    }

    private static string NormalizeSqlForCatalogDetection(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return string.Empty;

        var normalized = Regex.Replace(sql, "\\s+", " ").Trim().ToLowerInvariant();
        normalized = normalized.Replace("\"", string.Empty, StringComparison.Ordinal);
        return normalized;
    }

    // -----------------------------------------------------------------------------
    // Table discovery
    // -----------------------------------------------------------------------------

    private static IReadOnlyList<PgVirtualTableDefinition> DiscoverTableDefinitions(IPgWireBackendConnection backend, PgDbSessionState? session = null)
    {
        // Session-level cache: the table catalogue rarely changes within a connection lifetime.
        if (session?.CachedTableDefinitions is { } cached)
            return cached;

        try
        {
            var discovered = backend.DiscoverTables();
            if (discovered.Count > 0)
            {
                if (session != null)
                    session.CachedTableDefinitions = discovered;
                return discovered;
            }
        }
        catch
        {
            // Fall through to empty result
        }

        return Array.Empty<PgVirtualTableDefinition>();
    }


    private static IReadOnlyList<PgVirtualTableDefinition> DiscoverTableDefinitionsFromCatalog(object databaseHandle)
    {
        try
        {
            var collectionExistsMethod = databaseHandle.GetType().GetMethod("CollectionExists", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            var getCollectionMethod = databaseHandle.GetType().GetMethod("GetCollection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (collectionExistsMethod is null || getCollectionMethod is null)
                return Array.Empty<PgVirtualTableDefinition>();

            var exists = collectionExistsMethod.Invoke(databaseHandle, new object[] { "__sql_catalog" });
            if (exists is not bool b || !b)
                return Array.Empty<PgVirtualTableDefinition>();

            var catalogCollection = getCollectionMethod.Invoke(databaseHandle, new object[] { "__sql_catalog" });
            if (catalogCollection is not System.Collections.IEnumerable rows)
                return Array.Empty<PgVirtualTableDefinition>();

            var tables = new Dictionary<string, PgVirtualTableDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                try
                {
                    if (row is null) continue;

                    var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                    if (ident is null) continue;

                    var identType = ident.GetType();
                    var attribute = Convert.ToInt32(identType.GetProperty("Attribute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);
                    if (attribute != 0) continue;

                    var keyIdent = identType.GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
                    if (keyIdent is null) continue;

                    var keyIdentType = keyIdent.GetType();
                    var keyTypeName = keyIdentType.GetProperty("Type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyIdent)?.ToString();
                    if (!string.Equals(keyTypeName, "String", StringComparison.OrdinalIgnoreCase)) continue;

                    var key = keyIdentType.GetMethod("AsString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(keyIdent, null) as string;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var relationKind = key.StartsWith("view:", StringComparison.OrdinalIgnoreCase)
                        ? PgVirtualRelationKind.View
                        : key.StartsWith("table:", StringComparison.OrdinalIgnoreCase)
                            ? PgVirtualRelationKind.Table
                            : (PgVirtualRelationKind?)null;
                    if (relationKind is null) continue;

                    var data = row.GetType().GetProperty("Data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row) as byte[];
                    if (data is null || data.Length == 0) continue;

                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    if (relationKind == PgVirtualRelationKind.View)
                    {
                        var viewName = root.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString()
                            : key["view:".Length..];

                        if (string.IsNullOrWhiteSpace(viewName) || viewName.StartsWith("__", StringComparison.Ordinal))
                            continue;

                        tables[viewName] = new PgVirtualTableDefinition(viewName, Array.Empty<PgVirtualColumnDefinition>(), PgVirtualRelationKind.View);
                        continue;
                    }

                    if (root.TryGetProperty("CollectionName", out var collectionNameElement) && collectionNameElement.ValueKind == JsonValueKind.String)
                    {
                        var collectionName = collectionNameElement.GetString();
                        if (string.IsNullOrWhiteSpace(collectionName) || collectionName.StartsWith("__", StringComparison.Ordinal))
                            continue;

                        var columns = new List<PgVirtualColumnDefinition>();
                        if (root.TryGetProperty("Columns", out var columnsElement) && columnsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var col in columnsElement.EnumerateArray())
                            {
                                if (!col.TryGetProperty("Name", out var colNameElement) || colNameElement.ValueKind != JsonValueKind.String) continue;
                                var columnName = colNameElement.GetString();
                                if (string.IsNullOrWhiteSpace(columnName)) continue;

                                var dataType = "text";
                                if (col.TryGetProperty("Type", out var colTypeElement))
                                    dataType = MapCatalogColumnTypeToInformationSchema(colTypeElement);

                                var isNullable = true;
                                if (col.TryGetProperty("IsNullable", out var nullableElement)
                                    && (nullableElement.ValueKind == JsonValueKind.True || nullableElement.ValueKind == JsonValueKind.False))
                                {
                                    isNullable = nullableElement.GetBoolean();
                                }

                                var isPrimaryKey = false;
                                if (col.TryGetProperty("IsPrimaryKey", out var pkElement)
                                    && (pkElement.ValueKind == JsonValueKind.True || pkElement.ValueKind == JsonValueKind.False))
                                {
                                    isPrimaryKey = pkElement.GetBoolean();
                                }

                                columns.Add(new PgVirtualColumnDefinition(columnName, dataType, isNullable, isPrimaryKey));
                            }
                        }

                        tables[collectionName] = new PgVirtualTableDefinition(collectionName, columns, PgVirtualRelationKind.Table);
                    }
                }
                catch { continue; }
            }

            return tables.Values.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<PgVirtualTableDefinition>();
        }
    }

    private static IReadOnlyList<string> DiscoverCollectionNamesFromRuntimeRows(object databaseHandle)
    {
        if (databaseHandle is not System.Collections.IEnumerable rows)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            try
            {
                if (row is null) continue;
                var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                if (ident is null) continue;

                var identType = ident.GetType();
                var attribute = Convert.ToInt32(identType.GetProperty("Attribute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);
                var index = Convert.ToInt32(identType.GetProperty("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);
                if (attribute != 0 || index != 0) continue;

                var keyIdent = identType.GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
                if (keyIdent is null) continue;

                var key = TryReadKeyIdentString(keyIdent);
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (TryExtractTableNameFromMetadataKey(key, out var suffix))
                    names.Add(suffix);
            }
            catch { continue; }
        }

        return names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> DiscoverCollectionNamesFromMasterCollection(object databaseHandle)
    {
        try
        {
            var getMasterCollection = databaseHandle.GetType().GetMethod("GetMasterCollection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var masterCollection = getMasterCollection?.Invoke(databaseHandle, null);
            if (masterCollection is not System.Collections.IEnumerable rows)
                return Array.Empty<string>();

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                try
                {
                    if (row is null) continue;
                    var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                    if (ident is null) continue;

                    var keyIdent = ident.GetType().GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
                    if (keyIdent is null) continue;

                    var key = TryReadKeyIdentString(keyIdent);
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    if (TryExtractTableNameFromMetadataKey(key, out var suffix))
                        names.Add(suffix);
                }
                catch { continue; }
            }

            return names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryExtractTableNameFromMetadataKey(string key, out string tableName)
    {
        tableName = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var match = Regex.Match(key, @"^Database:\d+:Table:(?<name>[^:]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var value = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("__", StringComparison.Ordinal)
            || string.Equals(value, "LastCollectionId", StringComparison.OrdinalIgnoreCase))
            return false;

        tableName = value;
        return true;
    }

    private static string? TryReadKeyIdentString(object keyIdent)
    {
        try
        {
            var asString = keyIdent.GetType().GetMethod("AsString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(keyIdent, null) as string;
            if (!string.IsNullOrWhiteSpace(asString))
                return asString;

            var text = keyIdent.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var marker = "KY:";
            var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                var extracted = text[(markerIndex + marker.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, "Byte", StringComparison.OrdinalIgnoreCase))
                    return extracted;
            }

            return text;
        }
        catch { return null; }
    }

    private static string MapSqlScalarTypeToInformationSchema(int scalarType) =>
        scalarType switch
        {
            1 => "integer",
            2 => "bigint",
            3 => "double precision",
            4 => "numeric",
            5 => "text",
            6 => "boolean",
            7 => "timestamp with time zone",
            8 => "bytea",
            9 => "json",
            10 => "uuid",
            _ => "text"
        };

    private static string MapCatalogColumnTypeToInformationSchema(JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.Number && typeElement.TryGetInt32(out var typeOrdinal))
            return MapSqlScalarTypeToInformationSchema(typeOrdinal);

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var raw = typeElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return "text";

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                return MapSqlScalarTypeToInformationSchema(ordinal);

            return raw.ToUpperInvariant() switch
            {
                "INT32" or "INTEGER" or "INT" => "integer",
                "INT64" or "BIGINT" or "LONG" => "bigint",
                "DOUBLE" or "REAL" or "FLOAT" => "double precision",
                "DECIMAL" or "NUMERIC" => "numeric",
                "BOOLEAN" or "BOOL" or "BIT" => "boolean",
                "DATETIME" or "TIMESTAMP" or "DATE" => "timestamp with time zone",
                "BINARY" or "VARBINARY" or "BYTEA" or "BLOB" => "bytea",
                "JSON" or "JSONB" => "json",
                "GUID" or "UUID" => "uuid",
                _ => "text"
            };
        }

        return "text";
    }

    private static Dictionary<string, object?> BuildPgTypeRow(int oid, string typname, short typlen, bool typbyval, string typcategory, string typtype, int typelem, int typcollation = 0) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["nspname"] = "pg_catalog",
            ["oid"] = oid, ["typname"] = typname, ["typnamespace"] = 11, ["typowner"] = 10,
            // elemtypoid: NULL for base types (Npgsql handles len==-1 ? 0);
            // for types with elements set typelem so ExtractSelectedColumns finds a value.
            ["elemtypoid"] = typelem == 0 ? (object?)null : typelem,
            ["typlen"] = typlen, ["typbyval"] = typbyval, ["typtype"] = typtype, ["typcategory"] = typcategory,
            ["typispreferred"] = true, ["typisdefined"] = true, ["typdelim"] = ",",
            ["typrelid"] = 0, ["typelem"] = typelem, ["typarray"] = 0,
            ["typinput"] = typname + "in", ["typoutput"] = typname + "out",
            ["typreceive"] = typname + "recv", ["typsend"] = typname + "send",
            ["typmodin"] = "-", ["typmodout"] = "-", ["typanalyze"] = "-",
            ["typalign"] = typlen switch { 8 => "d", 4 => "i", 2 => "s", _ => "i" },
            ["typstorage"] = typlen < 0 ? "x" : "p",
            ["typnotnull"] = false, ["typbasetype"] = 0, ["typtypmod"] = -1,
            ["typndims"] = 0, ["typcollation"] = typcollation, ["typdefaultbin"] = null,
            ["typdefault"] = null, ["typacl"] = null, ["base_type_name"] = typname,
            ["description"] = null, ["relkind"] = null
        };

    private static int MapInformationSchemaTypeToPgTypeOid(string? dataType)
    {
        var normalized = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "smallint" => 21,
            "integer" or "int" => 23,
            "bigint" => 20,
            "decimal" or "numeric" => 1700,
            "double precision" => 701,
            "real" => 700,
            "boolean" => 16,
            "bytea" => 17,
            "timestamp with time zone" => 1184,
            "timestamp" => 1114,
            _ => 1043
        };
    }

    private static short MapPgTypeSizeFromOid(int pgTypeOid) =>
        pgTypeOid switch { 16 => 1, 21 => 2, 23 or 26 or 700 => 4, 20 or 701 => 8, _ => -1 };

    private static int StableOid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 20000;

        unchecked
        {
            var hash = 17;
            foreach (var c in value)
                hash = (hash * 31) + c;

            if (hash == int.MinValue) hash = int.MaxValue;
            return 20000 + Math.Abs(hash % 500000);
        }
    }

    private static int GetCollationOid(string? collation) => collation switch
    {
        null or "" or "C" => 100,
        "de-DE-x-icu" => 950,
        "en-US-x-icu" => 951,
        "tr-TR-x-icu" => 952,
        _ => 100
    };

    // -----------------------------------------------------------------------------
    // Wire: Send helpers
    // -----------------------------------------------------------------------------

    private static async Task SendVirtualQueryResultAsync(Stream stream, PgVirtualQueryResult result)
    {
        await SendRowDescriptionTextAsync(stream, result.Fields);
        foreach (var row in result.Rows)
        {
            var values = result.Fields
                .Select(field => row.TryGetValue(field.Name, out var value) && value != null ? (string?)ToPgText(value) : null)
                .ToArray();
            await SendDataRowAsync(stream, values);
        }
        await SendCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}");
    }

    private static async Task SendVirtualExecuteResultAsync(
        Stream stream,
        PgVirtualQueryResult result,
        bool includeRowDescription,
        IReadOnlyList<short>? resultFormatCodes = null)
    {
        if (includeRowDescription)
            await SendRowDescriptionAsync(stream, result.Fields, resultFormatCodes);
        foreach (var row in result.Rows)
        {
            var rawValues = result.Fields
                .Select(field => row.TryGetValue(field.Name, out var value) ? value : null)
                .ToArray();
            await SendDataRowAsync(stream, rawValues, result.Fields, resultFormatCodes);
        }
        await SendCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}");
    }

    private static async Task SendAuthenticationOkAsync(Stream stream)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, 0);
        await WriteMessageAsync(stream, (byte)'R', body);
    }

    private static async Task SendAuthenticationSaslAsync(Stream stream, string[] mechanisms)
    {
        using var ms = new MemoryStream();
        // Reserve 4 bytes for AuthType
        ms.Write(new byte[4]);
        foreach (var mech in mechanisms)
        {
            var bytes = Encoding.UTF8.GetBytes(mech);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
        ms.WriteByte(0); // empty string terminates the list
        var body = ms.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), 10); // AuthType SASL
        await WriteMessageAsync(stream, (byte)'R', body);
    }

    private static async Task SendAuthenticationSaslContinueAsync(Stream stream, string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), 11); // AuthType SASLContinue
        bytes.CopyTo(body.AsSpan(4));
        await WriteMessageAsync(stream, (byte)'R', body);
    }

    private static async Task SendAuthenticationSaslFinalAsync(Stream stream, string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), 12); // AuthType SASLFinal
        bytes.CopyTo(body.AsSpan(4));
        await WriteMessageAsync(stream, (byte)'R', body);
    }

    private static async Task<(string? Mechanism, string? InitialResponse)> ReadSaslInitialResponseAsync(Stream stream)
    {
        var messageType = await ReadByteAsync(stream);
        if (messageType == null)
            return (null, null);

        var type = (char)messageType.Value;
        if (type != 'p')
            throw new InvalidOperationException($"Expected SASL message (type 'p'), got '{type}'.");

        var length = await ReadInt32Async(stream);
        if (length < 4)
            throw new InvalidOperationException("Invalid SASL message length.");

        var payload = await ReadExactlyAsync(stream, length - 4);
        var reader = new PgPayloadReader(payload);
        var mechanism = reader.ReadCString();
        var initialLen = reader.ReadInt32();
        if (initialLen < 0)
            return (mechanism, string.Empty);

        var initialBytes = reader.ReadBytes(initialLen);
        return (mechanism, Encoding.UTF8.GetString(initialBytes));
    }

    private static async Task<string?> ReadSaslResponseAsync(Stream stream)
    {
        var messageType = await ReadByteAsync(stream);
        if (messageType == null)
            return null;

        var type = (char)messageType.Value;
        if (type != 'p')
            throw new InvalidOperationException($"Expected SASL message (type 'p'), got '{type}'.");

        var length = await ReadInt32Async(stream);
        if (length < 4)
            throw new InvalidOperationException("Invalid SASL message length.");

        var payload = await ReadExactlyAsync(stream, length - 4);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task SendParameterStatusAsync(Stream stream, string key, string value)
        => await WriteMessageAsync(stream, (byte)'S', BuildCString(key, value));

    private static async Task SendBackendKeyDataAsync(Stream stream)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), Environment.ProcessId);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
        await WriteMessageAsync(stream, (byte)'K', payload);
    }

    private static Task SendReadyForQueryAsync(Stream stream, byte status)
        => WriteMessageAsync(stream, (byte)'Z', new[] { status });

    // ── COPY protocol messages ─────────────────────────────────────────────────

    private static async Task SendCopyInResponseAsync(Stream stream, WalhallaSql.Sql.SqlCopyStatement copyStmt, IPgWireBackendConnection backend)
    {
        using var ms = new MemoryStream();
        // Overall format: 0 = text, 1 = binary
        ms.WriteByte((byte)(copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary ? 1 : 0));

        var tables = backend.DiscoverTables();
        var tableDef = tables.FirstOrDefault(t => string.Equals(t.Name, copyStmt.TableName, StringComparison.OrdinalIgnoreCase));
        var columnCount = tableDef?.Columns.Count ?? 0;
        if (copyStmt.ColumnNames != null && copyStmt.ColumnNames.Count > 0)
            columnCount = copyStmt.ColumnNames.Count;

        WriteInt16(ms, (short)columnCount);
        for (int i = 0; i < columnCount; i++)
            WriteInt16(ms, (short)(copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary ? 1 : 0));

        await WriteMessageAsync(stream, (byte)'G', ms.ToArray());
    }

    private static async Task SendCopyOutResponseAsync(Stream stream, WalhallaSql.Sql.SqlCopyStatement copyStmt, IPgWireBackendConnection backend)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary ? 1 : 0));

        var tables = backend.DiscoverTables();
        var tableDef = tables.FirstOrDefault(t => string.Equals(t.Name, copyStmt.TableName, StringComparison.OrdinalIgnoreCase));
        var columnCount = tableDef?.Columns.Count ?? 0;
        if (copyStmt.ColumnNames != null && copyStmt.ColumnNames.Count > 0)
            columnCount = copyStmt.ColumnNames.Count;

        WriteInt16(ms, (short)columnCount);
        for (int i = 0; i < columnCount; i++)
            WriteInt16(ms, (short)(copyStmt.Options.Format == WalhallaSql.Sql.SqlCopyFormat.Binary ? 1 : 0));

        await WriteMessageAsync(stream, (byte)'H', ms.ToArray());
    }

    private static Task SendCopyDataAsync(Stream stream, byte[] data)
        => WriteMessageAsync(stream, (byte)'d', data);

    private static Task SendCopyDoneAsync(Stream stream)
        => WriteMessageAsync(stream, (byte)'c', Array.Empty<byte>());

    private static string ResolveSqlState(Exception ex, string fallback = "XX000")
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is WalhallaSql.WalhallaException we && !string.IsNullOrEmpty(we.SqlState))
                return we.SqlState!;
        }
        return fallback;
    }

    private static async Task SendErrorAsync(Stream stream, string code, string message)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'S'); WriteCString(ms, "ERROR");
        ms.WriteByte((byte)'V'); WriteCString(ms, "ERROR");
        ms.WriteByte((byte)'C'); WriteCString(ms, code);
        ms.WriteByte((byte)'M'); WriteCString(ms, message);
        ms.WriteByte(0);
        await WriteMessageAsync(stream, (byte)'E', ms.ToArray());
    }

    private static Task SendParseCompleteAsync(Stream stream)  => WriteMessageAsync(stream, (byte)'1', Array.Empty<byte>());
    private static Task SendBindCompleteAsync(Stream stream)   => WriteMessageAsync(stream, (byte)'2', Array.Empty<byte>());
    private static Task SendCloseCompleteAsync(Stream stream)  => WriteMessageAsync(stream, (byte)'3', Array.Empty<byte>());
    private static Task SendNoDataAsync(Stream stream)         => WriteMessageAsync(stream, (byte)'n', Array.Empty<byte>());

    private static async Task SendParameterDescriptionAsync(Stream stream, IReadOnlyList<int> parameterTypeOids)
    {
        using var ms = new MemoryStream();
        WriteInt16(ms, (short)parameterTypeOids.Count);
        foreach (var oid in parameterTypeOids)
            WriteInt32(ms, oid);
        await WriteMessageAsync(stream, (byte)'t', ms.ToArray());
    }

    private static async Task SendRowDescriptionAsync(
        Stream stream,
        IReadOnlyList<(string Name, Type ClrType)> fields,
        IReadOnlyList<short>? resultFormatCodes = null)
    {
        using var ms = new MemoryStream();
        WriteInt16(ms, (short)fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var oid = MapPgTypeOid(field.ClrType);
            var formatCode = ResolveResultFormatCode(resultFormatCodes, i, field.ClrType);
            WriteCString(ms, field.Name);
            WriteInt32(ms, 0);
            WriteInt16(ms, 0);
            WriteInt32(ms, oid);
            WriteInt16(ms, MapPgTypeSize(field.ClrType));
            WriteInt32(ms, -1);
            WriteInt16(ms, formatCode);
        }
        await WriteMessageAsync(stream, (byte)'T', ms.ToArray());
    }

    // Text-only row description (all format codes = 0) � used for Simple Query virtuals (pg_type bootstrap, etc.)
    private static async Task SendRowDescriptionTextAsync(Stream stream, IReadOnlyList<(string Name, Type ClrType)> fields)
    {
        using var ms = new MemoryStream();
        WriteInt16(ms, (short)fields.Count);
        foreach (var field in fields)
        {
            var oid = MapPgTypeOid(field.ClrType);
            WriteCString(ms, field.Name);
            WriteInt32(ms, 0);
            WriteInt16(ms, 0);
            WriteInt32(ms, oid);
            WriteInt16(ms, MapPgTypeSize(field.ClrType));
            WriteInt32(ms, -1);
            WriteInt16(ms, 0); // always text for virtual/bootstrap queries
        }
        await WriteMessageAsync(stream, (byte)'T', ms.ToArray());
    }

    private static async Task SendDataRowAsync(Stream stream, IReadOnlyList<string?> values)
    {
        using var ms = new MemoryStream();
        WriteInt16(ms, (short)values.Count);
        foreach (var value in values)
        {
            if (value == null) { WriteInt32(ms, -1); continue; }
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(ms, bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        await WriteMessageAsync(stream, (byte)'D', ms.ToArray());
    }

    private static async Task SendDataRowAsync(
        Stream stream,
        IReadOnlyList<object?> rawValues,
        IReadOnlyList<(string Name, Type ClrType)> fields,
        IReadOnlyList<short>? resultFormatCodes = null)
    {
        using var ms = new MemoryStream();
        WriteInt16(ms, (short)rawValues.Count);
        for (var i = 0; i < rawValues.Count; i++)
        {
            var value = rawValues[i];
            var clrType = i < fields.Count ? fields[i].ClrType : typeof(string);
            var formatCode = ResolveResultFormatCode(resultFormatCodes, i, clrType);
            if (value == null) { WriteInt32(ms, -1); continue; }

            if (formatCode == 1)
            {
                var binBytes = ToBinaryBytes(value, clrType);
                if (binBytes != null)
                {
                    WriteInt32(ms, binBytes.Length);
                    ms.Write(binBytes, 0, binBytes.Length);
                    continue;
                }
            }
            var textBytes = Encoding.UTF8.GetBytes(ToPgText(value));
            WriteInt32(ms, textBytes.Length);
            ms.Write(textBytes, 0, textBytes.Length);
        }
        await WriteMessageAsync(stream, (byte)'D', ms.ToArray());
    }

    private static short ResolveResultFormatCode(IReadOnlyList<short>? resultFormatCodes, int index, Type? clrType)
    {
        var requestedFormat = resultFormatCodes is { Count: > 0 }
            ? ResolveFormatCode(resultFormatCodes.ToArray(), index)
            : (short)0;

        return requestedFormat == 1 && CanBinaryEncodeType(clrType) ? (short)1 : (short)0;
    }

    private static bool CanBinaryEncodeType(Type? clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType ?? typeof(string)) ?? clrType ?? typeof(string);
        return t == typeof(short) || t == typeof(int) || t == typeof(long)
            || t == typeof(bool) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
    }

    private static byte[]? ToBinaryBytes(object value, Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(int))
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, Convert.ToInt32(value));
            return b;
        }
        if (t == typeof(long))
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, Convert.ToInt64(value));
            return b;
        }
        if (t == typeof(short))
        {
            var b = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, Convert.ToInt16(value));
            return b;
        }
        if (t == typeof(bool))
            return new byte[] { Convert.ToBoolean(value) ? (byte)1 : (byte)0 };
        if (t == typeof(float))
        {
            var b = new byte[4];
            BinaryPrimitives.WriteSingleBigEndian(b, Convert.ToSingle(value));
            return b;
        }
        if (t == typeof(double))
        {
            var b = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, Convert.ToDouble(value));
            return b;
        }
        if (t == typeof(decimal))
            return ToNumericBinaryBytes(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        return null;
    }

    private static byte[] ToNumericBinaryBytes(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0x7F;
        var sign = (bits[3] & unchecked((int)0x80000000)) != 0 ? (short)0x4000 : (short)0x0000;
        var absoluteValue = decimal.Abs(value);
        var text = absoluteValue.ToString($"F{scale}", CultureInfo.InvariantCulture);
        var parts = text.Split('.');
        var integerPart = parts[0].TrimStart('0');
        var fractionalPart = parts.Length > 1 ? parts[1] : string.Empty;

        var digitGroups = new List<short>();
        var integerGroupCount = 0;

        if (integerPart.Length > 0)
        {
            var firstGroupLength = integerPart.Length % 4;
            if (firstGroupLength == 0)
                firstGroupLength = 4;

            digitGroups.Add(short.Parse(integerPart[..firstGroupLength], CultureInfo.InvariantCulture));
            integerGroupCount++;

            for (var offset = firstGroupLength; offset < integerPart.Length; offset += 4)
            {
                digitGroups.Add(short.Parse(integerPart.Substring(offset, 4), CultureInfo.InvariantCulture));
                integerGroupCount++;
            }
        }

        if (fractionalPart.Length > 0)
        {
            var paddedFraction = fractionalPart.PadRight(((fractionalPart.Length + 3) / 4) * 4, '0');
            for (var offset = 0; offset < paddedFraction.Length; offset += 4)
                digitGroups.Add(short.Parse(paddedFraction.Substring(offset, 4), CultureInfo.InvariantCulture));
        }

        var weight = integerGroupCount - 1;
        var firstNonZero = 0;
        while (firstNonZero < digitGroups.Count && digitGroups[firstNonZero] == 0)
        {
            firstNonZero++;
            weight--;
        }

        var lastNonZero = digitGroups.Count - 1;
        while (lastNonZero >= firstNonZero && digitGroups[lastNonZero] == 0)
            lastNonZero--;

        using var ms = new MemoryStream();
        if (lastNonZero < firstNonZero)
        {
            WriteInt16(ms, 0);
            WriteInt16(ms, 0);
            WriteInt16(ms, 0);
            WriteInt16(ms, (short)scale);
            return ms.ToArray();
        }

        var digits = digitGroups.GetRange(firstNonZero, lastNonZero - firstNonZero + 1);
        WriteInt16(ms, checked((short)digits.Count));
        WriteInt16(ms, checked((short)weight));
        WriteInt16(ms, sign);
        WriteInt16(ms, checked((short)scale));
        foreach (var digit in digits)
            WriteInt16(ms, digit);

        return ms.ToArray();
    }

    private static async Task SendCommandCompleteAsync(Stream stream, string tag)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, tag);
        await WriteMessageAsync(stream, (byte)'C', ms.ToArray());
    }

    private static int MapPgTypeOid(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(short)) return 21;
        if (type == typeof(int)) return 23;
        if (type == typeof(long)) return 20;
        if (type == typeof(float)) return 700;
        if (type == typeof(double)) return 701;
        if (type == typeof(decimal)) return 1700;
        if (type == typeof(bool)) return 16;
        if (type == typeof(byte[])) return 17;
        if (type == typeof(DateTime)) return 1184;
        if (type == typeof(Guid)) return 2950;
        return 25;
    }

    private static short MapPgTypeSize(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(short)) return 2;
        if (type == typeof(int) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(double)) return 8;
        if (type == typeof(bool)) return 1;
        if (type == typeof(Guid)) return 16;
        return -1;
    }

    // -----------------------------------------------------------------------------
    // Wire: Low-level read / write
    // -----------------------------------------------------------------------------

    private static async Task WriteMessageAsync(Stream stream, byte messageType, byte[] payload)
    {
        var header = new byte[5];
        header[0] = messageType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length + 4);

        PgWireTrace.Backend((char)messageType, payload.Length + 4, payload.Length);

        await stream.WriteAsync(header);
        if (payload.Length > 0)
            await stream.WriteAsync(payload);

        await stream.FlushAsync();
    }

    private static byte[] BuildCString(string key, string value)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, key);
        WriteCString(ms, value);
        return ms.ToArray();
    }

    private static void WriteCString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static async Task<byte[]?> ReadExactlyOrNullAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (read == 0) return null;
            offset += read;
        }
        return buffer;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var buffer = await ReadExactlyOrNullAsync(stream, length);
        if (buffer == null)
            throw new InvalidOperationException("Unexpected end of stream.");
        return buffer;
    }

    private static async Task<int?> ReadByteAsync(Stream stream)
    {
        var one = await ReadExactlyOrNullAsync(stream, 1);
        return one == null ? null : one[0];
    }

    private static async Task<int> ReadInt32Async(Stream stream)
    {
        var bytes = await ReadExactlyAsync(stream, 4);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    // -------------------------------------------------------------------------
    // WebSocket transport: HTTP upgrade handshake
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads HTTP request headers from <paramref name="stream"/> and performs the
    /// RFC 6455 WebSocket upgrade handshake.  Returns a ready-to-use
    /// <see cref="WebSocket"/> on success, or <c>null</c> when the request is not
    /// a valid WebSocket upgrade (connection is then closed by the caller).
    /// </summary>
    private static async Task<WebSocket?> TryUpgradeToWebSocketAsync(
        Stream stream, CancellationToken ct)
    {
        var headers = await ReadHttpRequestHeadersAsync(stream, ct).ConfigureAwait(false);
        if (headers == null) return null;

        string? wsKey     = null;
        bool    isUpgrade = false;

        foreach (var line in headers.Split('\n').Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name  = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                && value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                isUpgrade = true;

            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                wsKey = value;
        }

        if (!isUpgrade || wsKey == null) return null;

        // RFC 6455 §4.2.2: Sec-WebSocket-Accept = Base64(SHA-1(key + magic))
        var acceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Protocol: pgwire\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
            "\r\n");
        await stream.WriteAsync(response, ct).ConfigureAwait(false);

        return WebSocket.CreateFromStream(stream, isServer: true,
            subProtocol: "pgwire", keepAliveInterval: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Reads HTTP request headers byte-by-byte until the <c>\r\n\r\n</c> terminator.
    /// Stops exactly at the header boundary without consuming any body bytes.
    /// </summary>
    private static async Task<string?> ReadHttpRequestHeadersAsync(
        Stream stream, CancellationToken ct)
    {
        var sb     = new StringBuilder(512);
        var buf    = new byte[1];
        int crlfN  = 0;
        bool prevCr = false;

        while (await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false) == 1)
        {
            char c = (char)buf[0];
            sb.Append(c);
            if (c == '\r') { prevCr = true; continue; }
            if (c == '\n' && prevCr) { if (++crlfN == 2) return sb.ToString(); }
            else crlfN = 0;
            prevCr = false;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // WebSocket transport: stream wrapper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adapts a <see cref="WebSocket"/> as a bidirectional <see cref="Stream"/>.
    /// Each <see cref="WriteAsync"/> call sends one binary WebSocket frame.
    /// <see cref="ReadAsync"/> buffers received frames so partial reads work
    /// correctly with the PG wire protocol’s fixed-header framing.
    /// </summary>
    private sealed class WebSocketStream : Stream
    {
        private readonly WebSocket _ws;
        private byte[] _recvBuf   = new byte[65536];
        private int    _recvStart;
        private int    _recvEnd;

        internal WebSocketStream(WebSocket ws)
            => _ws = ws ?? throw new ArgumentNullException(nameof(ws));

        public override bool CanRead  => true;
        public override bool CanWrite => true;
        public override bool CanSeek  => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            // Return buffered data from a previously received frame first.
            if (_recvStart < _recvEnd)
            {
                int avail = Math.Min(count, _recvEnd - _recvStart);
                Buffer.BlockCopy(_recvBuf, _recvStart, buffer, offset, avail);
                _recvStart += avail;
                return avail;
            }

            // Receive the next WebSocket message (may span multiple continuation frames).
            _recvStart = 0;
            _recvEnd   = 0;
            WebSocketReceiveResult result;
            do
            {
                if (_recvEnd >= _recvBuf.Length)
                    Array.Resize(ref _recvBuf, _recvBuf.Length * 2);
                result = await _ws.ReceiveAsync(
                    new ArraySegment<byte>(_recvBuf, _recvEnd, _recvBuf.Length - _recvEnd), ct)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return 0;
                _recvEnd += result.Count;
            }
            while (!result.EndOfMessage);

            int toCopy = Math.Min(count, _recvEnd);
            Buffer.BlockCopy(_recvBuf, 0, buffer, offset, toCopy);
            _recvStart = toCopy;
            return toCopy;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _ws.SendAsync(new ArraySegment<byte>(buffer, offset, count),
                WebSocketMessageType.Binary, endOfMessage: true, ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => _ws.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _ws.Dispose();
            base.Dispose(disposing);
        }
    }
}

// -----------------------------------------------------------------------------
// Internal session / protocol types
// -----------------------------------------------------------------------------

internal enum PgAuthState
{
    None,
    SASL,
    SASLContinue,
    Authenticated,
    Failed
}

internal sealed class PgDbSessionState
{
    public IPgWireBackendTransaction? Transaction { get; set; }
    public bool IgnoreUntilSync { get; set; }
    /// <summary>Cached result of DiscoverTableDefinitions; null = stale/not-yet-computed.</summary>
    public IReadOnlyList<PgVirtualTableDefinition>? CachedTableDefinitions { get; set; }
    public void InvalidateTableDefinitionCache() => CachedTableDefinitions = null;
    public Dictionary<string, PgPreparedStatement> PreparedStatements { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PgBoundPortal> Portals { get; } = new(StringComparer.Ordinal);

    // Auth state
    public string? UserName { get; set; }
    public PgAuthState AuthState { get; set; }
    public WalhallaSql.PgWire.Auth.ScramSha256Server? ScramServer { get; set; }

    // COPY protocol state
    public PgCopyState? CopyState { get; set; }
}

internal sealed class PgCopyState
{
    public required WalhallaSql.Sql.SqlCopyStatement Statement { get; set; }
    public required WalhallaSql.Sql.SqlCopyFormat Format { get; set; }
    public System.Text.StringBuilder TextBuffer { get; } = new();
    public List<byte[]> BinaryRows { get; } = new();
}

internal sealed class PgPreparedStatement
{
    public PgPreparedStatement(string sql, IReadOnlyList<int> parameterTypeOids)
    {
        Sql = sql;
        ParameterTypeOids = parameterTypeOids;
    }

    public string Sql { get; }
    public IReadOnlyList<int> ParameterTypeOids { get; }
    public bool MetadataDescribed { get; set; }
    public IReadOnlyList<(string Name, Type ClrType)>? DescribedFields { get; set; }

    /// <summary>Reusable engine-side prepared statement. Null until first successful Bind compiles it.</summary>
    public WalhallaPreparedStatement? Compiled { get; set; }

    /// <summary>SQL text with <c>@p0</c>-style placeholders used for the compiled statement.</summary>
    public string? ParameterizedSql { get; set; }
}

internal sealed class PgBoundPortal
{
    public PgBoundPortal(string sql, bool isQuery, bool metadataDescribed, PgPreparedStatement? sourceStatement = null)
    {
        Sql = sql;
        IsQuery = isQuery;
        MetadataDescribed = metadataDescribed;
        SourceStatement = sourceStatement;
    }

    public string Sql { get; }
    public bool IsQuery { get; }
    public bool MetadataDescribed { get; set; }
    public PgPreparedStatement? SourceStatement { get; }
    public IReadOnlyList<(string Name, Type ClrType)>? DescribedFields { get; set; }
    public IReadOnlyList<short> ResultFormatCodes { get; set; } = Array.Empty<short>();

    /// <summary>SQL text with <c>@p0</c>-style placeholders; used to compile the reusable statement.</summary>
    public string? ParameterizedSql { get; set; }

    /// <summary>Decoded parameter values for the current bind, indexed 0..N-1.</summary>
    public object?[]? ParameterValues { get; set; }

    /// <summary>Engine-side prepared statement reused across Execute calls for this portal.</summary>
    public WalhallaPreparedStatement? PreparedStatement { get; set; }
}

internal sealed record PgVirtualQueryResult(
    IReadOnlyList<(string Name, Type ClrType)> Fields,
    IReadOnlyList<Dictionary<string, object?>> Rows);

