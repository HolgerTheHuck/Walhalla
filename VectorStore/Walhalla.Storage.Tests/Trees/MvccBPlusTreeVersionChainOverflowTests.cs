// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeVersionChainOverflowTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _overflowPath;
    private readonly OdsPager _pager;
    private readonly OverflowStore _overflow;
    private readonly MvccBPlusTree _tree;
    private readonly TransactionManager _txManager;

    public MvccBPlusTreeVersionChainOverflowTests()
    {
        var guid = Guid.NewGuid().ToString("N");
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-vc-overflow-" + guid + ".ods");
        _overflowPath = Path.ChangeExtension(_tempPath, ".overflow");
        _txManager = new TransactionManager();
        _pager = new OdsPager(_tempPath, pageSize: 4096);
        _overflow = new OverflowStore(_overflowPath);
        _tree = new MvccBPlusTree(_pager, _txManager, overflowStore: _overflow, overflowThreshold: 256);
    }

    public void Dispose()
    {
        _tree?.Dispose();
        _overflow?.Dispose();
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
        if (File.Exists(_overflowPath))
            File.Delete(_overflowPath);
    }

    /// <summary>
    /// Advances the TransactionManager's global sequence so that CurrentSequence
    /// is at least <paramref name="sequence"/>.
    /// </summary>
    private void AdvanceSequenceBeyond(ulong sequence)
    {
        while (_txManager.CurrentSequence < sequence)
        {
            _txManager.AcquireCommitSequence();
        }
    }

    [Fact]
    public void ManyUpdates_SameKey_AllVersionsReadable()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            var value = new byte[] { (byte)(seq % 256) };
            _tree.Upsert(seq, key, value);
        }

        // Jede Version muss via Snapshot lesbar sein
        for (ulong snap = 1; snap <= (ulong)updates; snap++)
        {
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found, $"Snapshot {snap} should see a version");
            Assert.Equal(new byte[] { (byte)(snap % 256) }, visible);
        }

        // Neueste Version
        bool foundLatest = _tree.TryGetLatest(key, out var latest);
        Assert.True(foundLatest);
        Assert.Equal(new byte[] { (byte)(updates % 256) }, latest);
    }

    [Fact]
    public void ManyUpdates_LargeValues_OverflowChainWorks()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            var value = new byte[512]; // > overflowThreshold → OverflowStore für Value
            value[0] = (byte)(seq % 256);
            _tree.Upsert(seq, key, value);
        }

        // Neueste Version prüfen
        bool foundLatest = _tree.TryGetLatest(key, out var latest);
        Assert.True(foundLatest);
        Assert.NotNull(latest);
        Assert.Equal(512, latest!.Length);
        Assert.Equal((byte)(updates % 256), latest[0]);

        // Einige Snapshots stichprobenartig prüfen
        for (ulong snap = 1; snap <= (ulong)updates; snap += 50)
        {
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found, $"Snapshot {snap} should see a version");
            Assert.NotNull(visible);
            Assert.Equal(512, visible!.Length);
            Assert.Equal((byte)(snap % 256), visible[0]);
        }
    }

    [Fact]
    public void Scan_AfterOverflow_ReturnsCorrectVersions()
    {
        // Viele verschiedene Keys + einen Key oft updaten
        for (int i = 0; i < 100; i++)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            _tree.Upsert(1, key, new byte[] { (byte)i });
        }

        var hotKey = new byte[] { 0xFF, 0xFF };
        for (ulong seq = 1; seq <= 150; seq++)
        {
            _tree.Upsert(seq, hotKey, new byte[] { (byte)(seq % 256) });
        }

        // Scan über alle Keys
        var results = _tree.ScanVisible(snapshotSeq: 150).ToList();
        Assert.Equal(101, results.Count); // 100 + 1 hotKey

        // HotKey muss die richtige Version zeigen
        var hotResult = results.First(r => r.Key.AsSpan().SequenceEqual(hotKey));
        Assert.Equal(new byte[] { (byte)(150 % 256) }, hotResult.Value);
    }

    [Fact]
    public void Vacuum_TrimsOverflowChain()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }
        AdvanceSequenceBeyond((ulong)updates);

        // Vacuum ohne aktive Snapshots → nur neueste Version bleibt
        _tree.Vacuum();

        // Neueste Version noch lesbar
        bool foundLatest = _tree.TryGetLatest(key, out var latest);
        Assert.True(foundLatest);
        Assert.Equal(new byte[] { (byte)(updates % 256) }, latest);

        // Alte Snapshots sehen nichts mehr
        bool foundOld = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
        Assert.False(foundOld);
    }

    [Fact]
    public void Vacuum_WithActiveSnapshot_KeepsVisibleVersions()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }
        AdvanceSequenceBeyond((ulong)updates);

        // Aktiven Snapshot halten, damit Vacuum nicht alles pruned
        ulong snap = _txManager.AcquireSnapshot();
        try
        {
            _tree.Vacuum();

            // Der aktive Snapshot sieht die neueste Version
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found);
            Assert.Equal(new byte[] { (byte)(updates % 256) }, visible);

            // Neueste Version noch lesbar
            bool foundLatest = _tree.TryGetLatest(key, out var latest);
            Assert.True(foundLatest);
            Assert.Equal(new byte[] { (byte)(updates % 256) }, latest);

            // Alte Snapshots (ohne aktiven Halter) wurden geprunt
            bool foundOld = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
            Assert.False(foundOld);
        }
        finally
        {
            _txManager.ReleaseSnapshot(snap);
        }
    }

    [Fact]
    public void Reopen_OverflowChainSurvives()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }
        _tree.Checkpoint();

        // Schließen und neu öffnen
        _tree.Dispose();
        _overflow.Dispose();
        _pager.Dispose();

        using var pager2 = new OdsPager(_tempPath, pageSize: 4096);
        using var overflow2 = new OverflowStore(_overflowPath);
        var txManager2 = new TransactionManager();
        // TxManager muss auf die höchste Sequenz synchronisiert werden
        txManager2.AdvanceTo((ulong)updates);
        using var tree2 = new MvccBPlusTree(pager2, txManager2, overflowStore: overflow2, overflowThreshold: 256);

        // Alle Snapshots stichprobenartig prüfen
        for (ulong snap = 1; snap <= (ulong)updates; snap += 50)
        {
            bool found = tree2.TryGetVisible(key, snap, out var visible);
            Assert.True(found, $"Snapshot {snap} should see a version after reopen");
            Assert.Equal(new byte[] { (byte)(snap % 256) }, visible);
        }

        // Neueste Version
        bool foundLatest = tree2.TryGetLatest(key, out var latest);
        Assert.True(foundLatest);
        Assert.Equal(new byte[] { (byte)(updates % 256) }, latest);
    }

    [Fact]
    public void Delete_CreatesTombstoneInOverflowChain()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }

        // Delete auf den Key
        _tree.Delete((ulong)(updates + 1), key);

        // Neueste Version ist Tombstone → TryGetLatest gibt false
        bool foundLatest = _tree.TryGetLatest(key, out _);
        Assert.False(foundLatest);

        // Alte Snapshots sehen noch die Werte
        for (ulong snap = 1; snap <= (ulong)updates; snap += 50)
        {
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found, $"Snapshot {snap} should still see a version");
            Assert.Equal(new byte[] { (byte)(snap % 256) }, visible);
        }

        // Snapshot nach Delete sieht nichts
        bool foundAfterDelete = _tree.TryGetVisible(key, (ulong)(updates + 1), out _);
        Assert.False(foundAfterDelete);
    }

    [Fact]
    public void Delete_ThenVacuum_RemovesOverflowChain()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }
        _tree.Delete((ulong)(updates + 1), key);
        AdvanceSequenceBeyond((ulong)(updates + 1));

        _tree.Vacuum();

        // Key sollte komplett entfernt sein
        bool found = _tree.TryGetLatest(key, out _);
        Assert.False(found);

        // Auch alte Snapshots sehen nichts mehr
        bool foundOld = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
        Assert.False(foundOld);
    }

    [Fact]
    public void MultipleHotKeys_AllOverflowChainsWork()
    {
        const int hotKeys = 10;
        const int updates = 100;

        for (int k = 0; k < hotKeys; k++)
        {
            var key = new byte[] { (byte)k };
            for (ulong seq = 1; seq <= (ulong)updates; seq++)
            {
                _tree.Upsert(seq, key, new byte[] { (byte)k, (byte)(seq % 256) });
            }
        }

        // Alle Keys prüfen
        for (int k = 0; k < hotKeys; k++)
        {
            var key = new byte[] { (byte)k };
            bool found = _tree.TryGetLatest(key, out var latest);
            Assert.True(found, $"Key {k} should exist");
            Assert.Equal(new byte[] { (byte)k, (byte)(updates % 256) }, latest);

            // Stichprobe: Snapshot 50
            bool foundSnap = _tree.TryGetVisible(key, 50, out var snap50);
            Assert.True(foundSnap);
            Assert.Equal(new byte[] { (byte)k, (byte)(50 % 256) }, snap50);
        }
    }

    [Fact]
    public void ScanPrefix_WithOverflowChains_ReturnsCorrectResults()
    {
        // Keys mit gleichem Prefix
        var prefix = new byte[] { 0xAA };
        for (int i = 0; i < 5; i++)
        {
            var key = new byte[] { 0xAA, (byte)i };
            for (ulong seq = 1; seq <= 100; seq++)
            {
                _tree.Upsert(seq, key, new byte[] { (byte)i, (byte)(seq % 256) });
            }
        }

        // Keys mit anderem Prefix
        for (int i = 0; i < 10; i++)
        {
            var key = new byte[] { 0xBB, (byte)i };
            _tree.Upsert(1, key, new byte[] { (byte)i });
        }

        var results = _tree.ScanPrefixVisible(snapshotSeq: 100, prefix).ToList();
        Assert.Equal(5, results.Count);

        for (int i = 0; i < 5; i++)
        {
            var expectedKey = new byte[] { 0xAA, (byte)i };
            Assert.Contains(results, r => r.Key.AsSpan().SequenceEqual(expectedKey));
        }
    }

    [Fact]
    public void Vacuum_CompactsOverflowChain_WhenPrunedSmall()
    {
        var key = new byte[] { 0x01 };
        const int updates = 200;

        for (ulong seq = 1; seq <= (ulong)updates; seq++)
        {
            _tree.Upsert(seq, key, new byte[] { (byte)(seq % 256) });
        }
        AdvanceSequenceBeyond((ulong)updates);

        // Aktiven Snapshot halten, damit Vacuum nicht alles pruned
        ulong snap = _txManager.AcquireSnapshot();
        try
        {
            _tree.Vacuum();

            // Der aktive Snapshot sieht die neueste Version
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found);
            Assert.Equal(new byte[] { (byte)(updates % 256) }, visible);

            // Neueste Version noch lesbar
            bool foundLatest = _tree.TryGetLatest(key, out var latest);
            Assert.True(foundLatest);
            Assert.Equal(new byte[] { (byte)(updates % 256) }, latest);

            // Nach Release des Snapshots und erneutem Vacuum ist nur
            // noch die neueste Version da
        }
        finally
        {
            _txManager.ReleaseSnapshot(snap);
        }

        // Zweites Vacuum ohne aktive Snapshots → alles außer neuester Version wird geprunt
        AdvanceSequenceBeyond((ulong)(updates + 1));
        _tree.Vacuum();

        bool foundLatest2 = _tree.TryGetLatest(key, out var latest2);
        Assert.True(foundLatest2);
        Assert.Equal(new byte[] { (byte)(updates % 256) }, latest2);

        // Alte Snapshots sehen nichts mehr
        bool foundOld = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
        Assert.False(foundOld);
    }
}
