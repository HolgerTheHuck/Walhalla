using System;
using System.IO;
using WalhallaSql.Core;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;
using Xunit;

namespace WalhallaSql.Tests.Mvcc;

public class IsolationLevelTests
{
    private static WalhallaEngine CreateMvccEngine(int maxTransactionRetries = 5)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "walhalla-mvcc-test-" + Guid.NewGuid().ToString("N"));
        var options = new WalhallaOptions(tempPath)
        {
            StorageMode = StorageMode.MvccBPlusTree,
            MaxTransactionRetries = maxTransactionRetries
        };
        return new WalhallaEngine(options);
    }

    [Fact]
    public void DefaultIsolationLevel_IsSnapshot()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        // Without explicit SET TRANSACTION, default is Snapshot.
        // Tx A starts first, gets a snapshot before B commits.
        using var txA = engine.BeginTransaction();

        using (var txB = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", txB);
            txB.Commit();
        }

        // Tx A should NOT see B's insert (snapshot isolation)
        var result = engine.Execute("SELECT * FROM T WHERE Id = 2", txA);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void SetTransaction_ReadCommitted_NoWriteConflictDetection()
    {
        // ReadCommitted differs from Snapshot only at commit time:
        // it does NOT check for write-write conflicts.
        // Two transactions can write the same key and both commit.
        using var engine = CreateMvccEngine(maxTransactionRetries: 1);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("SET TRANSACTION ISOLATION LEVEL READ COMMITTED");

        using var txA = engine.BeginTransaction();
        using var txB = engine.BeginTransaction();

        engine.Execute("UPDATE T SET Name = 'TxA' WHERE Id = 1", txA);
        engine.Execute("UPDATE T SET Name = 'TxB' WHERE Id = 1", txB);

        // Both commits succeed — no write-write conflict with ReadCommitted
        txA.Commit();
        txB.Commit();

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("TxB", result.Rows[0].GetValue(0));
    }

    [Fact]
    public void SetTransaction_RepeatableRead_MapsToSnapshot()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");

        using var txA = engine.BeginTransaction();

        using (var txB = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", txB);
            txB.Commit();
        }

        // REPEATABLE READ maps to Snapshot — should NOT see B's insert
        var result = engine.Execute("SELECT * FROM T WHERE Id = 2", txA);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void SetTransaction_Serializable_DetectsConflict()
    {
        // MaxTransactionRetries=1: one attempt, no retry on conflict
        using var engine = CreateMvccEngine(maxTransactionRetries: 1);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE");

        using var txA = engine.BeginTransaction();
        using var txB = engine.BeginTransaction();

        // Both read the same row
        engine.Execute("SELECT * FROM T WHERE Id = 1", txA);
        engine.Execute("SELECT * FROM T WHERE Id = 1", txB);

        // Both write to the same row (classic SI anomaly — SSI detects this)
        engine.Execute("UPDATE T SET Name = 'TxA' WHERE Id = 1", txA);
        engine.Execute("UPDATE T SET Name = 'TxB' WHERE Id = 1", txB);

        txA.Commit();

        // txB should fail with conflict (SSI rw-dependency cycle)
        Assert.Throws<TransactionConflictException>(() => txB.Commit());
    }

    [Fact]
    public void SetTransaction_InvalidLevel_Throws()
    {
        using var engine = CreateMvccEngine();
        var ex = Assert.Throws<NotSupportedException>(
            () => engine.Execute("SET TRANSACTION ISOLATION LEVEL WRONG"));
        Assert.Contains("WRONG", ex.Message);
    }

    [Fact]
    public void SetTransaction_CaseInsensitive()
    {
        using var engine = CreateMvccEngine(maxTransactionRetries: 1);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        // Lowercase should work
        engine.Execute("set transaction isolation level serializable");

        // Verify Serializable is active: write-write conflict is detected
        using var txA = engine.BeginTransaction();
        using var txB = engine.BeginTransaction();

        engine.Execute("SELECT * FROM T WHERE Id = 1", txA);
        engine.Execute("SELECT * FROM T WHERE Id = 1", txB);
        engine.Execute("UPDATE T SET Name = 'TxA' WHERE Id = 1", txA);
        engine.Execute("UPDATE T SET Name = 'TxB' WHERE Id = 1", txB);

        txA.Commit();
        Assert.Throws<TransactionConflictException>(() => txB.Commit());
    }

    [Fact]
    public void SetTransaction_InsideTransaction_Throws()
    {
        using var engine = CreateMvccEngine();
        using var tx = engine.BeginTransaction();

        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.Execute("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", tx));
        Assert.Contains("outside any transaction", ex.Message);
    }

    [Fact]
    public void Snapshot_DoesNotSeeUncommittedData()
    {
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var txA = engine.BeginTransaction();

        // Tx B inserts and commits
        using (var txB = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", txB);
            txB.Commit();
        }

        // Tx A has a snapshot from before B committed — should NOT see (2, 'Bob')
        var result = engine.Execute("SELECT * FROM T WHERE Id = 2", txA);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Snapshot_BasicWriteCommit()
    {
        // Verify a simple UPDATE+Commit works in Snapshot mode
        using var engine = CreateMvccEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("UPDATE T SET Name = 'Bob' WHERE Id = 1", tx);

        var beforeCommit = engine.Execute("SELECT * FROM T WHERE Id = 1", tx);
        Assert.Equal("Bob", beforeCommit.Rows[0].GetValue(1));

        tx.Commit();

        var afterCommit = engine.Execute("SELECT * FROM T WHERE Id = 1");
        Assert.Equal("Bob", afterCommit.Rows[0].GetValue(1));
    }

    [Fact]
    public void Snapshot_WriteWriteConflict_Detected()
    {
        // Snapshot isolation detects write-write conflicts
        using var engine = CreateMvccEngine(maxTransactionRetries: 1);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        // Verify SET TRANSACTION didn't change from default Snapshot

        using var txA = engine.BeginTransaction();
        using var txB = engine.BeginTransaction();

        // Verify isolation
        var resultA = engine.Execute("SELECT * FROM T WHERE Id = 1", txA);
        Assert.Equal("Alice", resultA.Rows[0].GetValue(1));

        engine.Execute("UPDATE T SET Name = 'TxA' WHERE Id = 1", txA);
        engine.Execute("UPDATE T SET Name = 'TxB' WHERE Id = 1", txB);

        txA.Commit();
        // txB: write-write conflict — other tx committed the same key
        Assert.Throws<TransactionConflictException>(() => txB.Commit());
    }

    [Fact]
    public void Serializable_WriteConflict_Retried()
    {
        // With retries, a write-conflicting transaction eventually succeeds
        using var engine = CreateMvccEngine(); // default 5 retries
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE");

        using var txA = engine.BeginTransaction();
        using var txB = engine.BeginTransaction();

        engine.Execute("UPDATE T SET Name = 'TxA' WHERE Id = 1", txA);
        engine.Execute("UPDATE T SET Name = 'TxB' WHERE Id = 1", txB);

        txA.Commit();
        // txB: conflict on first attempt, retries with fresh snapshot, succeeds
        txB.Commit();

        var result = engine.Execute("SELECT Name FROM T WHERE Id = 1");
        Assert.Equal("TxB", result.Rows[0].GetValue(0));
    }
}
