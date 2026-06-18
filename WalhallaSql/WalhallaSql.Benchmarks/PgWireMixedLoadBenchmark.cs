using System;
using System.Buffers.Binary;
using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using WalhallaSql.PgWire;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.PostgreSql;
using WalhallaSql;

// ReSharper disable All
#pragma warning disable CS0414

namespace WalhallaSql.Benchmarks;

// ── Shared mixed-load runner ────────────────────────────────────────────────

internal sealed class MixedLoadResult
{
    public long ReadsDone;
    public long WritesDone;
    public long Errors;
}

internal static class MixedLoadRunner
{
    public static MixedLoadResult Run(
        int readThreads, int writeThreads, int durationMs,
        Func<DbConnection> connFactory,
        int customerCount, int orderCount)
    {
        var result = new MixedLoadResult();
        var totalThreads = readThreads + writeThreads;

        using var cts = new CancellationTokenSource(durationMs);
        var tasks = new Task[totalThreads];

        for (var i = 0; i < readThreads; i++)
        {
            var seed = 42 + i * 17;
            tasks[i] = Task.Run(() => ReadLoop(connFactory, result, customerCount, new Random(seed), cts.Token));
        }

        for (var i = 0; i < writeThreads; i++)
        {
            var seed = 137 + i * 23;
            tasks[readThreads + i] = Task.Run(() => WriteLoop(connFactory, result, customerCount, orderCount, new Random(seed), cts.Token));
        }

        Task.WaitAll(tasks);
        return result;
    }

    private static void ReadLoop(Func<DbConnection> connFactory, MixedLoadResult result,
        int customerCount, Random rng, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cid = rng.Next(customerCount);
                using var conn = connFactory();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Id, Name FROM Customers WHERE Id = {cid}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) { }
                Interlocked.Increment(ref result.ReadsDone);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Increment(ref result.Errors);
            }
        }
    }

    private static void WriteLoop(Func<DbConnection> connFactory, MixedLoadResult result,
        int customerCount, int orderCount, Random rng, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cid = rng.Next(customerCount);
                using var conn = connFactory();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE Customers SET Name = 'Upd{rng.Next(1_000_000)}' WHERE Id = {cid}";
                cmd.ExecuteNonQuery();
                Interlocked.Increment(ref result.WritesDone);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Increment(ref result.Errors);
            }
        }
    }
}

// ── WalhallaSql over PgWire benchmark ───────────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlPgWireBenchmark : IDisposable
{
    private const int CustomerCount = 10_000;
    private const int OrderCount = 10_000;
    private const int DurationMs = 3_000;

    private WalhallaEngine _engine = null!;
    private PgWireServer _server = null!;
    private NpgsqlDataSource _dataSource = null!;
    private string _connString = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = WalhallaEngine.InMemory();
        var backendFactory = new Func<IPgWireBackendConnection>(() => new WalhallaSqlPgWireBackend(_engine));
        _server = new PgWireServer(backendFactory, "127.0.0.1", 0);
        _server.StartAsync().GetAwaiter().GetResult();

        _connString = $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id=test;Password=test;Pooling=true;MaxPoolSize=64;MinPoolSize=4;Timeout=10;Command Timeout=30;No Reset On Close=true";
        _dataSource = NpgsqlDataSource.Create(_connString);

        SeedViaNpgsql();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dataSource?.Dispose();
        _server?.DisposeAsync().GetAwaiter().GetResult();
        _engine?.Dispose();
    }

    public void Dispose() => Cleanup();

    private void SeedViaNpgsql()
    {
        using var conn = _dataSource.CreateConnection();
        conn.Open();

        ExecuteNpgsql(conn, "CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING, Email STRING, Region STRING)");
        ExecuteNpgsql(conn, "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE, OrderDate DATETIME)");
        ExecuteNpgsql(conn, "CREATE INDEX IX_Customers_Email ON Customers (Email)");
        ExecuteNpgsql(conn, "CREATE INDEX IX_Customers_Region ON Customers (Region)");
        ExecuteNpgsql(conn, "CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId)");

        for (var i = 0; i < CustomerCount; i++)
            ExecuteNpgsql(conn, $"INSERT INTO Customers (Id, Name, Email, Region) VALUES ({i}, 'Customer_{i}', 'user{i}@test.com', 'Region_{i % 10}')");

        for (var i = 0; i < OrderCount; i++)
            ExecuteNpgsql(conn, $"INSERT INTO Orders (Id, CustomerId, Amount, OrderDate) VALUES ({i}, {i % CustomerCount}, {i * 10.0}, '2025-01-01')");
    }

    private static void ExecuteNpgsql(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DbConnection CreateConn()
    {
        var conn = _dataSource.CreateConnection();
        conn.Open();
        return conn;
    }

    [Benchmark] public double Mixed_2t()  => RunAndReport("Mixed_2t", 2);
    [Benchmark] public double Mixed_4t()  => RunAndReport("Mixed_4t", 4);
    [Benchmark] public double Mixed_8t()  => RunAndReport("Mixed_8t", 8);
    [Benchmark] public double Mixed_16t() => RunAndReport("Mixed_16t", 16);
    [Benchmark] public double ReadOnly_4t()  => RunAndReport("ReadOnly_4t", 4, writesEnabled: false);
    [Benchmark] public double WriteOnly_4t() => RunAndReport("WriteOnly_4t", 4, readsEnabled: false);
    [Benchmark] public double ReadOnly_Embedded() => RunEmbeddedReads("ReadOnly_Embedded", 2);
    [Benchmark] public double ReadOnly_SimpleQuery_2t() => RunSimpleQueryReads("ReadOnly_SimpleQuery_2t", 2);
    [Benchmark] public double ReadOnly_SimpleQuery_4t() => RunSimpleQueryReads("ReadOnly_SimpleQuery_4t", 4);

    // Direct engine reads — bypasses PgWire entirely to measure protocol overhead.
    private double RunEmbeddedReads(string name, int threads)
    {
        var result = new MixedLoadResult();
        using var cts = new CancellationTokenSource(DurationMs);
        var tasks = new Task[threads];

        for (var i = 0; i < threads; i++)
        {
            var seed = 42 + i * 17;
            var rng = new Random(seed);
            tasks[i] = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var cid = rng.Next(CustomerCount);
                    _engine.Execute($"SELECT Id, Name FROM Customers WHERE Id = {cid}");
                    Interlocked.Increment(ref result.ReadsDone);
                }
            });
        }

        Task.WaitAll(tasks);
        var opsPerSec = result.ReadsDone / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {result.ReadsDone} reads = {opsPerSec:F0} ops/sec ({result.Errors} errors)");
        return opsPerSec;
    }

    // Raw TCP simple query reads — bypasses Npgsql to measure true PgWire simple-query throughput.
    private double RunSimpleQueryReads(string name, int threads)
    {
        var result = new MixedLoadResult();
        var port = _server.BoundPort;
        using var cts = new CancellationTokenSource(DurationMs);
        var tasks = new Task[threads];

        for (var t = 0; t < threads; t++)
        {
            var seed = 42 + t * 17;
            var customerCount = CustomerCount;
            tasks[t] = Task.Run(() =>
            {
                var client = new SimpleQueryClient(port);
                var rng = new Random(seed);
                while (!cts.Token.IsCancellationRequested)
                {
                    var cid = rng.Next(customerCount);
                    client.Query($"SELECT Id, Name FROM Customers WHERE Id = {cid}");
                    Interlocked.Increment(ref result.ReadsDone);
                }
                client.Dispose();
            });
        }

        Task.WaitAll(tasks);
        var opsPerSec = result.ReadsDone / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {result.ReadsDone} reads = {opsPerSec:F0} ops/sec ({result.Errors} errors)");
        return opsPerSec;
    }

    private sealed class SimpleQueryClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly byte[] _readBuf = new byte[8192];
        private int _bufPos, _bufLen;
        private readonly byte[] _writeBuf = new byte[512];

        public SimpleQueryClient(int port)
        {
            _tcp = new TcpClient();
            _tcp.Connect("127.0.0.1", port);
            _stream = _tcp.GetStream();

            // Send startup message
            var startup = BuildStartupMessage();
            _stream.Write(startup, 0, startup.Length);
            ReadUntilReady();
        }

        public void Query(string sql)
        {
            // Build Query message: 'Q' + length + sql\0
            var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
            var msgLen = sqlBytes.Length + 4;
            _writeBuf[0] = (byte)'Q';
            BinaryPrimitives.WriteInt32BigEndian(_writeBuf.AsSpan(1), msgLen);
            _stream.Write(_writeBuf, 0, 5);
            _stream.Write(sqlBytes, 0, sqlBytes.Length);
            ReadUntilReady();
        }

        private void ReadUntilReady()
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

        private void Ensure(int count)
        {
            var available = _bufLen - _bufPos;
            if (available >= count) return;

            // Move remaining data to front
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
            try { _stream.Dispose(); } catch { }
            try { _tcp.Dispose(); } catch { }
        }

        static byte[] BuildStartupMessage()
        {
            var pairs = new[] { "user", "test", "database", "WalhallaSql" };
            int totalLen = 8;
            foreach (var p in pairs) totalLen += Encoding.UTF8.GetByteCount(p) + 1;
            totalLen += 1;
            var msg = new byte[totalLen];
            var pos = 0;
            BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(pos), totalLen); pos += 4;
            msg[pos++] = 0; msg[pos++] = 3; msg[pos++] = 0; msg[pos++] = 0; // protocol 3.0
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

    private double RunAndReport(string name, int threads, bool readsEnabled = true, bool writesEnabled = true)
    {
        var actualReads = readsEnabled ? threads / 2 : 0;
        var actualWrites = writesEnabled ? threads - actualReads : 0;
        var actualThreads = actualReads + actualWrites;
        if (actualThreads == 0) return 0;

        var res = MixedLoadRunner.Run(actualReads, actualWrites, DurationMs, CreateConn, CustomerCount, OrderCount);
        var opsPerSec = (res.ReadsDone + res.WritesDone) / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {res.ReadsDone} reads + {res.WritesDone} writes = {opsPerSec:F0} ops/sec ({res.Errors} errors)");
        return opsPerSec;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        using var conn = _dataSource.CreateConnection();
        conn.Open();
        // Restore names that were modified by UPDATEs
        for (var i = 0; i < CustomerCount; i++)
            ExecuteNpgsql(conn, $"UPDATE Customers SET Name = 'Customer_{i}' WHERE Id = {i}");
    }
}

// ── MSSQL direct benchmark ─────────────────────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class MssqlDirectBenchmark : IDisposable
{
    private const int CustomerCount = 10_000;
    private const int OrderCount = 10_000;
    private const int DurationMs = 3_000;

    private string _dbName = null!;
    private string _connString = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbName = $"WalhallaBench_{Guid.NewGuid():N}";
        _connString = $"Server=localhost;Database={_dbName};Integrated Security=True;TrustServerCertificate=True;Pooling=true;Max Pool Size=64;Min Pool Size=4;Connect Timeout=10;Command Timeout=30";

        using var masterConn = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;Pooling=true;Max Pool Size=64;Min Pool Size=4");
        masterConn.Open();
        using var cmd = masterConn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_dbName}]";
        cmd.ExecuteNonQuery();

        SeedViaSqlClient();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            using var masterConn = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;Pooling=true;Max Pool Size=64;Min Pool Size=4");
            masterConn.Open();
            using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]";
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => Cleanup();

    private void SeedViaSqlClient()
    {
        using var conn = new SqlConnection(_connString);
        conn.Open();

        ExecuteSql(conn, "CREATE TABLE Customers (Id INT PRIMARY KEY, Name NVARCHAR(200), Email NVARCHAR(200), Region NVARCHAR(100))");
        ExecuteSql(conn, "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount FLOAT, OrderDate DATETIME2)");
        ExecuteSql(conn, "CREATE INDEX IX_Customers_Email ON Customers (Email)");
        ExecuteSql(conn, "CREATE INDEX IX_Customers_Region ON Customers (Region)");
        ExecuteSql(conn, "CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId)");

        for (var i = 0; i < CustomerCount; i++)
            ExecuteSql(conn, $"INSERT INTO Customers (Id, Name, Email, Region) VALUES ({i}, 'Customer_{i}', 'user{i}@test.com', 'Region_{i % 10}')");

        for (var i = 0; i < OrderCount; i++)
            ExecuteSql(conn, $"INSERT INTO Orders (Id, CustomerId, Amount, OrderDate) VALUES ({i}, {i % CustomerCount}, {i * 10.0}, '2025-01-01')");
    }

    private static void ExecuteSql(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DbConnection CreateConn()
    {
        var conn = new SqlConnection(_connString);
        conn.Open();
        return conn;
    }

    [Benchmark] public double Mixed_2t()  => RunAndReport("Mixed_2t", 2);
    [Benchmark] public double Mixed_4t()  => RunAndReport("Mixed_4t", 4);
    [Benchmark] public double Mixed_8t()  => RunAndReport("Mixed_8t", 8);
    [Benchmark] public double Mixed_16t() => RunAndReport("Mixed_16t", 16);
    [Benchmark] public double ReadOnly_4t()  => RunAndReport("ReadOnly_4t", 4, writesEnabled: false);
    [Benchmark] public double WriteOnly_4t() => RunAndReport("WriteOnly_4t", 4, readsEnabled: false);

    private double RunAndReport(string name, int threads, bool readsEnabled = true, bool writesEnabled = true)
    {
        var actualReads = readsEnabled ? threads / 2 : 0;
        var actualWrites = writesEnabled ? threads - actualReads : 0;
        var actualThreads = actualReads + actualWrites;
        if (actualThreads == 0) return 0;

        var res = MixedLoadRunner.Run(actualReads, actualWrites, DurationMs, CreateConn, CustomerCount, OrderCount);
        var opsPerSec = (res.ReadsDone + res.WritesDone) / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {res.ReadsDone} reads + {res.WritesDone} writes = {opsPerSec:F0} ops/sec ({res.Errors} errors)");
        return opsPerSec;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        using var conn = new SqlConnection(_connString);
        conn.Open();
        for (var i = 0; i < CustomerCount; i++)
            ExecuteSql(conn, $"UPDATE Customers SET Name = 'Customer_{i}' WHERE Id = {i}");
    }
}

// ── PostgreSQL Native benchmark (localhost) ──────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class PostgreSqlNativeBenchmark : IDisposable
{
    private const int CustomerCount = 10_000;
    private const int OrderCount = 10_000;
    private const int DurationMs = 3_000;

    private const string PgPassword = "Test";
    private const string PgDatabase = "walhalla_bench";
    private string _connString = null!;
    private string _adminConnString = null!;
    private NpgsqlDataSource _dataSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connString = $"Host=localhost;Port=5432;Database={PgDatabase};Username=postgres;Password={PgPassword};Pooling=true;MaxPoolSize=64;MinPoolSize=4;Timeout=10;Command Timeout=30;No Reset On Close=true";
        _adminConnString = $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={PgPassword};Timeout=10";

        // Drop and recreate database for clean state
        using (var adminConn = new NpgsqlConnection(_adminConnString))
        {
            adminConn.Open();
            using var cmd = adminConn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{PgDatabase}\" WITH (FORCE)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"CREATE DATABASE \"{PgDatabase}\"";
            cmd.ExecuteNonQuery();
        }

        _dataSource = NpgsqlDataSource.Create(_connString);
        Seed();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_dataSource != null) _dataSource.Dispose();
        try
        {
            using var adminConn = new NpgsqlConnection(_adminConnString);
            adminConn.Open();
            using var cmd = adminConn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{PgDatabase}\" WITH (FORCE)";
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => Cleanup();

    private void Seed()
    {
        using var conn = _dataSource.OpenConnection();

        ExecutePgsql(conn, "CREATE TABLE Customers (Id INT PRIMARY KEY, Name TEXT, Email TEXT, Region TEXT)");
        ExecutePgsql(conn, "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE PRECISION, OrderDate TIMESTAMP)");
        ExecutePgsql(conn, "CREATE INDEX IX_Customers_Email ON Customers (Email)");
        ExecutePgsql(conn, "CREATE INDEX IX_Customers_Region ON Customers (Region)");
        ExecutePgsql(conn, "CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId)");

        for (var i = 0; i < CustomerCount; i++)
            ExecutePgsql(conn, $"INSERT INTO Customers (Id, Name, Email, Region) VALUES ({i}, 'Customer_{i}', 'user{i}@test.com', 'Region_{i % 10}')");

        for (var i = 0; i < OrderCount; i++)
            ExecutePgsql(conn, $"INSERT INTO Orders (Id, CustomerId, Amount, OrderDate) VALUES ({i}, {i % CustomerCount}, {i * 10.0}, '2025-01-01')");
    }

    private static void ExecutePgsql(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DbConnection CreateConn()
    {
        var conn = _dataSource.CreateConnection();
        conn.Open();
        return conn;
    }

    [Benchmark] public double Mixed_2t()  => RunAndReport("Mixed_2t", 2);
    [Benchmark] public double Mixed_4t()  => RunAndReport("Mixed_4t", 4);
    [Benchmark] public double Mixed_8t()  => RunAndReport("Mixed_8t", 8);
    [Benchmark] public double Mixed_16t() => RunAndReport("Mixed_16t", 16);
    [Benchmark] public double ReadOnly_4t()  => RunAndReport("ReadOnly_4t", 4, writesEnabled: false);
    [Benchmark] public double WriteOnly_4t() => RunAndReport("WriteOnly_4t", 4, readsEnabled: false);

    private double RunAndReport(string name, int threads, bool readsEnabled = true, bool writesEnabled = true)
    {
        var actualReads = readsEnabled ? threads / 2 : 0;
        var actualWrites = writesEnabled ? threads - actualReads : 0;
        var actualThreads = actualReads + actualWrites;
        if (actualThreads == 0) return 0;

        var res = MixedLoadRunner.Run(actualReads, actualWrites, DurationMs, CreateConn, CustomerCount, OrderCount);
        var opsPerSec = (res.ReadsDone + res.WritesDone) / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {res.ReadsDone} reads + {res.WritesDone} writes = {opsPerSec:F0} ops/sec ({res.Errors} errors)");
        return opsPerSec;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        using var conn = _dataSource.OpenConnection();
        for (var i = 0; i < CustomerCount; i++)
            ExecutePgsql(conn, $"UPDATE Customers SET Name = 'Customer_{i}' WHERE Id = {i}");
    }
}

// ── Tuned PostgreSQL Docker container benchmark ────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class PostgreSqlContainerBenchmark : IAsyncDisposable
{
    private const int CustomerCount = 10_000;
    private const int OrderCount = 10_000;
    private const int DurationMs = 3_000;

    private const string PgPassword = "Test";
    private const string PgDatabase = "walhalla_bench";
    private PostgreSqlContainer _container = null!;
    private string _connString = null!;
    private NpgsqlDataSource _dataSource = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithTmpfsMount("/var/lib/postgresql/data")
            .WithDatabase(PgDatabase)
            .WithUsername("postgres")
            .WithPassword(PgPassword)
            .Build();

        await _container.StartAsync();

        _connString = _container.GetConnectionString();
        _dataSource = NpgsqlDataSource.Create(_connString);
        Seed();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_container != null) await _container.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await Cleanup();

    private void Seed()
    {
        using var conn = _dataSource.OpenConnection();

        ExecutePgsql(conn, "CREATE TABLE Customers (Id INT PRIMARY KEY, Name TEXT, Email TEXT, Region TEXT)");
        ExecutePgsql(conn, "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT, Amount DOUBLE PRECISION, OrderDate TIMESTAMP)");
        ExecutePgsql(conn, "CREATE INDEX IX_Customers_Email ON Customers (Email)");
        ExecutePgsql(conn, "CREATE INDEX IX_Customers_Region ON Customers (Region)");
        ExecutePgsql(conn, "CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId)");

        for (var i = 0; i < CustomerCount; i++)
            ExecutePgsql(conn, $"INSERT INTO Customers (Id, Name, Email, Region) VALUES ({i}, 'Customer_{i}', 'user{i}@test.com', 'Region_{i % 10}')");

        for (var i = 0; i < OrderCount; i++)
            ExecutePgsql(conn, $"INSERT INTO Orders (Id, CustomerId, Amount, OrderDate) VALUES ({i}, {i % CustomerCount}, {i * 10.0}, '2025-01-01')");
    }

    private static void ExecutePgsql(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DbConnection CreateConn()
    {
        var conn = _dataSource.CreateConnection();
        conn.Open();
        return conn;
    }

    [Benchmark] public double Mixed_2t()  => RunAndReport("Mixed_2t", 2);
    [Benchmark] public double Mixed_4t()  => RunAndReport("Mixed_4t", 4);
    [Benchmark] public double Mixed_8t()  => RunAndReport("Mixed_8t", 8);
    [Benchmark] public double Mixed_16t() => RunAndReport("Mixed_16t", 16);
    [Benchmark] public double ReadOnly_4t()  => RunAndReport("ReadOnly_4t", 4, writesEnabled: false);
    [Benchmark] public double WriteOnly_4t() => RunAndReport("WriteOnly_4t", 4, readsEnabled: false);

    private double RunAndReport(string name, int threads, bool readsEnabled = true, bool writesEnabled = true)
    {
        var actualReads = readsEnabled ? threads / 2 : 0;
        var actualWrites = writesEnabled ? threads - actualReads : 0;
        var actualThreads = actualReads + actualWrites;
        if (actualThreads == 0) return 0;

        var res = MixedLoadRunner.Run(actualReads, actualWrites, DurationMs, CreateConn, CustomerCount, OrderCount);
        var opsPerSec = (res.ReadsDone + res.WritesDone) / (DurationMs / 1000.0);
        Console.WriteLine($"// {name}: {res.ReadsDone} reads + {res.WritesDone} writes = {opsPerSec:F0} ops/sec ({res.Errors} errors)");
        return opsPerSec;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        using var conn = _dataSource.OpenConnection();
        for (var i = 0; i < CustomerCount; i++)
            ExecutePgsql(conn, $"UPDATE Customers SET Name = 'Customer_{i}' WHERE Id = {i}");
    }
}
