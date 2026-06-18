// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Ods.Paging;
using Walhalla.Storage.Ods.Tree;
using Walhalla.Storage.Trees;
using Walhalla.Storage.Mvcc.Transactions;

namespace Walhalla.Benchmarks;

/// <summary>
/// Head-to-head benchmark: klassischer BPlusTree vs. MvccBPlusTree.
/// Misst Insertion, Point-Lookup und Range-Scan bei identischer Workload.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class BPlusTreeComparisonBenchmark : IDisposable
{
    private const int PageSize = 4096;

    [Params(10_000)]
    public int N;

    [Params(16)]
    public int KeySize;

    [Params(128)]
    public int ValueSize;

    private byte[][] _keys = null!;
    private byte[][] _values = null!;

    private string _classicPath = null!;
    private string _mvccPath = null!;

    private OdsPager _classicPager = null!;
    private BPlusTree _classicTree = null!;

    private OdsPager _mvccPager = null!;
    private MvccBPlusTree _mvccTree = null!;
    private TransactionManager _mvccTxManager = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var random = new Random(42);
        _keys = new byte[N][];
        _values = new byte[N][];

        for (int i = 0; i < N; i++)
        {
            _keys[i] = new byte[KeySize];
            random.NextBytes(_keys[i]);
            _values[i] = new byte[ValueSize];
            random.NextBytes(_values[i]);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _classicPath = Path.Combine(Path.GetTempPath(), $"walhalla_classic_{Guid.NewGuid():N}.ods");
        _mvccPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_{Guid.NewGuid():N}.ods");

        _classicPager = new OdsPager(_classicPath, PageSize, pageCacheCapacity: 1024);
        _classicTree = new BPlusTree(_classicPager, BuiltInKeyComparators.Bytewise);

        _mvccTxManager = new TransactionManager();
        _mvccPager = new OdsPager(_mvccPath, PageSize, pageCacheCapacity: 1024);
        _mvccTree = new MvccBPlusTree(_mvccPager, _mvccTxManager, BuiltInKeyComparators.Bytewise, order: 128);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _classicTree?.Dispose();
        _classicPager?.Dispose();
        _mvccTree?.Dispose();
        _mvccPager?.Dispose();

        try { if (File.Exists(_classicPath)) File.Delete(_classicPath); } catch { }
        try { if (File.Exists(_mvccPath)) File.Delete(_mvccPath); } catch { }
    }

    // ── Insertion ─────────────────────────────────────────────────────────────

    [Benchmark(Description = "Classic: Insert N", Baseline = false)]
    public void Classic_InsertN()
    {
        for (int i = 0; i < N; i++)
            _classicTree.Upsert(_keys[i], _values[i]);
    }

    [Benchmark(Description = "MVCC: Insert N")]
    public void Mvcc_InsertN()
    {
        for (int i = 0; i < N; i++)
        {
            var seq = _mvccTxManager.AcquireCommitSequence();
            _mvccTree.Upsert(seq, _keys[i], _values[i]);
        }
    }

    // ── Point Lookup (best-case: existierende Keys) ───────────────────────────

    [Benchmark(Description = "Classic: Lookup N")]
    public int Classic_LookupN()
    {
        // Zuerst einfügen, damit Lookup etwas zu finden hat
        for (int i = 0; i < N; i++)
            _classicTree.Upsert(_keys[i], _values[i]);

        int found = 0;
        for (int i = 0; i < N; i++)
        {
            if (_classicTree.TryGet(_keys[i], out _))
                found++;
        }
        return found;
    }

    [Benchmark(Description = "MVCC: Lookup N (TryGetLatest)")]
    public int Mvcc_LookupN()
    {
        for (int i = 0; i < N; i++)
        {
            var seq = _mvccTxManager.AcquireCommitSequence();
            _mvccTree.Upsert(seq, _keys[i], _values[i]);
        }

        int found = 0;
        for (int i = 0; i < N; i++)
        {
            if (_mvccTree.TryGetLatest(_keys[i], out _))
                found++;
        }
        return found;
    }

    // ── Range Scan (Scan 1 % der Keys) ────────────────────────────────────────

    [Benchmark(Description = "Classic: Scan 1%")]
    public int Classic_Scan1Pct()
    {
        for (int i = 0; i < N; i++)
            _classicTree.Upsert(_keys[i], _values[i]);

        // Sortierte Keys bestimmen, damit Range sinnvoll ist
        var sorted = _keys
            .Select((k, idx) => (Key: k, Index: idx))
            .OrderBy(x => x.Key, ByteArrayComparer.Instance)
            .ToList();

        int count = Math.Max(1, N / 100);
        var fromKey = sorted[0].Key;
        var toKey = sorted[count - 1].Key;

        int scanned = 0;
        foreach (var entry in _classicTree.EnumerateRange(fromKey, toKey))
            scanned++;
        return scanned;
    }

    [Benchmark(Description = "MVCC: Scan 1%")]
    public int Mvcc_Scan1Pct()
    {
        for (int i = 0; i < N; i++)
        {
            var seq = _mvccTxManager.AcquireCommitSequence();
            _mvccTree.Upsert(seq, _keys[i], _values[i]);
        }

        var sorted = _keys
            .Select((k, idx) => (Key: k, Index: idx))
            .OrderBy(x => x.Key, ByteArrayComparer.Instance)
            .ToList();

        int count = Math.Max(1, N / 100);
        var fromKey = sorted[0].Key;
        var toKey = sorted[count - 1].Key;

        var snapshot = _mvccTxManager.AcquireSnapshot();
        int scanned = 0;
        foreach (var entry in _mvccTree.ScanVisible(snapshot, fromKey, toKey))
            scanned++;
        return scanned;
    }

    public void Dispose()
    {
        IterationCleanup();
    }
}

/// <summary>
/// Hilfs-IComparer für byte[], da Array.Sort auf Span nicht direkt funktioniert.
/// </summary>
internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (x == null || y == null) return (x == null ? 0 : 1) - (y == null ? 0 : 1);
        return x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
