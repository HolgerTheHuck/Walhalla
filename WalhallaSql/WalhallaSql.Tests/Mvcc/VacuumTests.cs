using System;
using System.IO;
using WalhallaSql.Core;
using Xunit;

namespace WalhallaSql.Tests.Mvcc;

public class VacuumTests
{
    private static WalhallaEngine CreateMvccEngine()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "walhalla-mvcc-test-" + Guid.NewGuid().ToString("N"));
        var options = new WalhallaOptions(tempPath)
        {
            StorageMode = StorageMode.MvccBPlusTree
        };
        return new WalhallaEngine(options);
    }

    private static WalhallaEngine CreateInMemoryEngine()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory
        };
        return new WalhallaEngine(options);
    }

    [Fact]
    public void Vacuum_OnMvccBPlusTree_ReturnsZero()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var result = engine.Execute("VACUUM");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact]
    public void Vacuum_OnInMemory_ReturnsZero()
    {
        using var engine = CreateInMemoryEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var result = engine.Execute("VACUUM");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact]
    public void Vacuum_OnBPlusTree_ReturnsZero()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"wal_vac_bp_{Guid.NewGuid():N}");
        try
        {
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
                engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

                var result = engine.Execute("VACUUM");
                Assert.Equal(0, result.AffectedRows);
            }
            // Ensure WAL file is closed before cleanup
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                try { Directory.Delete(tempPath, true); }
                catch { /* best-effort cleanup on flaky file locks */ }
            }
        }
    }

    [Fact]
    public void Vacuum_AfterOperations_DoesNotThrow()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice'), (2, 'Bob')");
        engine.Execute("DELETE FROM T WHERE Id = 2");
        engine.Execute("UPDATE T SET Name = 'Charlie' WHERE Id = 1");

        var result = engine.Execute("VACUUM");
        Assert.Equal(0, result.AffectedRows);

        // Data should still be visible after VACUUM
        var rows = engine.Execute("SELECT * FROM T WHERE Id = 1");
        Assert.Single(rows.Rows);
        Assert.Equal("Charlie", rows.Rows[0].GetValue(1));
    }

    [Fact]
    public void Vacuum_TableName_Existing_Succeeds()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var result = engine.Execute("VACUUM T");
        Assert.Equal(0, result.AffectedRows);
    }

    [Fact]
    public void Vacuum_TableName_NonExistent_Throws()
    {
        using var engine = CreateMvccEngine();
        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("VACUUM NoSuchTable"));
        Assert.Contains("NoSuchTable", ex.Message);
    }

    [Fact]
    public void Vacuum_InsideTransaction_Succeeds()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);

        var result = engine.Execute("VACUUM", tx);
        Assert.Equal(0, result.AffectedRows);

        tx.Commit();

        // Data should be visible after VACUUM + commit
        var rows = engine.Execute("SELECT * FROM T");
        Assert.Equal(2, rows.Rows.Count);
    }

    [Fact]
    public void VacuumFull_ThrowsNotSupported()
    {
        using var engine = CreateMvccEngine();
        var ex = Assert.Throws<NotSupportedException>(() => engine.Execute("VACUUM FULL"));
        Assert.Contains("VACUUM FULL", ex.Message);
    }

    [Fact]
    public void Vacuum_OnMvccBPlusTreeFileBased_DoesNotThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"wal_vac_mvcc_{Guid.NewGuid():N}");
        try
        {
            var options = new WalhallaOptions(tempPath)
            {
                StorageMode = StorageMode.MvccBPlusTree
            };
            using var engine = new WalhallaEngine(options);

            engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
            engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice'), (2, 'Bob')");
            engine.Execute("DELETE FROM T WHERE Id = 2");

            var result = engine.Execute("VACUUM");
            Assert.Equal(0, result.AffectedRows);

            var rows = engine.Execute("SELECT * FROM T");
            Assert.Single(rows.Rows);
            Assert.Equal("Alice", rows.Rows[0].GetValue(1));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void Vacuum_EmptyMvccBPlusTree_DoesNotThrow()
    {
        using var engine = CreateMvccEngine();
        var result = engine.Execute("VACUUM");
        Assert.Equal(0, result.AffectedRows);
    }

}
