using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Mixed read/write benchmark: 70 % reads, 30 % writes, 4 threads, fixed operation count.
///
/// WalhallaSql uses MVCC (snapshot isolation) — readers do not block writers.
/// SQLite uses WAL mode but still serialises writers on the connection mutex,
/// so throughput is expected to be lower under concurrent writes.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class MixedReadWriteBenchmark
{
    private const int SeedRowCount = 10_000;
    private const int OpsPerThread = 250;   // 4 threads × 250 = 1 000 ops per iteration
    private const double ReadRatio = 0.70;  // 70 % reads

    // ── WalhallaSql state ──
    private WalhallaEngine _walhallaEngine = null!;
    private string? _walhallaPath;
    private int _walhallaCounter;

    // ── SQLite state ──
    private SqliteConnection _sqliteConn = null!;
    private string? _sqliteFile;
    private int _sqliteCounter;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // WalhallaSql disk (fast mode: no sync)
        _walhallaPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Mixed",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_walhallaPath);
        _walhallaEngine = new WalhallaEngine(new WalhallaOptions(_walhallaPath)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });
        SetupWalhalla(_walhallaEngine);

        // SQLite disk (WAL, sync NORMAL)
        _sqliteFile = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Mixed",
            Guid.NewGuid().ToString("N") + ".sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(_sqliteFile)!);
        _sqliteConn = new SqliteConnection($"Data Source={_sqliteFile};Mode=ReadWriteCreate");
        _sqliteConn.Open();
        using (var pragma = _sqliteConn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        SetupSqlite(_sqliteConn);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _walhallaEngine?.Dispose();
        if (_walhallaPath != null)
        {
            try { Directory.Delete(_walhallaPath, true); } catch { }
        }

        _sqliteConn?.Close();
        _sqliteConn?.Dispose();
        if (_sqliteFile != null)
        {
            try { File.Delete(_sqliteFile); } catch { }
            try { File.Delete(_sqliteFile + "-wal"); } catch { }
            try { File.Delete(_sqliteFile + "-shm"); } catch { }
        }
    }

    // ── Benchmarks ──

    [Benchmark(Baseline = true, Description = "Sqlite_MixedRW_4t")]
    public int SqliteMixedReadWrite()
    {
        var counter = Interlocked.Increment(ref _sqliteCounter);
        var tasks = new Task[4];
        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < OpsPerThread; i++)
                {
                    bool isRead = (i % 100) < (int)(ReadRatio * 100);
                    if (isRead)
                    {
                        // Read: SELECT by PK
                        var id = 1 + ((i + counter) % SeedRowCount);
                        lock (_sqliteConn)
                        {
                            using var cmd = _sqliteConn.CreateCommand();
                            cmd.CommandText = "SELECT Id, Name FROM Customers WHERE Id = @id";
                            cmd.Parameters.AddWithValue("@id", (long)id);
                            using var reader = cmd.ExecuteReader();
                            while (reader.Read()) { }
                        }
                    }
                    else
                    {
                        // Write: UPDATE Name by PK
                        var id = 1 + ((i + counter) % SeedRowCount);
                        lock (_sqliteConn)
                        {
                            using var cmd = _sqliteConn.CreateCommand();
                            cmd.CommandText = "UPDATE Customers SET Name = @n WHERE Id = @id";
                            cmd.Parameters.AddWithValue("@n", $"Updated-{id}-{counter}");
                            cmd.Parameters.AddWithValue("@id", (long)id);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        return 4 * OpsPerThread;
    }

    [Benchmark(Description = "Walhalla_MixedRW_4t")]
    public int WalhallaMixedReadWrite()
    {
        var counter = Interlocked.Increment(ref _walhallaCounter);
        var tasks = new Task[4];
        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < OpsPerThread; i++)
                {
                    bool isRead = (i % 100) < (int)(ReadRatio * 100);
                    if (isRead)
                    {
                        // Read: SELECT by PK
                        var id = 1 + ((i + counter) % SeedRowCount);
                        _walhallaEngine.Execute(
                            $"SELECT Id, Name FROM Customers WHERE Id = {id}");
                    }
                    else
                    {
                        // Write: UPDATE Name by PK
                        var id = 1 + ((i + counter) % SeedRowCount);
                        _walhallaEngine.Execute(
                            $"UPDATE Customers SET Name = 'Updated-{id}-{counter}' WHERE Id = {id}");
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        return 4 * OpsPerThread;
    }

    // ── Setup helpers ──

    private static void SetupWalhalla(WalhallaEngine engine)
    {
        engine.Execute(
            "CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL, Region VARCHAR(50) NOT NULL)");

        var rows = new List<object?[]>(SeedRowCount);
        for (int i = 1; i <= SeedRowCount; i++)
            rows.Add(new object?[] { i, "Customer " + i, "R" + (i % 10) });
        engine.InsertBatch("Customers", rows);
    }

    private static void SetupSqlite(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using (var ddl = conn.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText =
                "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Region TEXT NOT NULL)";
            ddl.ExecuteNonQuery();
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO Customers (Id, Name, Region) VALUES (@p0, @p1, @p2)";
        var p0 = ins.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = ins.Parameters.Add("@p1", SqliteType.Text);
        var p2 = ins.Parameters.Add("@p2", SqliteType.Text);

        for (int i = 1; i <= SeedRowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = "Customer " + i;
            p2.Value = "R" + (i % 10);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
