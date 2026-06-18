using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Bulk-insert benchmark: 100 000 rows in a single transaction.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class BulkInsertBenchmark
{
    private const int RowCount = 100_000;

    // ── WalhallaSql state ──
    private WalhallaEngine _walhallaEngine = null!;
    private string? _walhallaPath;

    // ── SQLite state ──
    private SqliteConnection _sqliteConn = null!;
    private string? _sqliteFile;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // WalhallaSql disk
        _walhallaPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Bulk",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_walhallaPath);
        _walhallaEngine = new WalhallaEngine(new WalhallaOptions(_walhallaPath)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });
        _walhallaEngine.Execute(
            "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, TotalAmount REAL NOT NULL)");

        // SQLite disk (WAL, sync OFF for fair comparison)
        _sqliteFile = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Bulk",
            Guid.NewGuid().ToString("N") + ".sqlite3");
        Directory.CreateDirectory(Path.GetDirectoryName(_sqliteFile)!);
        _sqliteConn = new SqliteConnection($"Data Source={_sqliteFile};Mode=ReadWriteCreate");
        _sqliteConn.Open();
        using (var cmd = _sqliteConn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _sqliteConn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE Orders (Id INTEGER PRIMARY KEY, CustomerId INTEGER NOT NULL, TotalAmount REAL NOT NULL)";
            cmd.ExecuteNonQuery();
        }
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

    [Benchmark(Baseline = true, Description = "Sqlite_BulkInsert_100k")]
    public int SqliteBulkInsert()
    {
        // Use prepared statement inside a transaction for best SQLite throughput.
        using var tx = _sqliteConn.BeginTransaction();
        using var cmd = _sqliteConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@p0, @p1, @p2)";
        var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
        var p1 = cmd.Parameters.Add("@p1", SqliteType.Integer);
        var p2 = cmd.Parameters.Add("@p2", SqliteType.Real);

        for (int i = 1; i <= RowCount; i++)
        {
            p0.Value = (long)i;
            p1.Value = (long)(i % 10_000);
            p2.Value = (double)i;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();

        // Cleanup so the next iteration can reuse the table.
        using var del = _sqliteConn.CreateCommand();
        del.CommandText = "DELETE FROM Orders";
        del.ExecuteNonQuery();

        return RowCount;
    }

    [Benchmark(Description = "Walhalla_BulkInsert_100k")]
    public int WalhallaBulkInsert()
    {
        var rows = new List<object?[]>(RowCount);
        for (int i = 1; i <= RowCount; i++)
            rows.Add(new object?[] { i, i % 10_000, (double)i });

        _walhallaEngine.InsertBatch("Orders", rows);

        // Cleanup
        _walhallaEngine.Execute("DELETE FROM Orders");

        return RowCount;
    }
}
