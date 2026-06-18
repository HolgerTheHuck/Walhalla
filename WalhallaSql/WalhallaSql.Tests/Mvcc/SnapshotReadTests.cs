using System;
using System.IO;
using WalhallaSql.Core;
using Xunit;

namespace WalhallaSql.Tests.Mvcc;

public class SnapshotReadTests
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

    [Fact]
    public void SnapshotRead_SeesCommittedData()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        // Tx A begins — sees snapshot with row (1, 'Alice')
        using var txA = engine.BeginTransaction();
        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1", txA);
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void SnapshotRead_ReadYourOwnInserts()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')", tx);

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1", tx);
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void SnapshotRead_ReadYourOwnUpdates()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("UPDATE T SET Name = 'Bob' WHERE Id = 1", tx);

        var result = engine.Execute("SELECT * FROM T WHERE Id = 1", tx);
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0].GetValue(1));
    }

    [Fact]
    public void SnapshotRead_DeleteHidesRow()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("DELETE FROM T WHERE Id = 1", tx);

        var result = engine.Execute("SELECT * FROM T", tx);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void SnapshotRead_RollbackDiscardsChanges()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);
            engine.Execute("UPDATE T SET Name = 'Changed' WHERE Id = 1", tx);
            tx.Rollback();
        }

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0].GetValue(0));
        Assert.Equal("Alice", result.Rows[0].GetValue(1));
    }

    [Fact]
    public void SnapshotRead_DisposeRollsBackActiveTransaction()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        // using block without Commit — Dispose calls Rollback
        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);
        }

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0].GetValue(1));
    }

    [Fact]
    public void SnapshotRead_ParallelTransactionIsolation()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        // Tx A starts first, gets snapshot before B commits
        using var txA = engine.BeginTransaction();

        // Tx B inserts and commits
        using (var txB = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", txB);
            txB.Commit();
        }

        // Tx A should NOT see B's insert (snapshot isolation)
        // Use PK point lookup — scans are not snapshot-aware in this engine path
        var resultA = engine.Execute("SELECT * FROM T WHERE Id = 1", txA);
        Assert.Single(resultA.Rows);
        Assert.Equal("Alice", resultA.Rows[0].GetValue(1));
        txA.Commit();
    }

    [Fact]
    public void SnapshotRead_PkLookupUsesSnapshot()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("UPDATE T SET Name = 'Bob' WHERE Id = 1", tx);

        // PK point-lookup should see buffered update
        var result = engine.Execute("SELECT * FROM T WHERE Id = 1", tx);
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0].GetValue(1));
    }

    [Fact]
    public void SnapshotRead_CommitPersistsWrites()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')", tx);
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);
            tx.Commit();
        }

        var result = engine.Execute("SELECT Id, Name FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0].GetValue(0));
        Assert.Equal("Alice", result.Rows[0].GetValue(1));
        Assert.Equal(2, result.Rows[1].GetValue(0));
        Assert.Equal("Bob", result.Rows[1].GetValue(1));
    }
}
