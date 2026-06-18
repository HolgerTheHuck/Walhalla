using System;
using System.IO;
using System.Linq;
using WalhallaSql.Core;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests.Storage;

/// <summary>
/// Verifiziert, dass WalhallaSql mit <see cref="StorageMode.MvccBPlusTree">
/// als Backend korrekt arbeitet: DDL, DML, SELECT, Vacuum, Reopen/Recovery.
/// </summary>
public class MvccBPlusTreeStorageTests : IDisposable
{
    private readonly string _rootPath;

    public MvccBPlusTreeStorageTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"walhalla_mvcc_sql_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
    }

    private WalhallaEngine CreateEngine()
    {
        var options = new WalhallaOptions(_rootPath)
        {
            StorageMode = StorageMode.MvccBPlusTree,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        };
        return new WalhallaEngine(options);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Mvcc_CreateTable_Insert_Select()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'hello')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'world')");

        var result = engine.Execute("SELECT * FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1, result.Rows[0]["Id"]);
        Assert.Equal("hello", result.Rows[0]["Val"]);
        Assert.Equal(2, result.Rows[1]["Id"]);
        Assert.Equal("world", result.Rows[1]["Val"]);
    }

    [Fact]
    public void Mvcc_Update_Delete_Select()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        engine.Execute("UPDATE T SET Val = 99 WHERE Id = 1");
        engine.Execute("DELETE FROM T WHERE Id = 2");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
        Assert.Equal(99, result.Rows[0]["Val"]);
    }

    [Fact]
    public void Mvcc_Checkpoint_Reopen_DataPersists()
    {
        using (var engine = CreateEngine())
        {
            engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
            engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'persist')");
            engine.Checkpoint();
        }

        using (var engine = CreateEngine())
        {
            var result = engine.Execute("SELECT * FROM T");
            Assert.Single(result.Rows);
            Assert.Equal(1, result.Rows[0]["Id"]);
            Assert.Equal("persist", result.Rows[0]["Val"]);
        }
    }

    [Fact]
    public void Mvcc_Vacuum_DoesNotThrow()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'a')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'b')");
        engine.Execute("DELETE FROM T WHERE Id = 1");

        // Vacuum sollte für MvccBPlusTree ohne Exception durchlaufen
        var vacuumed = engine.Vacuum(null);
        Assert.Equal(0, vacuumed); // MvccBPlusTree-Vacuum gibt keinen Row-Count zurück
    }

    [Fact]
    public void Mvcc_BulkInsert_SelectAll()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 'hello')");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 'world')");

        var result = engine.Execute("SELECT * FROM T ORDER BY Id");
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Mvcc_Index_Create_And_Query()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE INDEX idx_name ON T(Name)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (2, 'Bob')");

        // Full table scan sollte funktionieren
        var all = engine.Execute("SELECT * FROM T");
        Assert.Equal(2, all.Rows.Count);

        // Index query
        var result = engine.Execute("SELECT * FROM T WHERE Name = 'Alice'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void Mvcc_Transaction_Commit_Rollback()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");

        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 100)", tx);
            tx.Commit();
        }

        using (var tx = engine.BeginTransaction())
        {
            engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 200)", tx);
            tx.Rollback();
        }

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }
}
