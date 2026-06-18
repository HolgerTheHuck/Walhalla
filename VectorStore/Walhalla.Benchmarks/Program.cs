// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Running;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Walhalla.Storage.Ods.Tree;
using Walhalla.Storage.Trees;

namespace Walhalla.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--vector")
        {
            using var comparison = new ComparisonBenchmark();
            comparison.RunAll().Wait();
            return;
        }

        if (args.Length > 0 && args[0] == "--tree")
        {
            RunQuickTreeBenchmark();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    static void RunQuickTreeBenchmark()
    {
        const int N = 10_000;
        const int KeySize = 16;
        const int ValueSize = 128;
        const int PageSize = 4096;

        Console.WriteLine("========================================");
        Console.WriteLine("Quick BPlusTree vs MvccBPlusTree Benchmark");
        Console.WriteLine($"N={N}, KeySize={KeySize}, ValueSize={ValueSize}");
        Console.WriteLine("========================================\n");

        var random = new Random(42);
        var keys = new byte[N][];
        var values = new byte[N][];
        for (int i = 0; i < N; i++)
        {
            keys[i] = new byte[KeySize];
            random.NextBytes(keys[i]);
            values[i] = new byte[ValueSize];
            random.NextBytes(values[i]);
        }

        // ── Insertion ───────────────────────────────────────────────────────────
        {
            var classicPath = Path.Combine(Path.GetTempPath(), $"walhalla_classic_insert_{Guid.NewGuid():N}.ods");
            var mvccPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_insert_{Guid.NewGuid():N}.ods");

            // Classic
            var cp = new OdsPager(classicPath, PageSize, pageCacheCapacity: 1024);
            var ct = new BPlusTree(cp, BuiltInKeyComparators.Bytewise);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) ct.Upsert(keys[i], values[i]);
            sw.Stop();
            var classicInsert = sw.ElapsedMilliseconds;
            ct.Dispose(); cp.Dispose();

            // MVCC
            var txm = new TransactionManager();
            var mp = new OdsPager(mvccPath, PageSize, pageCacheCapacity: 1024);
            var mt = new MvccBPlusTree(mp, txm, BuiltInKeyComparators.Bytewise, order: 128);
            sw.Restart();
            for (int i = 0; i < N; i++)
            {
                var seq = txm.AcquireCommitSequence();
                mt.Upsert(seq, keys[i], values[i]);
            }
            sw.Stop();
            var mvccInsert = sw.ElapsedMilliseconds;
            mt.Dispose(); mp.Dispose();

            Console.WriteLine($"Insertion:");
            Console.WriteLine($"  Classic: {classicInsert,6} ms  ({N / (classicInsert / 1000.0):F0} ops/s)");
            Console.WriteLine($"  MVCC:    {mvccInsert,6} ms  ({N / (mvccInsert / 1000.0):F0} ops/s)");
            Console.WriteLine($"  Ratio:   {(double)mvccInsert / classicInsert:F2}x\n");

            File.Delete(classicPath);
            File.Delete(mvccPath);
        }

        // ── Point Lookup ────────────────────────────────────────────────────────
        {
            var classicPath = Path.Combine(Path.GetTempPath(), $"walhalla_classic_lookup_{Guid.NewGuid():N}.ods");
            var mvccPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_lookup_{Guid.NewGuid():N}.ods");

            // Classic: insert + lookup
            var cp = new OdsPager(classicPath, PageSize, pageCacheCapacity: 1024);
            var ct = new BPlusTree(cp, BuiltInKeyComparators.Bytewise);
            for (int i = 0; i < N; i++) ct.Upsert(keys[i], values[i]);
            var sw = Stopwatch.StartNew();
            int foundC = 0;
            for (int i = 0; i < N; i++) if (ct.TryGet(keys[i], out _)) foundC++;
            sw.Stop();
            var classicLookup = sw.ElapsedMilliseconds;
            ct.Dispose(); cp.Dispose();

            // MVCC: insert + lookup
            var txm = new TransactionManager();
            var mp = new OdsPager(mvccPath, PageSize, pageCacheCapacity: 1024);
            var mt = new MvccBPlusTree(mp, txm, BuiltInKeyComparators.Bytewise, order: 128);
            for (int i = 0; i < N; i++)
            {
                var seq = txm.AcquireCommitSequence();
                mt.Upsert(seq, keys[i], values[i]);
            }
            sw.Restart();
            int foundM = 0;
            for (int i = 0; i < N; i++) if (mt.TryGetLatest(keys[i], out _)) foundM++;
            sw.Stop();
            var mvccLookup = sw.ElapsedMilliseconds;
            mt.Dispose(); mp.Dispose();

            Console.WriteLine($"Point Lookup ({foundC}/{N} found):");
            Console.WriteLine($"  Classic: {classicLookup,6} ms  ({N / (classicLookup / 1000.0):F0} ops/s)");
            Console.WriteLine($"  MVCC:    {mvccLookup,6} ms  ({N / (mvccLookup / 1000.0):F0} ops/s)");
            Console.WriteLine($"  Ratio:   {(double)mvccLookup / classicLookup:F2}x\n");

            File.Delete(classicPath);
            File.Delete(mvccPath);
        }

        // ── Range Scan (1 %) ────────────────────────────────────────────────────
        {
            var classicPath = Path.Combine(Path.GetTempPath(), $"walhalla_classic_scan_{Guid.NewGuid():N}.ods");
            var mvccPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_scan_{Guid.NewGuid():N}.ods");

            // Classic: insert + scan
            var cp = new OdsPager(classicPath, PageSize, pageCacheCapacity: 1024);
            var ct = new BPlusTree(cp, BuiltInKeyComparators.Bytewise);
            for (int i = 0; i < N; i++) ct.Upsert(keys[i], values[i]);
            var sorted = keys.OrderBy(k => k, ByteArrayComparer.Instance).ToList();
            var fromKey = sorted[0];
            var toKey = sorted[Math.Max(1, N / 100) - 1];
            var sw = Stopwatch.StartNew();
            int scannedC = 0;
            foreach (var _ in ct.EnumerateRange(fromKey, toKey)) scannedC++;
            sw.Stop();
            var classicScan = sw.ElapsedMilliseconds;
            ct.Dispose(); cp.Dispose();

            // MVCC: insert + scan
            var txm = new TransactionManager();
            var mp = new OdsPager(mvccPath, PageSize, pageCacheCapacity: 1024);
            var mt = new MvccBPlusTree(mp, txm, BuiltInKeyComparators.Bytewise, order: 128);
            for (int i = 0; i < N; i++)
            {
                var seq = txm.AcquireCommitSequence();
                mt.Upsert(seq, keys[i], values[i]);
            }
            var snapshot = txm.AcquireSnapshot();
            sw.Restart();
            int scannedM = 0;
            foreach (var _ in mt.ScanVisible(snapshot, fromKey, toKey)) scannedM++;
            sw.Stop();
            var mvccScan = sw.ElapsedMilliseconds;
            mt.Dispose(); mp.Dispose();

            Console.WriteLine($"Range Scan ({scannedC} / {scannedM} items):");
            Console.WriteLine($"  Classic: {classicScan,6} ms");
            Console.WriteLine($"  MVCC:    {mvccScan,6} ms");
            Console.WriteLine($"  Ratio:   {(double)mvccScan / classicScan:F2}x\n");

            File.Delete(classicPath);
            File.Delete(mvccPath);
        }

        Console.WriteLine("========================================");
        Console.WriteLine("Benchmark complete.");
        Console.WriteLine("========================================");
    }
}
