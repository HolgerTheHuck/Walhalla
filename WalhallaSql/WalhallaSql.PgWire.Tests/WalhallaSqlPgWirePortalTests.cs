using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WalhallaSql;
using WalhallaSql.PgWire;
using Xunit;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Tests the PgWire extended-query protocol directly at the message layer,
/// focusing on portal/cursor semantics (fetch-count, PortalSuspended, resume).
/// </summary>
public class WalhallaSqlPgWirePortalTests
{
    [Fact]
    public async Task Portal_FetchCount_SuspendsAndResumes()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSqlPgWirePortalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        using var engine = new WalhallaEngine(new WalhallaOptions(tempPath));
        engine.AuthIdCatalog.CreateRole("test", "test", canLogin: true, isSuperuser: true);

        engine.Execute("CREATE TABLE Numbers (Id INT PRIMARY KEY)");
        for (int i = 1; i <= 10; i++)
            engine.Execute($"INSERT INTO Numbers (Id) VALUES ({i})");

        var backend = new WalhallaSqlPgWireBackend(engine);
        var server = new PgWireServer(backend, host: "127.0.0.1", port: 0);
        await server.StartAsync();

        try
        {
            using var tcp = new TcpClient("127.0.0.1", server.BoundPort);
            await using var stream = tcp.GetStream();

            // Startup without user: unknown users skip authentication and go
            // straight to AuthenticationOk (auth-type 0). This avoids implementing
            // SCRAM-SHA-256 in this low-level message test.
            await SendStartupMessageAsync(stream);
            await SkipToReadyForQueryAsync(stream);

            // Parse: SELECT * FROM Numbers ORDER BY Id
            await SendParseAsync(stream, "S_numbers", "SELECT * FROM Numbers ORDER BY Id");
            await ExpectMessageAsync(stream, '1'); // ParseComplete

            // Bind portal with empty name = default portal
            await SendBindAsync(stream, "", "S_numbers", Array.Empty<object?>());
            await ExpectMessageAsync(stream, '2'); // BindComplete

            // Execute with fetch count 3
            await SendExecuteAsync(stream, "", maxRows: 3);

            var (rowCount1, suspended1) = await CountRowsUntilTerminalAsync(stream);
            Assert.Equal(3, rowCount1);
            Assert.True(suspended1, "Expected PortalSuspended after first fetch.");

            // Resume portal, fetch another 3 rows
            await SendExecuteAsync(stream, "", maxRows: 3);
            var (rowCount2, suspended2) = await CountRowsUntilTerminalAsync(stream);
            Assert.Equal(3, rowCount2);
            Assert.True(suspended2, "Expected PortalSuspended after second fetch.");

            // Resume and fetch the remaining 4 rows (no suspend this time)
            await SendExecuteAsync(stream, "", maxRows: 0); // 0 = unlimited/finish
            var (rowCount3, suspended3) = await CountRowsUntilTerminalAsync(stream);
            Assert.Equal(4, rowCount3);
            Assert.False(suspended3, "Expected CommandComplete, not PortalSuspended.");

            // Close portal
            await SendCloseAsync(stream, 'P', "");
            await ExpectMessageAsync(stream, '3'); // CloseComplete

            // Sync / ReadyForQuery
            await SendSyncAsync(stream);
            await ExpectMessageAsync(stream, 'Z');
        }
        finally
        {
            await server.DisposeAsync();
            engine.Dispose();
            try { Directory.Delete(tempPath, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Portal_LargeTable_FetchAllRows()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSqlPgWirePortalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        using var engine = new WalhallaEngine(new WalhallaOptions(tempPath));
        engine.AuthIdCatalog.CreateRole("test", "test", canLogin: true, isSuperuser: true);

        engine.Execute("CREATE TABLE Numbers (Id INT PRIMARY KEY, Name STRING)");
        const int rowCount = 5_000;
        const int chunkSize = 1_000;
        for (int chunk = 0; chunk < rowCount / chunkSize; chunk++)
        {
            var batch = new System.Collections.Generic.List<string>(chunkSize);
            int start = chunk * chunkSize;
            int end = Math.Min(start + chunkSize, rowCount);
            for (int i = start; i < end; i++)
                batch.Add($"({i}, 'Row{i}')");
            engine.Execute($"INSERT INTO Numbers (Id, Name) VALUES {string.Join(", ", batch)}");
        }

        var backend = new WalhallaSqlPgWireBackend(engine);
        var server = new PgWireServer(backend, host: "127.0.0.1", port: 0);
        await server.StartAsync();

        try
        {
            using var tcp = new TcpClient("127.0.0.1", server.BoundPort);
            await using var stream = tcp.GetStream();

            await SendStartupMessageAsync(stream);
            await SkipToReadyForQueryAsync(stream);

            await SendParseAsync(stream, "S_numbers", "SELECT * FROM Numbers ORDER BY Id");
            await ExpectMessageAsync(stream, '1'); // ParseComplete

            await SendBindAsync(stream, "", "S_numbers", Array.Empty<object?>());
            await ExpectMessageAsync(stream, '2'); // BindComplete

            int totalRows = 0;
            bool suspended;
            const int fetchSize = 1_000;
            do
            {
                await SendExecuteAsync(stream, "", maxRows: fetchSize);
                var (rows, isSuspended) = await CountRowsUntilTerminalAsync(stream);
                totalRows += rows;
                suspended = isSuspended;
            }
            while (suspended);

            Assert.Equal(rowCount, totalRows);

            await SendCloseAsync(stream, 'P', "");
            await ExpectMessageAsync(stream, '3'); // CloseComplete

            await SendSyncAsync(stream);
            await ExpectMessageAsync(stream, 'Z');
        }
        finally
        {
            await server.DisposeAsync();
            engine.Dispose();
            try { Directory.Delete(tempPath, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Low-level helpers
    // -------------------------------------------------------------------------

    private static async Task SendStartupMessageAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, 196608); // protocol 3.0
        WriteCString(ms, "database");
        WriteCString(ms, "WalhallaSql");
        WriteCString(ms, ""); // terminator

        var body = ms.ToArray();
        var len = 4 + body.Length;
        await stream.WriteAsync(Int32Bytes(len));
        await stream.WriteAsync(body);
    }

    private static async Task SkipToReadyForQueryAsync(Stream stream)
    {
        while (true)
        {
            var type = await ReadMessageTypeAsync(stream);
            if (type == 'Z')
            {
                await ReadMessageAsync(stream);
                return;
            }

            // Authentication OK / ParameterStatus / BackendKeyData etc. just consume
            if (type == 'R' || type == 'S' || type == 'K')
            {
                await ReadMessageAsync(stream);
                continue;
            }

            throw new InvalidOperationException($"Unexpected message during startup: {(char)type}");
        }
    }

    private static async Task SendParseAsync(Stream stream, string statementName, string sql)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, statementName);
        WriteCString(ms, sql);
        WriteInt16(ms, 0); // no parameter types
        await WriteMessageAsync(stream, (byte)'P', ms.ToArray());
    }

    private static async Task SendBindAsync(Stream stream, string portalName, string statementName, object?[] parameters)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, portalName);
        WriteCString(ms, statementName);
        WriteInt16(ms, 0); // no param format codes
        WriteInt16(ms, (short)parameters.Length);
        foreach (var p in parameters)
        {
            var text = p?.ToString() ?? "";
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteInt32(ms, bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        WriteInt16(ms, 0); // no result format codes
        await WriteMessageAsync(stream, (byte)'B', ms.ToArray());
    }

    private static async Task SendExecuteAsync(Stream stream, string portalName, int maxRows)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, portalName);
        WriteInt32(ms, maxRows);
        await WriteMessageAsync(stream, (byte)'E', ms.ToArray());
    }

    private static async Task SendCloseAsync(Stream stream, char targetType, string name)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)targetType);
        WriteCString(ms, name);
        await WriteMessageAsync(stream, (byte)'C', ms.ToArray());
    }

    private static async Task SendSyncAsync(Stream stream)
    {
        await WriteMessageAsync(stream, (byte)'S', Array.Empty<byte>());
    }

    private static async Task ExpectMessageAsync(Stream stream, char expectedType)
    {
        var type = await ReadMessageTypeAsync(stream);
        Assert.Equal(expectedType, (char)type);
        await ReadMessageAsync(stream);
    }

    /// <summary>
    /// Reads DataRow messages until a terminating message arrives. Returns the number
    /// of rows and whether the termination was PortalSuspended (true) or
    /// CommandComplete (false).
    /// </summary>
    private static async Task<(int RowCount, bool Suspended)> CountRowsUntilTerminalAsync(Stream stream)
    {
        int rows = 0;
        while (true)
        {
            var type = await ReadMessageTypeAsync(stream);
            if (type == 'D')
            {
                await ReadMessageAsync(stream);
                rows++;
                continue;
            }

            if (type == 's')
            {
                // PortalSuspended has empty payload
                await ReadMessageAsync(stream);
                return (rows, true);
            }

            if (type == 'C')
            {
                await ReadMessageAsync(stream);
                return (rows, false);
            }

            if (type == 'T') // RowDescription may be sent once per portal
            {
                await ReadMessageAsync(stream);
                continue;
            }

            if (type == 'E')
            {
                var (payload, _) = await ReadMessageAsync(stream);
                var errorText = Encoding.UTF8.GetString(payload);
                throw new InvalidOperationException($"Server sent error: {errorText}");
            }

            throw new InvalidOperationException($"Unexpected message while reading portal rows: {(char)type}");
        }
    }

    private static async Task<byte> ReadMessageTypeAsync(Stream stream)
    {
        var one = await ReadExactlyOrNullAsync(stream, 1);
        if (one == null) throw new InvalidOperationException("End of stream while reading message type.");
        return one[0];
    }

    private static async Task<(byte[] Payload, int Length)> ReadMessageAsync(Stream stream)
    {
        var lenBytes = await ReadExactlyAsync(stream, 4);
        var length = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        if (length < 4) return (Array.Empty<byte>(), 0);
        var payload = await ReadExactlyAsync(stream, length - 4);
        return (payload, length);
    }

    private static async Task WriteMessageAsync(Stream stream, byte messageType, byte[] payload)
    {
        var header = new byte[5];
        header[0] = messageType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length + 4);
        await stream.WriteAsync(header);
        if (payload.Length > 0)
            await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var buffer = await ReadExactlyOrNullAsync(stream, length);
        if (buffer == null) throw new InvalidOperationException("Unexpected end of stream.");
        return buffer;
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

    private static byte[] Int32Bytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
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
}
