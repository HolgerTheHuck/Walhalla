using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Demonstrates the query-planner impact of ANALYZE on a skewed dataset.
///
/// The <c>Region</c> column is strongly skewed: 1% "R1", 99% "R0".
/// Without ANALYZE the planner uses a default selectivity constant; after
/// ANALYZE it uses real MCV (most-common-value) statistics to produce accurate
/// <c>est_rows</c> and pick the right index / join strategy.
///
/// Run with: dotnet run -c Release -- --filter *StatisticsAnalyzeBenchmark*
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(5)]
[MemoryDiagnoser]
public class StatisticsAnalyzeBenchmark
{
    /// <summary>
    /// When <c>true</c> ANALYZE runs during <see cref="Setup"/> before any
    /// query is prepared, so the planner has real statistics.
    /// When <c>false</c> the planner operates on defaults only.
    /// </summary>
    [Params(false, true)]
    public bool UseAnalyze { get; set; }

    private WalhallaEngine _engine = null!;
    private WalhallaPreparedStatement _stmtRare = null!;
    private WalhallaPreparedStatement _stmtCommon = null!;
    private WalhallaPreparedStatement _stmtJoin = null!;

    private const int RowCount = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _engine = WalhallaEngine.InMemory();

        _engine.Execute("""
            CREATE TABLE Customers (
                Id     INT          NOT NULL,
                Name   VARCHAR(100) NOT NULL,
                Region VARCHAR(10)  NOT NULL
            )
            """);
        _engine.Execute("CREATE INDEX ix_Customers_Region ON Customers (Region)");

        _engine.Execute("""
            CREATE TABLE Orders (
                Id         INT  NOT NULL,
                CustomerId INT  NOT NULL,
                Amount     REAL NOT NULL
            )
            """);
        _engine.Execute("CREATE INDEX ix_Orders_CustomerId ON Orders (CustomerId)");

        // 1% rare ("R1") — 99% common ("R0").
        var rnd = new Random(42);
        var custRows = new List<object?[]>(RowCount);
        for (int i = 1; i <= RowCount; i++)
            custRows.Add([i, "Customer " + i, i <= 100 ? "R1" : "R0"]);
        _engine.InsertBatch("Customers", custRows);

        var orderRows = new List<object?[]>(RowCount);
        for (int i = 1; i <= RowCount; i++)
            orderRows.Add([i, (i % RowCount) + 1, Math.Round(rnd.NextDouble() * 1000, 2)]);
        _engine.InsertBatch("Orders", orderRows);

        if (UseAnalyze)
            _engine.Execute("ANALYZE");

        // Prepare after optional ANALYZE so the planner can use fresh stats.
        _stmtRare   = _engine.Prepare("SELECT Id, Name FROM Customers WHERE Region = 'R1'");
        _stmtCommon = _engine.Prepare("SELECT Id, Name FROM Customers WHERE Region = 'R0'");
        _stmtJoin   = _engine.Prepare(
            "SELECT c.Name, o.Amount FROM Customers c " +
            "INNER JOIN Orders o ON o.CustomerId = c.Id WHERE c.Region = 'R1'");
    }

    [GlobalCleanup]
    public void Cleanup() => _engine?.Dispose();

    /// <summary>
    /// Filter on the rare value (1 % of rows).
    /// With ANALYZE the planner knows the true cardinality and picks an index seek.
    /// Without ANALYZE it may over-estimate and prefer a scan.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int SelectRareRegion() => _stmtRare.Execute().Rows.Count;

    /// <summary>
    /// Filter on the dominant value (99 % of rows).
    /// With ANALYZE the planner avoids the index and uses a full scan, which is cheaper.
    /// </summary>
    [Benchmark]
    public int SelectCommonRegion() => _stmtCommon.Execute().Rows.Count;

    /// <summary>
    /// Join with a selective outer filter.
    /// Accurate est_rows guides the join-strategy choice (nested-loop vs hash).
    /// </summary>
    [Benchmark]
    public int JoinWithSkewedFilter() => _stmtJoin.Execute().Rows.Count;
}
