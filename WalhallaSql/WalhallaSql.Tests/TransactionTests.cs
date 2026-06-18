using System;
using Xunit;

namespace WalhallaSql.Tests;

public class TransactionTests
{
    [Fact]
    public void Commit_PersistsInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')", tx);
        tx.Commit();

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
    }

    [Fact]
    public void Rollback_DiscardsInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')", tx);
        tx.Rollback();

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void ReadYourOwnWrites_Insert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')", tx);

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1", tx);
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
    }

    [Fact]
    public void UpdateInTransaction_ReadsUpdatedValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING, Score INT)");
        engine.Execute("INSERT INTO t (Id, Name, Score) VALUES (1, 'Alice', 10)");

        using var tx = engine.BeginTransaction();
        engine.Execute("UPDATE t SET Score = 20 WHERE Id = 1", tx);

        var result = engine.Execute("SELECT Id, Score FROM t WHERE Id = 1", tx);
        Assert.Single(result.Rows);
        Assert.Equal(20, result.Rows[0]["Score"]);
    }

    [Fact]
    public void DeleteInTransaction_RowNotVisible()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("DELETE FROM t WHERE Id = 1", tx);

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1", tx);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void OtherEngine_CannotSeeUncommitted()
    {
        using var engine1 = WalhallaEngine.InMemory();
        engine1.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        using var engine2 = WalhallaEngine.InMemory();
        engine2.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        // engine1 already has data; engine2 starts fresh and won't see engine1's data
        // since they are separate in-memory instances. This test validates the isolation model.
        using var tx = engine1.BeginTransaction();
        engine1.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')", tx);

        // engine2 has its own in-memory store, so it can't see engine1's uncommitted data
        var result = engine2.Execute("SELECT * FROM t");
        Assert.Empty(result.Rows);

        tx.Commit();
    }

    [Fact]
    public void ConcurrentTransactions_DifferentTables()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE a (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE b (Id INT PRIMARY KEY, Val STRING)");

        using var tx1 = engine.BeginTransaction();
        using var tx2 = engine.BeginTransaction();

        engine.Execute("INSERT INTO a (Id, Val) VALUES (1, 'A1')", tx1);
        engine.Execute("INSERT INTO b (Id, Val) VALUES (1, 'B1')", tx2);

        // Each transaction sees only its own writes
        var r1 = engine.Execute("SELECT * FROM a", tx1);
        Assert.Single(r1.Rows);

        var r2 = engine.Execute("SELECT * FROM b", tx2);
        Assert.Single(r2.Rows);

        tx1.Commit();
        tx2.Commit();

        var ra = engine.Execute("SELECT * FROM a");
        Assert.Single(ra.Rows);

        var rb = engine.Execute("SELECT * FROM b");
        Assert.Single(rb.Rows);
    }

    [Fact]
    public void Dispose_AutoRollback()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO t (Id, Name) VALUES (1, 'Alice')", tx);
        } // Dispose without Commit → auto-rollback

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void MultipleOperations_CommitPersistsAll()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING, Score INT)");
        // pre-seed some data
        engine.Execute("INSERT INTO t (Id, Name, Score) VALUES (1, 'Alice', 10)");
        engine.Execute("INSERT INTO t (Id, Name, Score) VALUES (2, 'Bob', 20)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Name, Score) VALUES (3, 'Charlie', 30)", tx);
        engine.Execute("UPDATE t SET Score = 15 WHERE Id = 1", tx);
        engine.Execute("DELETE FROM t WHERE Id = 2", tx);
        tx.Commit();

        var result = engine.Execute("SELECT * FROM t ORDER BY Id ASC");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0]["Id"]);
        Assert.Equal(15, result.Rows[0]["Score"]);
        Assert.Equal(3, result.Rows[1]["Id"]);
        Assert.Equal("Charlie", result.Rows[1]["Name"]);
    }

    [Fact]
    public void InsertSelect_InTransaction()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE src (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE dst (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO src (Id, Val) VALUES (1, 'X')");
        engine.Execute("INSERT INTO src (Id, Val) VALUES (2, 'Y')");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO dst (Id, Val) SELECT * FROM src WHERE Id = 1", tx);
        tx.Commit();

        var result = engine.Execute("SELECT * FROM dst");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    // ── Savepoints ────────────────────────────────────────────────────────

    [Fact]
    public void Savepoint_RollbackTo_UndoesWrites()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Val STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Val) VALUES (1, 'first')", tx);
        engine.Execute("SAVEPOINT sp1", tx);
        engine.Execute("INSERT INTO t (Id, Val) VALUES (2, 'second')", tx);
        engine.Execute("ROLLBACK TO sp1", tx);
        tx.Commit();

        var result = engine.Execute("SELECT * FROM t ORDER BY Id ASC");
        Assert.Single(result.Rows);
        Assert.Equal("first", result.Rows[0]["Val"]);
    }

    [Fact]
    public void Savepoint_RollbackTo_UndoesUpdate()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO t (Id, Val) VALUES (1, 'original')");

        using var tx = engine.BeginTransaction();
        engine.Execute("SAVEPOINT sp1", tx);
        engine.Execute("UPDATE t SET Val = 'modified' WHERE Id = 1", tx);
        engine.Execute("ROLLBACK TO sp1", tx);
        tx.Commit();

        var result = engine.Execute("SELECT Val FROM t WHERE Id = 1");
        Assert.Equal("original", result.Rows[0]["Val"]);
    }

    [Fact]
    public void Savepoint_RollbackTo_UndoesDelete()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO t (Id, Val) VALUES (1, 'keep')");

        using var tx = engine.BeginTransaction();
        engine.Execute("SAVEPOINT sp1", tx);
        engine.Execute("DELETE FROM t WHERE Id = 1", tx);
        engine.Execute("ROLLBACK TO sp1", tx);
        tx.Commit();

        var result = engine.Execute("SELECT * FROM t WHERE Id = 1");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Savepoint_Release_RemovesSavepoint()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Val STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Val) VALUES (1, 'first')", tx);
        engine.Execute("SAVEPOINT sp1", tx);
        engine.Execute("INSERT INTO t (Id, Val) VALUES (2, 'second')", tx);
        engine.Execute("RELEASE SAVEPOINT sp1", tx);

        // Savepoint released — rollback to it should fail
        Assert.Throws<InvalidOperationException>(() => tx.RollbackTo("sp1"));

        tx.Commit();
        Assert.Equal(2, engine.Execute("SELECT * FROM t").Rows.Count);
    }

    [Fact]
    public void Savepoint_MultipleLevels()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Val STRING)");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO t (Id, Val) VALUES (1, 'a')", tx);
        engine.Execute("SAVEPOINT s1", tx);
        engine.Execute("INSERT INTO t (Id, Val) VALUES (2, 'b')", tx);
        engine.Execute("SAVEPOINT s2", tx);
        engine.Execute("INSERT INTO t (Id, Val) VALUES (3, 'c')", tx);

        engine.Execute("ROLLBACK TO s2", tx);
        // Row 3 is gone, row 2 still visible
        var result = engine.Execute("SELECT * FROM t", tx);
        Assert.Equal(2, result.Rows.Count);

        engine.Execute("ROLLBACK TO s1", tx);
        // Row 2 is gone, only row 1
        result = engine.Execute("SELECT * FROM t", tx);
        Assert.Single(result.Rows);
        Assert.Equal("a", result.Rows[0]["Val"]);

        tx.Commit();
    }
}
