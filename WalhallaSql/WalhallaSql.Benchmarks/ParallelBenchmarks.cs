using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Multi-threaded write benchmarks that demonstrate Group Commit advantages.
///
/// Under Fsync mode, concurrent writers enqueue into the GroupCommitQueue which
/// batches multiple transactions into a single fsync — throughput scales with
/// concurrency instead of being bottlenecked by per-transaction fsync latency.
///
/// Under None mode (no durability), there's no fsync at all — this shows the
/// theoretical maximum throughput without disk-sync overhead.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class GroupCommitParallelBenchmark
{
    private const int SeedRowCount = 5_000;
    private const int BatchSize = 100;

    private string? _fsyncPath;
    private string? _nonePath;
    private string? _sqlitePath;
    private WalhallaEngine _fsyncEngine = null!;
    private WalhallaEngine _noneEngine = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _sqliteConn = null!;

    private int _fsyncCounter;
    private int _noneCounter;
    private int _sqliteCounter;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // ── Fsync engine (GroupCommit enabled) ──────────────────────────
        _fsyncPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Parallel",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fsyncPath);
        var fsyncOpts = new WalhallaOptions(_fsyncPath)
        {
            WalSyncMode = WalhallaSql.Core.WalSyncMode.Fsync
        };
        _fsyncEngine = new WalhallaEngine(fsyncOpts);
        SetupEngine(_fsyncEngine);
        _fsyncEngine.Checkpoint();

        // ── None engine (no WAL, no durability) ────────────────────────
        _nonePath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Parallel",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_nonePath);
        var noneOpts = new WalhallaOptions(_nonePath)
        {
            WalSyncMode = WalhallaSql.Core.WalSyncMode.None
        };
        _noneEngine = new WalhallaEngine(noneOpts);
        SetupEngine(_noneEngine);
        _noneEngine.Checkpoint();

        // ── SQLite baseline ────────────────────────────────────────────
        _sqlitePath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Parallel",
            Guid.NewGuid().ToString("N") + ".sqlite3");
        _sqliteConn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_sqlitePath};Mode=ReadWriteCreate");
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
        _fsyncEngine?.Dispose();
        _noneEngine?.Dispose();
        _sqliteConn?.Close();
        _sqliteConn?.Dispose();

        TryDeleteDir(_fsyncPath);
        TryDeleteDir(_nonePath);
        TryDeleteFile(_sqlitePath);
        TryDeleteFile(_sqlitePath + "-wal");
        TryDeleteFile(_sqlitePath + "-shm");
    }

    // ── Benchmarks: 1 thread (baseline) ────────────────────────────────────

    [Benchmark(Description = "Fsync_1t")]
    public int FsyncSingleThread()
    {
        return RunParallelUpdates(_fsyncEngine, 1, BatchSize);
    }

    [Benchmark(Description = "None_1t")]
    public int NoneSingleThread()
    {
        return RunParallelUpdates(_noneEngine, 1, BatchSize);
    }

    [Benchmark(Description = "SQLite_1t")]
    public int SqliteSingleThread()
    {
        return RunSqliteUpdates(1, BatchSize);
    }

    // ── Benchmarks: 4 threads ──────────────────────────────────────────────

    [Benchmark(Description = "Fsync_4t")]
    public int FsyncFourThreads()
    {
        return RunParallelUpdates(_fsyncEngine, 4, BatchSize);
    }

    [Benchmark(Description = "None_4t")]
    public int NoneFourThreads()
    {
        return RunParallelUpdates(_noneEngine, 4, BatchSize);
    }

    [Benchmark(Description = "SQLite_4t")]
    public int SqliteFourThreads()
    {
        return RunSqliteUpdates(4, BatchSize);
    }

    // ── Benchmarks: 8 threads ──────────────────────────────────────────────

    [Benchmark(Description = "Fsync_8t")]
    public int FsyncEightThreads()
    {
        return RunParallelUpdates(_fsyncEngine, 8, BatchSize);
    }

    [Benchmark(Description = "None_8t")]
    public int NoneEightThreads()
    {
        return RunParallelUpdates(_noneEngine, 8, BatchSize);
    }

    [Benchmark(Description = "SQLite_8t")]
    public int SqliteEightThreads()
    {
        return RunSqliteUpdates(8, BatchSize);
    }

    // ── Benchmarks: INSERT (tests memTable + WAL contention) ───────────────

    [Benchmark(Description = "Insert_Fsync_4t")]
    public int InsertFsync4Threads()
    {
        return RunParallelInserts(_fsyncEngine, 4, 25);
    }

    [Benchmark(Description = "Insert_None_4t")]
    public int InsertNone4Threads()
    {
        return RunParallelInserts(_noneEngine, 4, 25);
    }

    // ── Worker methods ─────────────────────────────────────────────────────

    private int RunParallelUpdates(WalhallaEngine engine, int threadCount, int opsPerThread)
    {
        var counter = Interlocked.Increment(ref (engine == _fsyncEngine
            ? ref _fsyncCounter : ref _noneCounter));
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < opsPerThread; i++)
                {
                    var id = 1 + ((i * threadCount + t + counter) % SeedRowCount);
                    var newName = "'Updated-" + id + "-" + counter + "'";
                    engine.Execute(
                        "UPDATE Customers SET Name = " + newName + " WHERE Id = " + id);
                }
            });
        }
        Task.WaitAll(tasks);
        return threadCount * opsPerThread;
    }

    private int RunParallelInserts(WalhallaEngine engine, int threadCount, int opsPerThread)
    {
        var counter = Interlocked.Increment(ref (engine == _fsyncEngine
            ? ref _fsyncCounter : ref _noneCounter));
        var baseId = SeedRowCount + (counter * threadCount * opsPerThread);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadOffset = t * opsPerThread;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < opsPerThread; i++)
                {
                    var id = baseId + threadOffset + i;
                    engine.Execute(
                        "INSERT INTO Customers (Id, Name, Email, Region) VALUES (" +
                        id + ", 'ParUser" + id + "', 'par" + id + "@test.com', 'R0')");
                }
            });
        }
        Task.WaitAll(tasks);

        // Cleanup inserted rows (single-threaded, outside measurement noise).
        var lastId = baseId + (threadCount * opsPerThread) - 1;
        engine.Execute("DELETE FROM Customers WHERE Id BETWEEN " + baseId + " AND " + lastId);

        return threadCount * opsPerThread;
    }

    private int RunSqliteUpdates(int threadCount, int opsPerThread)
    {
        var counter = Interlocked.Increment(ref _sqliteCounter);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < opsPerThread; i++)
                {
                    var id = 1 + ((i * threadCount + t + counter) % SeedRowCount);
                    var newName = "Updated-" + id + "-" + counter;
                    lock (_sqliteConn)
                    {
                        using var cmd = _sqliteConn.CreateCommand();
                        cmd.CommandText = "UPDATE Customers SET Name = @n WHERE Id = @id";
                        cmd.Parameters.AddWithValue("@n", newName);
                        cmd.Parameters.AddWithValue("@id", (long)id);
                        cmd.ExecuteNonQuery();
                    }
                }
            });
        }
        Task.WaitAll(tasks);
        return threadCount * opsPerThread;
    }

    // ── Setup helpers ───────────────────────────────────────────────────────

    private static void SetupEngine(WalhallaEngine engine)
    {
        engine.Execute(@"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL,
                Email VARCHAR(100) NOT NULL,
                Region VARCHAR(50) NOT NULL
            );");

        var rows = new List<object?[]>(SeedRowCount);
        for (int i = 1; i <= SeedRowCount; i++)
            rows.Add(new object?[] { i, "Customer " + i, "cust" + i + "@demo.local", "R" + (i % 10) });
        engine.InsertBatch("Customers", rows);
    }

    private static void SetupSqlite(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using (var ddl = conn.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = @"
                CREATE TABLE Customers (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    Region TEXT NOT NULL
                );";
            ddl.ExecuteNonQuery();
        }

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            ins.Parameters.AddWithValue("@p0", (long)i);
            ins.Parameters.AddWithValue("@p1", "Customer " + i);
            ins.Parameters.AddWithValue("@p2", "cust" + i + "@demo.local");
            ins.Parameters.AddWithValue("@p3", "R" + (i % 10));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void TryDeleteDir(string? path)
    {
        if (path != null)
        {
            try { Directory.Delete(path, true); } catch { }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (path != null)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
