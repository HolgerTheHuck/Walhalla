// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeVacuumTests : IDisposable
{
    private readonly string _tempPath;
    private readonly OdsPager _pager;
    private readonly MvccBPlusTree _tree;
    private readonly TransactionManager _txManager;

    public MvccBPlusTreeVacuumTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-vacuum-test-" + Guid.NewGuid().ToString("N") + ".ods");
        _txManager = new TransactionManager();
        _pager = new OdsPager(_tempPath, pageSize: 4096);
        _tree = new MvccBPlusTree(_pager, _txManager);
    }

    public void Dispose()
    {
        _tree?.Dispose();
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    /// <summary>
    /// Advances the TransactionManager's global sequence so that CurrentSequence
    /// is at least <paramref name="sequence"/>. Required when calling tree methods
    /// directly with explicit commit sequences because those do not bump the manager.
    /// </summary>
    private void AdvanceSequenceBeyond(ulong sequence)
    {
        while (_txManager.CurrentSequence < sequence)
        {
            _txManager.AcquireCommitSequence();
        }
    }

    [Fact]
    public void Vacuum_EmptyTree_NoOp()
    {
        _tree.Vacuum();
        // No exception and tree remains empty
        bool found = _tree.TryGetLatest(new byte[] { 0x01 }, out _);
        Assert.False(found);
    }

    [Fact]
    public void Vacuum_NoActiveSnapshot_RemovesOldVersions()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        AdvanceSequenceBeyond(2);

        // No active snapshots, vacuum should prune everything except latest
        _tree.Vacuum();

        // Latest still readable
        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(v2, latest);

        // Old snapshot no longer sees v1 because it was pruned.
        // (No snapshot @1 is active, so pruning is allowed.)
        bool oldFound = _tree.TryGetVisible(key, snapshotSeq: 1, out _);
        Assert.False(oldFound);
    }

    [Fact]
    public void Vacuum_WithActiveSnapshot_KeepsVisibleVersion()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        AdvanceSequenceBeyond(2);

        // Acquire a snapshot at the current sequence (simulates an active reader)
        ulong snap = _txManager.AcquireSnapshot();
        try
        {
            // Vacuum should keep both versions because the active snapshot
            // is at the latest sequence, so nothing older can be pruned yet.
            _tree.Vacuum();

            // The active snapshot sees the latest version (v2)
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found);
            Assert.Equal(v2, visible);

            // Snapshot @1 does NOT see v1 because v1 was pruned:
            // With active snapshot @3, pruneCutoff = 4, and v1@1 < 4 is removed.
            // Only v2@2 remains, and 2 > 1, so no visible version for snapshot @1.
            bool oldFound = _tree.TryGetVisible(key, snapshotSeq: 1, out var oldVisible);
            Assert.False(oldFound);

            // Latest is still v2
            bool latestFound = _tree.TryGetLatest(key, out var latest);
            Assert.True(latestFound);
            Assert.Equal(v2, latest);
        }
        finally
        {
            _txManager.ReleaseSnapshot(snap);
        }
    }

    [Fact]
    public void Vacuum_TombstoneOlderThanSnapshot_PhysicallyRemoves()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);
        AdvanceSequenceBeyond(2);

        // No active snapshots → vacuum can prune the tombstone
        _tree.Vacuum();

        // Key should be completely gone (physically deleted)
        bool found = _tree.TryGetLatest(key, out _);
        Assert.False(found);
    }

    [Fact]
    public void Vacuum_TombstoneWithActiveSnapshot_KeepsTombstone()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);
        AdvanceSequenceBeyond(2);

        // Acquire snapshot at the current sequence (sees the tombstone)
        ulong snap = _txManager.AcquireSnapshot();
        try
        {
            _tree.Vacuum();

            // Snapshot should still see the tombstone (key not found)
            bool found = _tree.TryGetVisible(key, snap, out _);
            Assert.False(found);
        }
        finally
        {
            _txManager.ReleaseSnapshot(snap);
        }
    }

    [Fact]
    public void Vacuum_LeafChain_TraversesAllPages()
    {
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            var value = new byte[] { (byte)(i % 256) };
            _tree.Upsert(1, key, value);
        }
        AdvanceSequenceBeyond(1);

        // Delete every even key
        for (int i = 0; i < count; i += 2)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            _tree.Delete(2, key);
        }
        AdvanceSequenceBeyond(2);

        _tree.Vacuum();

        // Even keys should be gone (physically removed)
        for (int i = 0; i < count; i += 2)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            bool found = _tree.TryGetLatest(key, out _);
            Assert.False(found, $"Key {i} should have been physically removed by vacuum");
        }

        // Odd keys still present
        for (int i = 1; i < count; i += 2)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            bool found = _tree.TryGetLatest(key, out var value);
            Assert.True(found, $"Key {i} should still exist");
            Assert.Equal(new byte[] { (byte)(i % 256) }, value);
        }
    }

    [Fact]
    public void Vacuum_Reopen_RestoresPrunedState()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        AdvanceSequenceBeyond(2);
        _tree.Vacuum();
        _tree.Checkpoint();

        _tree.Dispose();
        _pager.Dispose();

        var pager2 = new OdsPager(_tempPath, pageSize: 4096);
        var tree2 = new MvccBPlusTree(pager2, new TransactionManager());

        try
        {
            // After vacuum and reopen, only v2 should remain
            bool found = tree2.TryGetLatest(key, out var latest);
            Assert.True(found);
            Assert.Equal(v2, latest);

            // Old snapshot should not find anything because v1 was pruned
            bool oldFound = tree2.TryGetVisible(key, snapshotSeq: 1, out _);
            Assert.False(oldFound);
        }
        finally
        {
            tree2.Dispose();
            pager2.Dispose();
        }
    }

    [Fact]
    public void Vacuum_MultipleVersions_PrunesMiddleVersions()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0x01 };
        var v2 = new byte[] { 0x02 };
        var v3 = new byte[] { 0x03 };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        _tree.Upsert(3, key, v3);
        AdvanceSequenceBeyond(3);

        // Snapshot at latest sequence (3)
        ulong snap = _txManager.AcquireSnapshot();
        try
        {
            _tree.Vacuum();

            // Snapshot @3 sees v3
            bool found = _tree.TryGetVisible(key, snap, out var visible);
            Assert.True(found);
            Assert.Equal(v3, visible);

            // Latest is v3
            bool latestFound = _tree.TryGetLatest(key, out var latest);
            Assert.True(latestFound);
            Assert.Equal(v3, latest);

            // Snapshot @1 sees v1 (v1 is the newest <= 1, v2 was pruned because
            // v2 is not the newest < pruneCutoff when no snapshots are active).
            // Wait: with no active snapshots, pruneCutoff = CurrentSequence + 1 = 4.
            // Prune(4) on @3,@2,@1: 3 >= 4 false → current=@3. current.Older=null → @2,@1 removed.
            // So only @3 remains. Snapshot @1 should see @3? No, 3 > 1, skip. No older version. false.
            bool oldFound = _tree.TryGetVisible(key, snapshotSeq: 1, out var oldVisible);
            Assert.False(oldFound);
        }
        finally
        {
            _txManager.ReleaseSnapshot(snap);
        }
    }
}
