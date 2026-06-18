// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Mvcc.Transactions;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeScanTests : IDisposable
{
    private readonly string _tempPath;
    private readonly OdsPager _pager;
    private readonly MvccBPlusTree _tree;

    public MvccBPlusTreeScanTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-scan-test-" + Guid.NewGuid().ToString("N") + ".ods");
        _pager = new OdsPager(_tempPath, pageSize: 4096);
        _tree = new MvccBPlusTree(_pager, new TransactionManager());
    }

    public void Dispose()
    {
        _tree?.Dispose();
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
        var overflowPath = Path.ChangeExtension(_tempPath, ".overflow");
        if (File.Exists(overflowPath))
            File.Delete(overflowPath);
    }

    [Fact]
    public void ScanVisible_EmptyTree_ReturnsNothing()
    {
        var results = _tree.ScanVisible(snapshotSeq: 1).ToList();
        Assert.Empty(results);
    }

    [Fact]
    public void ScanVisible_SinglePage_ReturnsAllInOrder()
    {
        var keys = new[]
        {
            new byte[] { 0x01 },
            new byte[] { 0x02 },
            new byte[] { 0x03 },
        };
        var values = new[]
        {
            new byte[] { 0xAA },
            new byte[] { 0xBB },
            new byte[] { 0xCC },
        };

        for (int i = 0; i < keys.Length; i++)
            _tree.Upsert((ulong)(i + 1), keys[i], values[i]);

        var results = _tree.ScanVisible(snapshotSeq: 3).ToList();

        Assert.Equal(3, results.Count);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(keys[i], results[i].Key);
            Assert.Equal(values[i], results[i].Value);
        }
    }

    [Fact]
    public void ScanVisible_MultiPage_ReturnsAllInOrder()
    {
        // Genug Keys, um mehrere Leaf-Pages zu füllen (Order=128, Page=4096)
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i / 256), (byte)(i % 256) };
            var value = new byte[] { (byte)i };
            _tree.Upsert((ulong)(i + 1), key, value);
        }

        var results = _tree.ScanVisible(snapshotSeq: (ulong)count).ToList();

        Assert.Equal(count, results.Count);
        for (int i = 0; i < count; i++)
        {
            var expectedKey = new byte[] { (byte)(i / 256), (byte)(i % 256) };
            Assert.Equal(expectedKey, results[i].Key);
            Assert.Equal(new byte[] { (byte)i }, results[i].Value);
        }
    }

    [Fact]
    public void ScanVisible_RangeScan_ReturnsOnlyInRange()
    {
        const int count = 200;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)i };
            _tree.Upsert((ulong)(i + 1), key, key);
        }

        var fromInclusive = new byte[] { 50 };
        var toExclusive = new byte[] { 60 };

        var results = _tree.ScanVisible(snapshotSeq: (ulong)count, fromInclusive, toExclusive).ToList();

        Assert.Equal(10, results.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(new byte[] { (byte)(50 + i) }, results[i].Key);
        }
    }

    [Fact]
    public void ScanVisible_SnapshotIsolation_SeesOldVersion()
    {
        var key = new byte[] { 0x01 };
        var v1 = new byte[] { 0xAA };
        var v2 = new byte[] { 0xBB };

        _tree.Upsert(1, key, v1);
        _tree.Upsert(2, key, v2);

        // Snapshot @1 sieht nur v1 (neueste Version <= 1)
        var results = _tree.ScanVisible(snapshotSeq: 1, fromInclusive: key, toExclusive: new byte[] { 0x02 }).ToList();

        Assert.Single(results);
        Assert.Equal(v1, results[0].Value);
    }

    [Fact]
    public void ScanPrefixVisible_ReturnsMatchingKeys()
    {
        var prefix = new byte[] { 0xAB };

        // Matching keys
        _tree.Upsert(1, new byte[] { 0xAB, 0x01 }, new byte[] { 0x11 });
        _tree.Upsert(2, new byte[] { 0xAB, 0x02 }, new byte[] { 0x22 });

        // Non-matching keys
        _tree.Upsert(3, new byte[] { 0xAC, 0x01 }, new byte[] { 0x33 });
        _tree.Upsert(4, new byte[] { 0xAA, 0x01 }, new byte[] { 0x44 });

        var results = _tree.ScanPrefixVisible(snapshotSeq: 4, prefix).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(new byte[] { 0xAB, 0x01 }, results[0].Key);
        Assert.Equal(new byte[] { 0xAB, 0x02 }, results[1].Key);
    }

    [Fact]
    public void ScanVisible_RangeDoesNotTouchAllLeaves()
    {
        // Insert many keys to create a multi-page tree
        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            var key = new byte[] { (byte)(i / 256), (byte)(i % 256) };
            _tree.Upsert((ulong)(i + 1), key, new byte[] { (byte)i });
        }

        // Range that only touches a small subset
        var fromInclusive = new byte[] { 0, 10 };  // Key 10
        var toExclusive = new byte[] { 0, 20 };    // Key 20

        var results = _tree.ScanVisible(snapshotSeq: (ulong)count, fromInclusive, toExclusive).ToList();

        Assert.Equal(10, results.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(new byte[] { 0, (byte)(10 + i) }, results[i].Key);
        }
    }

    [Fact]
    public void ScanVisible_DeletedKey_NotIncluded()
    {
        var key = new byte[] { 0x01 };
        var value = new byte[] { 0xAA };

        _tree.Upsert(1, key, value);
        _tree.Delete(2, key);

        var results = _tree.ScanVisible(snapshotSeq: 2).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void ScanVisible_MixedVisibleAndDeleted()
    {
        var key1 = new byte[] { 0x01 };
        var key2 = new byte[] { 0x02 };
        var key3 = new byte[] { 0x03 };

        _tree.Upsert(1, key1, new byte[] { 0xAA });
        _tree.Upsert(2, key2, new byte[] { 0xBB });
        _tree.Upsert(3, key3, new byte[] { 0xCC });
        _tree.Delete(4, key2);

        var results = _tree.ScanVisible(snapshotSeq: 4).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(key1, results[0].Key);
        Assert.Equal(key3, results[1].Key);
    }
}
