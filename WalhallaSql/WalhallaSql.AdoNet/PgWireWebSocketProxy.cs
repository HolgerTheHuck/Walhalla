using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace WalhallaSql.AdoNet;

/// <summary>
/// Client-side TCP -> WebSocket bridge for a PgWire server running in WebSocket mode.
/// </summary>
public sealed class PgWireWebSocketProxy : IAsyncDisposable
{
    private readonly Uri _remoteUri;
    private Socket? _serverSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _bridgeTasks = new();

    public PgWireWebSocketProxy(string remoteUri)
    {
        _remoteUri = new Uri(remoteUri ?? throw new ArgumentNullException(nameof(remoteUri)));
    }

    /// <summary>
    /// Local TCP port Npgsql or WalhallaSqlDbConnection should connect to.
    /// Available after <see cref="StartAsync(int, CancellationToken)"/>.
    /// </summary>
    public int BoundPort { get; private set; }

    /// <param name="localPort">0 = let the OS assign an ephemeral port (default).</param>
    public Task StartAsync(int localPort = 0, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, localPort));
        _serverSocket.Listen(32);
        BoundPort = ((IPEndPoint)_serverSocket.LocalEndPoint!).Port;
        _acceptLoop = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _serverSocket?.Close(); } catch { }
        if (_acceptLoop != null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        if (_bridgeTasks.Count > 0)
            try { await Task.WhenAll(_bridgeTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { }
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket clientSocket;
            try
            {
                clientSocket = await _serverSocket!.AcceptAsync(ct).ConfigureAwait(false);
                clientSocket.NoDelay = true;
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            _bridgeTasks.Add(Task.Run(() => BridgeAsync(clientSocket, ct), CancellationToken.None));
        }
    }

    private async Task BridgeAsync(Socket clientSocket, CancellationToken ct)
    {
        using var localStream = new NetworkStream(clientSocket, ownsSocket: true);
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("pgwire");

        try
        {
            await ws.ConnectAsync(_remoteUri, ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        try
        {
            await Task.WhenAny(
                PumpTcpToWsAsync(localStream, ws, ct),
                PumpWsToTcpAsync(ws, localStream, ct)
            ).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static async Task PumpTcpToWsAsync(Stream tcp, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            int read = await tcp.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    private static async Task PumpWsToTcpAsync(WebSocket ws, Stream tcp, CancellationToken ct)
    {
        var buffer = new byte[32768];
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.Count > 0)
                await tcp.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
        }
    }
}
