using System;
using System.IO;
using System.Threading.Tasks;
using WalhallaSql.Core;
using Xunit;

namespace WalhallaSql.Tests;

public class TransactionModeTests
{
    private static WalhallaOptions TempMvccOptions(TransactionMode? mode = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "walhalla-mvcc-test-" + Guid.NewGuid().ToString("N"));
        var options = new WalhallaOptions(tempPath)
        {
            StorageMode = StorageMode.MvccBPlusTree
        };
        if (mode.HasValue)
            options.TransactionMode = mode.Value;
        return options;
    }

    // ── Option-Level ──────────────────────────────────────────────

    [Fact]
    public void Options_TransactionMode_DefaultIsNull()
    {
        var options = new WalhallaOptions(":memory:");
        Assert.Null(options.TransactionMode);
    }

    [Fact]
    public void Options_TransactionMode_SetToLocking()
    {
        var options = new WalhallaOptions(":memory:")
        {
            TransactionMode = TransactionMode.Locking
        };
        Assert.Equal(TransactionMode.Locking, options.TransactionMode);
    }

    [Fact]
    public void Options_TransactionMode_SetToMvcc()
    {
        var options = new WalhallaOptions(":memory:")
        {
            TransactionMode = TransactionMode.Mvcc
        };
        Assert.Equal(TransactionMode.Mvcc, options.TransactionMode);
    }

    // ── MvccBPlusTree Locking Mode ────────────────────────────────────────

    [Fact]
    public void MvccBPlusTree_LockingMode_InsertAndSelect()
    {
        var options = TempMvccOptions(TransactionMode.Locking);
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var rows = engine.Execute("SELECT * FROM T WHERE Id = 1");
        Assert.Single(rows.Rows);
        Assert.Equal("Alice", rows.Rows[0].GetValue(1));
    }

    [Fact]
    public void MvccBPlusTree_LockingMode_ConcurrentInsert_Serialized()
    {
        var options = TempMvccOptions(TransactionMode.Locking);
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        var t1 = Task.Run(() => engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')"));
        var t2 = Task.Run(() => engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')"));

        Task.WaitAll(t1, t2);

        var rows = engine.Execute("SELECT * FROM T ORDER BY Id");
        Assert.Equal(2, rows.Rows.Count);
    }

    [Fact]
    public void MvccBPlusTree_LockingMode_RollbackDiscardsChanges()
    {
        var options = TempMvccOptions(TransactionMode.Locking);
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);
        tx.Rollback();

        var rows = engine.Execute("SELECT * FROM T");
        Assert.Single(rows.Rows);
        Assert.Equal("Alice", rows.Rows[0].GetValue(1));
    }

    // ── MvccBPlusTree MVCC Mode (Default) ─────────────────────────────────

    [Fact]
    public void MvccBPlusTree_MvccMode_SnapshotIsolationDefault()
    {
        var options = TempMvccOptions();
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        using var tx = engine.BeginTransaction();
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')", tx);

        // Outside tx should not see uncommitted row
        var rows = engine.Execute("SELECT * FROM T");
        Assert.Single(rows.Rows);

        tx.Commit();

        // After commit, both rows visible
        rows = engine.Execute("SELECT * FROM T ORDER BY Id");
        Assert.Equal(2, rows.Rows.Count);
    }

    [Fact]
    public void MvccBPlusTree_MvccMode_SetIsolationLevel()
    {
        var options = TempMvccOptions(TransactionMode.Mvcc);
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("SET TRANSACTION ISOLATION LEVEL READ COMMITTED");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var rows = engine.Execute("SELECT * FROM T");
        Assert.Single(rows.Rows);
    }

    [Fact]
    public void MvccBPlusTree_MvccMode_Vacuum()
    {
        var options = TempMvccOptions(TransactionMode.Mvcc);
        using var engine = new WalhallaEngine(options);
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var result = engine.Execute("VACUUM");
        Assert.Equal(0, result.AffectedRows);
    }

    // ── SQL Parsing ────────────────────────────────────────────────

    [Fact]
    public void SetTransactionMode_Locking()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("SET walhalla.transaction_mode = 'locking'");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var rows = engine.Execute("SELECT * FROM T");
        Assert.Single(rows.Rows);
    }

    [Fact]
    public void SetTransactionMode_Mvcc()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("SET walhalla.transaction_mode = 'mvcc'");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var rows = engine.Execute("SELECT * FROM T");
        Assert.Single(rows.Rows);
    }

    [Fact]
    public void SetTransactionMode_CaseInsensitive()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        engine.Execute("SET walhalla.transaction_mode = 'Mvcc'");
        engine.Execute("SET walhalla.transaction_mode = 'LOCKING'");
    }

    [Fact]
    public void SetTransactionMode_WithoutQuotes()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        engine.Execute("SET walhalla.transaction_mode = mvcc");
        engine.Execute("SET walhalla.transaction_mode = locking");
    }

    [Fact]
    public void SetTransactionMode_MissingEquals()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        engine.Execute("SET walhalla.transaction_mode 'mvcc'");
    }

    [Fact]
    public void SetTransactionMode_InvalidMode_Throws()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        var ex = Assert.Throws<NotSupportedException>(() =>
            engine.Execute("SET walhalla.transaction_mode = 'serializable'"));
        Assert.Contains("serializable", ex.Message);
    }

    [Fact]
    public void SetTransactionMode_EmptyMode_Throws()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        Assert.Throws<NotSupportedException>(() =>
            engine.Execute("SET walhalla.transaction_mode = ''"));
    }

    // ── Compatibility ─────────────────────────────────────────────

    [Fact]
    public void SetTransactionMode_InsideTransaction_Throws()
    {
        using var engine = new WalhallaEngine(TempMvccOptions());
        using var tx = engine.BeginTransaction();
        var ex = Assert.Throws<NotSupportedException>(() =>
            engine.Execute("SET walhalla.transaction_mode = 'mvcc'", tx));
        Assert.Contains("SqlSetTransactionModeStatement", ex.Message);
    }

    [Fact]
    public void SetTransactionMode_MvccOnNonMvcc_Throws()
    {
        using var engine = new WalhallaEngine(new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory
        });
        var ex = Assert.Throws<NotSupportedException>(() =>
            engine.Execute("SET walhalla.transaction_mode = 'mvcc'"));
        Assert.Contains("mvcc", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void SetTransactionMode_LockingOnNonMvcc_Succeeds()
    {
        using var engine = new WalhallaEngine(new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory
        });
        // locking should always work since it's the default non-MVCC path
        engine.Execute("SET walhalla.transaction_mode = 'locking'");
    }
}
