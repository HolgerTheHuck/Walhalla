using System;
using System.Data;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using WalhallaSql.AdoNet;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Vergleicht wiederholte einzelne parametrisierte INSERTs in einer Transaktion
/// zwischen WalhallaSql (via ADO.NET prepared statement) und SQLite.
/// Dies deckt das prepare / bind / execute / bind / execute-Muster ab.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class ParameterizedInsertBenchmark
{
    private const int RowCount = 10_000;

    // ── WalhallaSql state ──
    private WalhallaEngine _walhallaEngine = null!;
    private WalhallaSqlDbConnection _walhallaConn = null!;
    private string? _walhallaPath;

    // ── SQLite state ──
    private SqliteConnection _sqliteConn = null!;
    private string? _sqliteFile;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _walhallaPath = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.ParamInsert",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_walhallaPath);
        _walhallaEngine = new WalhallaEngine(new WalhallaOptions(_walhallaPath)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });
        _walhallaEngine.Execute(
            "CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, TotalAmount REAL NOT NULL)");
        _walhallaConn = new WalhallaSqlDbConnection(_walhallaEngine);
        _walhallaConn.Open();

        _sqliteFile = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.ParamInsert",
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
        _walhallaConn?.Dispose();
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

    [Benchmark(Baseline = true, Description = "Sqlite_ParamInsert_10k")]
    public int SqliteParameterizedInsert()
    {
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
        Cleanup(_sqliteConn, "DELETE FROM Orders");
        return RowCount;
    }

    [Benchmark(Description = "Walhalla_ParamInsert_10k")]
    public int WalhallaParameterizedInsert()
    {
        using var tx = _walhallaConn.BeginTransaction();
        using var cmd = _walhallaConn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@id, @customerId, @amount)";
        cmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@id", DbType = DbType.Int32 });
        cmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@customerId", DbType = DbType.Int32 });
        cmd.Parameters.Add(new WalhallaSqlDbParameter { ParameterName = "@amount", DbType = DbType.Double });

        for (int i = 1; i <= RowCount; i++)
        {
            cmd.Parameters["@id"].Value = i;
            cmd.Parameters["@customerId"].Value = i % 10_000;
            cmd.Parameters["@amount"].Value = (double)i;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Cleanup(_walhallaConn, "DELETE FROM Orders");
        return RowCount;
    }

    private static void Cleanup(IDbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
