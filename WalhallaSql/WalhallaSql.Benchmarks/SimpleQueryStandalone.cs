using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WalhallaSql.PgWire;
using WalhallaSql;

namespace WalhallaSql.Benchmarks;

public static class SimpleQueryStandalone
{
    public static void Run()
    {
        Console.WriteLine("Setting up...");
        var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        for (int i = 0; i < 1000; i++)
            engine.Execute($"INSERT INTO Customers (Id, Name) VALUES ({i}, 'Customer_{i}')");

        var backendFactory = new Func<IPgWireBackendConnection>(() => new WalhallaSqlPgWireBackend(engine));
        var server = new PgWireServer(backendFactory, "127.0.0.1", 0);
        server.StartAsync().GetAwaiter().GetResult();
        var port = server.BoundPort;
        Console.WriteLine($"Server on port {port}");

        // Test 1: connect and startup
        Console.WriteLine("Test 1: Connecting...");
        using var client = new SimpleQueryConnection(port);
        Console.WriteLine("Test 1 PASSED");

        // Test 2: single query
        Console.WriteLine("Test 2: Sending query...");
        client.Query("SELECT Id, Name FROM Customers WHERE Id = 42");
        Console.WriteLine("Test 2 PASSED");

        // Test 3: single-thread benchmark
        Console.WriteLine("Test 3: Running 5000 queries (single-thread)...");
        var rng = new Random(42);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++)
            client.Query($"SELECT Id, Name FROM Customers WHERE Id = {rng.Next(1000)}");
        sw.Stop();
        Console.WriteLine($"5000 queries in {sw.Elapsed.TotalMilliseconds:F1} ms = {5000 / sw.Elapsed.TotalSeconds:F0} ops/sec");

        // Test 4: multi-thread benchmark
        Console.WriteLine("Test 4: Running 20000 queries (4 threads)...");
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            var tid = t;
            threads[t] = new Thread(() =>
            {
                using var conn = new SimpleQueryConnection(port);
                var r = new Random(42 + tid);
                for (int i = 0; i < 5000; i++)
                    conn.Query($"SELECT Id, Name FROM Customers WHERE Id = {r.Next(1000)}");
            });
            threads[t].Start();
        }
        foreach (var t in threads) t.Join();
        sw2.Stop();
        var totalQueries = 20000;
        Console.WriteLine($"{totalQueries} queries in {sw2.Elapsed.TotalMilliseconds:F1} ms = {totalQueries / sw2.Elapsed.TotalSeconds:F0} ops/sec");

        Console.WriteLine("All tests PASSED!");
    }

    sealed class SimpleQueryConnection : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _stream;
        readonly byte[] _readBuf = new byte[8192];
        readonly byte[] _writeBuf = new byte[512];
        int _bufPos, _bufLen;

        public SimpleQueryConnection(int port)
        {
            _tcp = new TcpClient();
            _tcp.Connect("127.0.0.1", port);
            _stream = _tcp.GetStream();
            _stream.ReadTimeout = 5000;
            var startup = BuildStartup();
            _stream.Write(startup, 0, startup.Length);
            ReadUntilReady();
        }

        public void Query(string sql)
        {
            var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
            var msgLen = sqlBytes.Length + 4;
            _writeBuf[0] = (byte)'Q';
            BinaryPrimitives.WriteInt32BigEndian(_writeBuf.AsSpan(1), msgLen);
            _stream.Write(_writeBuf, 0, 5);
            _stream.Write(sqlBytes, 0, sqlBytes.Length);
            ReadUntilReady();
        }

        void ReadUntilReady()
        {
            while (true)
            {
                Ensure(5);
                var type = _readBuf[_bufPos];
                var len = BinaryPrimitives.ReadInt32BigEndian(_readBuf.AsSpan(_bufPos + 1));
                _bufPos += 5;
                var payloadLen = len - 4;

                if (type == 'E')
                {
                    Ensure(payloadLen);
                    _bufPos += payloadLen;
                    continue;
                }
                Ensure(payloadLen);
                if (type == 'Z') { _bufPos += payloadLen; return; }
                _bufPos += payloadLen;
            }
        }

        void Ensure(int count)
        {
            var available = _bufLen - _bufPos;
            if (available >= count) return;
            if (_bufPos > 0 && available > 0)
                Array.Copy(_readBuf, _bufPos, _readBuf, 0, available);
            _bufPos = 0;
            _bufLen = available;
            while (_bufLen < count)
            {
                var read = _stream.Read(_readBuf, _bufLen, _readBuf.Length - _bufLen);
                if (read == 0) throw new Exception("Connection closed");
                _bufLen += read;
            }
        }

        public void Dispose()
        {
            try { _tcp.Close(); } catch { }
        }
    }

    static byte[] BuildStartup()
    {
        var pairs = new[] { "user", "test", "database", "WalhallaSql" };
        int totalLen = 8;
        foreach (var p in pairs) totalLen += Encoding.UTF8.GetByteCount(p) + 1;
        totalLen += 1;
        var msg = new byte[totalLen];
        var pos = 0;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(pos), totalLen); pos += 4;
        msg[pos++] = 0; msg[pos++] = 3; msg[pos++] = 0; msg[pos++] = 0;
        foreach (var p in pairs)
        {
            var b = Encoding.UTF8.GetBytes(p);
            Array.Copy(b, 0, msg, pos, b.Length);
            pos += b.Length;
            msg[pos++] = 0;
        }
        msg[pos] = 0;
        return msg;
    }
}
