// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Vergleicht materialisierte und Streaming-Abfragen fuer grosse Resultsets.
/// Misst Laufzeit und Speicherallokation fuer "SELECT TOP N *" auf einer
/// großen Tabelle.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class StreamingBenchmark : IDisposable
{
    private const int TableRowCount = 100_000;
    private const int TopN = 10_000;

    private WalhallaEngine _engine = null!;
    private string? _tempDir;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Streaming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _engine = new WalhallaEngine(new WalhallaOptions(_tempDir)
        {
            WalSyncMode = Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });

        _engine.Execute(
            "CREATE TABLE Events (Id INT PRIMARY KEY, Category VARCHAR(20) NOT NULL, Value REAL NOT NULL, Payload VARCHAR(200) NOT NULL)");

        var rows = new object?[TableRowCount][];
        var random = new Random(42);
        for (int i = 0; i < TableRowCount; i++)
        {
            rows[i] = new object?[]
            {
                i + 1,
                "CAT" + (i % 10),
                random.NextDouble() * 1000.0,
                new string('x', 200)
            };
        }

        _engine.InsertBatch("Events", rows);
        _engine.Checkpoint();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _engine?.Dispose();
        if (_tempDir != null)
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Materialisierter Pfad: das gesamte TOP-N-Resultset wird auf einmal
    /// in eine Liste geladen.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Materialized_TOP_10000")]
    public int MaterializedTop10000()
    {
        var result = _engine.Execute($"SELECT TOP {TopN} * FROM Events");
        return result.Rows.Count;
    }

    /// <summary>
    /// Streaming-Pfad: Zeilen werden einzeln enumeriert, ohne vollstaendige
    /// Materialisierung im Arbeitsspeicher.
    /// </summary>
    [Benchmark(Description = "Streaming_TOP_10000")]
    public int StreamingTop10000()
    {
        using var stream = _engine.ExecuteStreaming($"SELECT TOP {TopN} * FROM Events");
        int count = 0;
        foreach (var _ in stream.EnumerateRows())
            count++;
        return count;
    }

    /// <summary>
    /// Asynchroner Streaming-Pfad fuer Vergleichbarkeit mit async-APIs.
    /// </summary>
    [Benchmark(Description = "StreamingAsync_TOP_10000")]
    public async Task<int> StreamingAsyncTop10000()
    {
        using var stream = await _engine.ExecuteStreamingAsync($"SELECT TOP {TopN} * FROM Events");
        int count = 0;
        await foreach (var _ in stream.EnumerateRowsAsync().ConfigureAwait(false))
            count++;
        return count;
    }

    public void Dispose() => GlobalCleanup();
}
