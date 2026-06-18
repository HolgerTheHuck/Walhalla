using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Aggregate GROUP BY benchmark: SUM and AVG over 100 000 rows grouped by region.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class AggregateGroupByBenchmark
{
    private const int RowCount = 100_000;

    // ── WalhallaSql state ──
    private WalhallaEngine _walhallaEngine = null!;
    private WalhallaPreparedStatement _walhallaStmt = null!;

    // ── SQLite state ──
    private SqliteConnection _sqliteConn = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // WalhallaSql in-memory for pure engine throughput.
        _walhallaEngine = WalhallaEngine.InMemory();
        _walhallaEngine.Execute(
            "CREATE TABLE Sales (Id INT PRIMARY KEY, Region VARCHAR(10) NOT NULL, Amount REAL NOT NULL)");

        var rows = new List<object?[]>(RowCount);
        for (int i = 1; i <= RowCount; i++)
            rows.Add(new object?[] { i, "R" + (i % 10), (double)(i % 1000) });
        _walhallaEngine.InsertBatch("Sales", rows);

        _walhallaStmt = _walhallaEngine.Prepare(
            "SELECT Region, SUM(Amount), AVG(Amount) FROM Sales GROUP BY Region");

        // SQLite in-memory
        _sqliteConn = new SqliteConnection("Data Source=:memory:");
        _sqliteConn.Open();
        using (var cmd = _sqliteConn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE Sales (Id INTEGER PRIMARY KEY, Region TEXT NOT NULL, Amount REAL NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        using (var tx = _sqliteConn.BeginTransaction())
        {
            using var cmd = _sqliteConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Sales (Id, Region, Amount) VALUES (@p0, @p1, @p2)";
            var p0 = cmd.Parameters.Add("@p0", SqliteType.Integer);
            var p1 = cmd.Parameters.Add("@p1", SqliteType.Text);
            var p2 = cmd.Parameters.Add("@p2", SqliteType.Real);

            for (int i = 1; i <= RowCount; i++)
            {
                p0.Value = (long)i;
                p1.Value = "R" + (i % 10);
                p2.Value = (double)(i % 1000);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _walhallaEngine?.Dispose();
        _sqliteConn?.Close();
        _sqliteConn?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Sqlite_AggregateGroupBy_100k")]
    public int SqliteAggregateGroupBy()
    {
        using var cmd = _sqliteConn.CreateCommand();
        cmd.CommandText = "SELECT Region, SUM(Amount), AVG(Amount) FROM Sales GROUP BY Region";
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "Walhalla_AggregateGroupBy_100k")]
    public int WalhallaAggregateGroupBy()
    {
        return _walhallaStmt.Execute().Rows.Count;
    }
}
