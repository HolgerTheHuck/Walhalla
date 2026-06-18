// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using Walhalla.Storage.Core.Comparers;
using Walhalla.Storage.Ods.Paging;
using Xunit;

namespace Walhalla.Storage.Trees.Tests;

public class MvccBPlusTreeRecoveryTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _walPath;

    public MvccBPlusTreeRecoveryTests()
    {
        var guid = Guid.NewGuid().ToString("N");
        _tempPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-recovery-test-" + guid + ".ods");
        _walPath = Path.ChangeExtension(_tempPath, ".wal");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
        if (File.Exists(_walPath))
            File.Delete(_walPath);
        var overflowPath = Path.ChangeExtension(_tempPath, ".overflow");
        if (File.Exists(overflowPath))
            File.Delete(overflowPath);
    }

    private MvccBPlusTreeStore CreateStore()
    {
        var pager = new OdsPager(_tempPath, pageSize: 4096);
        return new MvccBPlusTreeStore(pager, walPath: _walPath);
    }

    [Fact]
    public void Recovery_WalReplay_RestoresCommits()
    {
        // Phase 1: Daten schreiben
        using (var store = CreateStore())
        {
            store.Upsert(new byte[] { 0x01 }, new byte[] { 0xAA });
            store.Upsert(new byte[] { 0x02 }, new byte[] { 0xBB });
            store.Upsert(new byte[] { 0x03 }, new byte[] { 0xCC });
            store.Delete(new byte[] { 0x02 });
        }

        // Phase 2: Neu öffnen → WAL replay
        using (var store = CreateStore())
        {
            Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1));
            Assert.Equal(new byte[] { 0xAA }, v1);

            Assert.False(store.TryGet(new byte[] { 0x02 }, out _)); // Deleted

            Assert.True(store.TryGet(new byte[] { 0x03 }, out var v3));
            Assert.Equal(new byte[] { 0xCC }, v3);
        }
    }

    [Fact]
    public void Recovery_AfterCheckpoint_WalTruncated()
    {
        // Phase 1: Daten schreiben
        using (var store = CreateStore())
        {
            store.Upsert(new byte[] { 0x01 }, new byte[] { 0xAA });
            store.Checkpoint();
            store.Upsert(new byte[] { 0x02 }, new byte[] { 0xBB });
        }

        // Nach dem Schließen sollte die WAL noch existieren (zweiter Commit)
        Assert.True(File.Exists(_walPath));

        // Phase 2: Neu öffnen
        using (var store = CreateStore())
        {
            Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1));
            Assert.Equal(new byte[] { 0xAA }, v1);
            Assert.True(store.TryGet(new byte[] { 0x02 }, out var v2));
            Assert.Equal(new byte[] { 0xBB }, v2);
        }
    }

    [Fact]
    public void Recovery_TransactionCommit_RestoresAll()
    {
        // Phase 1: Mehrere Operationen in einer Transaktion
        using (var store = CreateStore())
        {
            using var tx = store.BeginTransaction();
            tx.Upsert(new byte[] { 0x01 }, new byte[] { 0xAA });
            tx.Upsert(new byte[] { 0x02 }, new byte[] { 0xBB });
            tx.Delete(new byte[] { 0x02 });
            tx.Upsert(new byte[] { 0x03 }, new byte[] { 0xCC });
            tx.Commit();
        }

        // Phase 2: Neu öffnen
        using (var store = CreateStore())
        {
            Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1));
            Assert.Equal(new byte[] { 0xAA }, v1);

            Assert.False(store.TryGet(new byte[] { 0x02 }, out _)); // Deleted

            Assert.True(store.TryGet(new byte[] { 0x03 }, out var v3));
            Assert.Equal(new byte[] { 0xCC }, v3);
        }
    }

    [Fact]
    public void Recovery_MultipleTransactions_RestoresAll()
    {
        // Phase 1: Mehrere Transaktionen
        using (var store = CreateStore())
        {
            using (var tx1 = store.BeginTransaction())
            {
                tx1.Upsert(new byte[] { 0x01 }, new byte[] { 0x11 });
                tx1.Commit();
            }

            using (var tx2 = store.BeginTransaction())
            {
                tx2.Upsert(new byte[] { 0x02 }, new byte[] { 0x22 });
                tx2.Commit();
            }

            using (var tx3 = store.BeginTransaction())
            {
                tx3.Upsert(new byte[] { 0x01 }, new byte[] { 0x33 }); // Update
                tx3.Commit();
            }
        }

        // Phase 2: Neu öffnen
        using (var store = CreateStore())
        {
            Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1));
            Assert.Equal(new byte[] { 0x33 }, v1); // Latest version

            Assert.True(store.TryGet(new byte[] { 0x02 }, out var v2));
            Assert.Equal(new byte[] { 0x22 }, v2);
        }
    }

    [Fact]
    public void Recovery_EmptyWal_NoOp()
    {
        // Neu öffnen ohne vorherige Daten
        using (var store = CreateStore())
        {
            Assert.False(store.TryGet(new byte[] { 0x01 }, out _));
        }
    }

    [Fact]
    public void Recovery_ScanAfterReplay()
    {
        // Phase 1: Daten schreiben
        using (var store = CreateStore())
        {
            store.Upsert(new byte[] { 0x01 }, new byte[] { 0xAA });
            store.Upsert(new byte[] { 0x02 }, new byte[] { 0xBB });
            store.Upsert(new byte[] { 0x03 }, new byte[] { 0xCC });
        }

        // Phase 2: Neu öffnen und scannen
        using (var store = CreateStore())
        {
            // Zuerst prüfen, ob TryGet funktioniert
            Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1), "Key 0x01 should exist after recovery");
            Assert.True(store.TryGet(new byte[] { 0x02 }, out var v2), "Key 0x02 should exist after recovery");
            Assert.True(store.TryGet(new byte[] { 0x03 }, out var v3), "Key 0x03 should exist after recovery");

            // Direkter Scan über den Baum
            var treeResults = store.Scan().ToList();
            Assert.Equal(3, treeResults.Count);
        }
    }

    [Fact]
    public void Recovery_WithoutWal_DataInOds()
    {
        var odsPath = Path.Combine(Path.GetTempPath(), "mvcc-btree-recovery-test-" + Guid.NewGuid().ToString("N") + ".ods");
        try
        {
            // Phase 1: Schreiben ohne WAL
            {
                var pager = new OdsPager(odsPath, pageSize: 4096);
                using var store = new MvccBPlusTreeStore(pager);
                store.Upsert(new byte[] { 0x01 }, new byte[] { 0xAA });
                store.Upsert(new byte[] { 0x02 }, new byte[] { 0xBB });
            }

            // Phase 2: Lesen ohne WAL
            {
                var pager = new OdsPager(odsPath, pageSize: 4096);
                using var store = new MvccBPlusTreeStore(pager);
                Assert.True(store.TryGet(new byte[] { 0x01 }, out var v1));
                Assert.Equal(new byte[] { 0xAA }, v1);

                var results = store.Scan().ToList();
                Assert.Equal(2, results.Count);
            }
        }
        finally
        {
            if (File.Exists(odsPath))
                File.Delete(odsPath);
        }
    }
}
