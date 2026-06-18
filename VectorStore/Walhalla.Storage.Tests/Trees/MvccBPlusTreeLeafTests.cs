// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeLeafTests : IDisposable
{
    private readonly string _tempPath;
    private readonly OdsPager _pager;
    private readonly MvccBPlusTree _tree;

    public MvccBPlusTreeLeafTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-test-" + Guid.NewGuid().ToString("N") + ".ods");
        _pager = new OdsPager(_tempPath, pageSize: 4096);
        _tree = new MvccBPlusTree(_pager, new TransactionManager());
    }

    public void Dispose()
    {
        _tree?.Dispose();
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void TryGetLatest_EmptyTree_ReturnsFalse()
    {
        bool found = _tree.TryGetLatest(new byte[] { 0x01 }, out var value);
        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void Upsert_ThenTryGetLatest_ReturnsValue()
    {
        var key = new byte[] { 0x01, 0x02 };
        var value = new byte[] { 0xAB, 0xCD };

        _tree.Upsert(1, key, value);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(value, latest);
    }

    [Fact]
    public void Upsert_Twice_TryGetLatest_ReturnsSecondValue()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.True(found);
        Assert.Equal(v2, latest);
    }

    [Fact]
    public void TryGetVisible_OldSnapshot_SeesFirstVersion()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);

        bool found = _tree.TryGetVisible(key, snapshotSeq: 1, out var visible);
        Assert.True(found);
        Assert.Equal(v1, visible);
    }

    [Fact]
    public void TryGetVisible_NewSnapshot_SeesLatestVersion()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);

        bool found = _tree.TryGetVisible(key, snapshotSeq: 2, out var visible);
        Assert.True(found);
        Assert.Equal(v2, visible);
    }

    [Fact]
    public void Delete_ThenTryGetLatest_ReturnsFalse()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);

        bool found = _tree.TryGetLatest(key, out var latest);
        Assert.False(found);
        Assert.Null(latest);
    }

    [Fact]
    public void Delete_ThenOldSnapshot_StillSeesVersion()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);

        bool found = _tree.TryGetVisible(key, snapshotSeq: 1, out var visible);
        Assert.True(found);
        Assert.Equal(value, visible);
    }

    [Fact]
    public void Delete_ThenSnapshotAfterDelete_SeesNothing()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);

        bool found = _tree.TryGetVisible(key, snapshotSeq: 2, out var visible);
        Assert.False(found);
    }

    [Fact]
    public void ScanVisible_Range_ReturnsMatchingKeys()
    {
        _tree.Upsert(1, new byte[] { 0x01 }, new byte[] { 0xA1 });
        _tree.Upsert(1, new byte[] { 0x02 }, new byte[] { 0xA2 });
        _tree.Upsert(1, new byte[] { 0x03 }, new byte[] { 0xA3 });

        var results = _tree.ScanVisible(
            snapshotSeq: 1,
            fromInclusive: new byte[] { 0x02 },
            toExclusive: new byte[] { 0x04 }).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(new byte[] { 0x02 }, results[0].Key);
        Assert.Equal(new byte[] { 0xA2 }, results[0].Value);
        Assert.Equal(new byte[] { 0x03 }, results[1].Key);
        Assert.Equal(new byte[] { 0xA3 }, results[1].Value);
    }

    [Fact]
    public void ScanVisible_WithDelete_OldSnapshotSeesDeletedKey()
    {
        var key = new byte[] { 0x02 };
        _tree.Upsert(1, key, new byte[] { 0xA2 });
        _tree.Delete(2, key);

        var results = _tree.ScanVisible(snapshotSeq: 1).ToList();
        Assert.Single(results);
        Assert.Equal(key, results[0].Key);
    }

    [Fact]
    public void ScanVisible_WithDelete_NewSnapshotDoesNotSeeDeletedKey()
    {
        var key = new byte[] { 0x02 };
        _tree.Upsert(1, key, new byte[] { 0xA2 });
        _tree.Delete(2, key);

        var results = _tree.ScanVisible(snapshotSeq: 2).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void Reopen_RestoresVersions()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);
        _tree.Checkpoint();

        // Dispose und neu öffnen
        _tree.Dispose();
        _pager.Dispose();

        var pager2 = new OdsPager(_tempPath, pageSize: 4096);
        var tree2 = new MvccBPlusTree(pager2, new TransactionManager());

        try
        {
            bool foundLatest = tree2.TryGetLatest(key, out var latest);
            Assert.True(foundLatest);
            Assert.Equal(v2, latest);

            bool foundOld = tree2.TryGetVisible(key, snapshotSeq: 1, out var old);
            Assert.True(foundOld);
            Assert.Equal(v1, old);
        }
        finally
        {
            tree2.Dispose();
            pager2.Dispose();
        }
    }

    [Fact]
    public void MultipleKeys_ScanReturnsSortedOrder()
    {
        _tree.Upsert(1, new byte[] { 0x03 }, new byte[] { 0xA3 });
        _tree.Upsert(1, new byte[] { 0x01 }, new byte[] { 0xA1 });
        _tree.Upsert(1, new byte[] { 0x02 }, new byte[] { 0xA2 });

        var results = _tree.ScanVisible(snapshotSeq: 1).ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(new byte[] { 0x01 }, results[0].Key);
        Assert.Equal(new byte[] { 0x02 }, results[1].Key);
        Assert.Equal(new byte[] { 0x03 }, results[2].Key);
    }

    [Fact]
    public void ScanPrefix_ReturnsMatchingKeys()
    {
        _tree.Upsert(1, new byte[] { 0x01, 0xAA }, new byte[] { 0xA1 });
        _tree.Upsert(1, new byte[] { 0x01, 0xBB }, new byte[] { 0xA2 });
        _tree.Upsert(1, new byte[] { 0x02, 0xAA }, new byte[] { 0xA3 });

        var results = _tree.ScanPrefixVisible(snapshotSeq: 1, prefix: new byte[] { 0x01 }).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(new byte[] { 0x01, 0xAA }, results[0].Key);
        Assert.Equal(new byte[] { 0x01, 0xBB }, results[1].Key);
    }

    [Fact]
    public void LargeInsert_SplitOccurs_AllKeysReadable()
    {
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            var value = new byte[] { (byte)(i % 256) };
            _tree.Upsert(1, key, value);
        }

        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            bool found = _tree.TryGetLatest(key, out var value);
            Assert.True(found, $"Key {i} not found");
            Assert.Equal(new byte[] { (byte)(i % 256) }, value);
        }
    }

    [Fact]
    public void LargeInsert_Reopen_AllKeysReadable()
    {
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            var value = new byte[] { (byte)(i % 256) };
            _tree.Upsert(1, key, value);
        }
        _tree.Checkpoint();

        _tree.Dispose();
        _pager.Dispose();

        var pager2 = new OdsPager(_tempPath, pageSize: 4096);
        var tree2 = new MvccBPlusTree(pager2, new TransactionManager());

        try
        {
            for (int i = 0; i < count; i++)
            {
                var key = new byte[] { (byte)(i >> 8), (byte)i };
                bool found = tree2.TryGetLatest(key, out var value);
                Assert.True(found, $"Key {i} not found after reopen");
                Assert.Equal(new byte[] { (byte)(i % 256) }, value);
            }
        }
        finally
        {
            tree2.Dispose();
            pager2.Dispose();
        }
    }

    [Fact]
    public void LargeInsert_ScanReturnsAllKeysSorted()
    {
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            // Big-endian 2-byte key für korrekte lexikografische Sortierung
            var key = new byte[] { (byte)(i >> 8), (byte)i };
            var value = new byte[] { (byte)(i % 256) };
            _tree.Upsert(1, key, value);
        }

        var results = _tree.ScanVisible(snapshotSeq: 1).ToList();
        Assert.Equal(count, results.Count);

        for (int i = 0; i < count; i++)
        {
            var expectedKey = new byte[] { (byte)(i >> 8), (byte)i };
            Assert.Equal(expectedKey, results[i].Key);
        }
    }
}
