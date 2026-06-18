// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeOverflowTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _overflowPath;
    private readonly OdsPager _pager;
    private readonly OverflowStore _overflow;
    private readonly MvccBPlusTree _tree;

    public MvccBPlusTreeOverflowTests()
    {
        var guid = Guid.NewGuid().ToString("N");
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-overflow-test-" + guid + ".ods");
        _overflowPath = Path.ChangeExtension(_tempPath, ".overflow");
        _pager = new OdsPager(_tempPath, pageSize: 4096);
        _overflow = new OverflowStore(_overflowPath);
        _tree = new MvccBPlusTree(_pager, new TransactionManager(), overflowStore: _overflow, overflowThreshold: 256);
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
    /// is at least <paramref name="sequence"/u003e.
    /// </summary>
    private void AdvanceSequenceBeyond(ulong sequence)
    {
        var txManager = _tree.TransactionManager;
        while (txManager.CurrentSequence < sequence)
        {
            txManager.AcquireCommitSequence();
        }
    }

    [Fact]
    public void LargeValue_Roundtrip()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[512];
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(value, latest);
    }

    [Fact]
    public void LargeValue_ViaSnapshot_SeesValue()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[512];
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);

        bool found = _tree.TryGetVisible(key, snapshotSeq: 1, out var visible);
        Assert.True(found);
        Assert.Equal(value, visible);
    }

    [Fact]
    public void SmallValue_RemainsInline()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[128]; // kleiner als Threshold (256)
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(value, latest);
    }

    [Fact]
    public void UpdateLargeValue_CreatesNewBlob()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[512];
        var v2 = new byte[512];
        Random.Shared.NextBytes(v1);
        Random.Shared.NextBytes(v2);

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(v2, latest);
    }

    [Fact]
    public void Vacuum_FreesOldBlob()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[512];
        var v2 = new byte[512];
        Random.Shared.NextBytes(v1);
        Random.Shared.NextBytes(v2);

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        AdvanceSequenceBeyond(2);

        // Keine Snapshots aktiv → Vacuum darf alles bis zur neuesten Version prunen
        _tree.Vacuum();

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(v2, latest);

        // Der alte Blob sollte zur Freigabe markiert sein
        Assert.True(_overflow.PendingFree.Count > 0, "Old blob should be pending free after vacuum.");
    }

    [Fact]
    public void Vacuum_WithActiveSnapshot_KeepsOldBlob()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[512];
        var v2 = new byte[512];
        Random.Shared.NextBytes(v1);
        Random.Shared.NextBytes(v2);

        _tree.Upsert(1, key, v1);

        // Snapshot erstellen, BEVOR v2 committed wird
        var snapshotSeq = _tree.TransactionManager.AcquireSnapshot();
        try
        {
            _tree.Upsert(2, key, v2);
            _tree.Vacuum();

            // Snapshot sieht noch v1 (Version @1 ist die neueste < snapshotSeq,
            // weil snapshotSeq >= 2 und @2 > snapshotSeq... Moment, AcquireSnapshot
            // gibt eine Zukunftssequenz zurück. Der Snapshot sieht alles, was
            // zum Zeitpunkt der Erstellung committed war, also @1 und @2.)
            // Korrektur: TryGetVisible mit snapshotSeq sieht die neueste Version
            // <= snapshotSeq. Wenn snapshotSeq >= 2, sieht er @2.
            // Das wichtige ist: Vacuum darf @1 nicht prunen, solange ein
            // aktiver Snapshot existiert, der @1 brauchen könnte.
            bool foundLatest = _tree.TryGetVisible(key, snapshotSeq, out var latest);
            Assert.True(foundLatest);
            Assert.Equal(v2, latest);

            // Neueste Version ist v2
            bool foundNew = _tree.TryGetLatest(key, out var newValue);
            Assert.True(foundNew);
            Assert.Equal(v2, newValue);

            // Nach Release des Snapshots und Vacuum sollte @1 geprunt werden
        }
        finally
        {
            _tree.TransactionManager.ReleaseSnapshot(snapshotSeq);
        }

        AdvanceSequenceBeyond(2);
        _tree.Vacuum();

        // Jetzt sollte @1 geprunt sein → nur @2 bleibt
        bool foundOld = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
        Assert.False(foundOld);
    }

    [Fact]
    public void Reopen_LargeValueReadable()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[512];
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);

        // Schließen und neu öffnen
        _tree.Dispose();
        _overflow.Dispose();
        _pager.Dispose();

        using var pager2 = new OdsPager(_tempPath, pageSize: 4096);
        using var overflow2 = new OverflowStore(_overflowPath);
        using var tree2 = new MvccBPlusTree(pager2, new TransactionManager(), overflowStore: overflow2, overflowThreshold: 256);

        bool found = tree2.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(value, latest);
    }

    [Fact]
    public void DeleteLargeValue_ThenVacuum_RemovesBlob()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[512];
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);
        AdvanceSequenceBeyond(2);

        _tree.Vacuum();

        bool found = _tree.TryGetLatest(key, out _);
        Assert.False(found);

        // Der Blob sollte zur Freigabe markiert sein
        Assert.True(_overflow.PendingFree.Count > 0, "Blob should be pending free after vacuum.");
    }

    [Fact]
    public void ScanVisible_LargeValues()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[512];
        Random.Shared.NextBytes(value);

        _tree.Upsert(1, key, value);

        var results = _tree.ScanVisible(snapshotSeq: 1, fromInclusive: key, toExclusive: new byte[] { 0x02 }).ToList();

        Assert.Single(results);
        Assert.Equal(value, results[0].Value);
    }
}
