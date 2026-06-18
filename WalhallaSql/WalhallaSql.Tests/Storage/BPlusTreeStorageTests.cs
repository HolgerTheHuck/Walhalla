using System;
using System.IO;
using WalhallaSql.Core;
using WalhallaSql.Sql;
using WalhallaSql.Storage;
using Xunit;

namespace WalhallaSql.Tests.Storage;

/// <summary>
/// Regression-Coverage für den legacy <see cref="StorageMode.BPlusTree"/> Backend.
/// Der Benchmark <c>WalhallaSqlBPlusTreeDiskBenchmark.DeleteSingle</c> schlug fehl,
/// weil DELETE + re-INSERT derselben PK einen falschen Duplicate-PK-Fehler auslöste.
/// </summary>
public class BPlusTreeStorageTests : IDisposable
{
    private readonly string _rootPath;

    public BPlusTreeStorageTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"walhalla_bplus_sql_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
    }

    private WalhallaEngine CreateEngine()
    {
        var options = new WalhallaOptions(_rootPath)
        {
            StorageMode = StorageMode.BPlusTree,
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
    public void BPlusTree_CreateTable_Insert_Select()
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
    public void BPlusTree_DeleteSingle_ThenReInsert_SamePk()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100), Email VARCHAR(100), Region VARCHAR(50))");
        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (1, 'A', 'a@b.c', 'R0')");
        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'B', 'b@c.d', 'R1')");
        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (3, 'C', 'c@d.e', 'R2')");

        Assert.Equal(1, engine.Execute("DELETE FROM Customers WHERE Id = 2").AffectedRows);

        var afterDelete = engine.Execute("SELECT * FROM Customers ORDER BY Id");
        Assert.Equal(2, afterDelete.Rows.Count);
        Assert.Equal(1, afterDelete.Rows[0]["Id"]);
        Assert.Equal(3, afterDelete.Rows[1]["Id"]);

        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'Restored', 'restored@test.com', 'R0')");

        var allAfterInsert = engine.Execute("SELECT * FROM Customers ORDER BY Id");
        Assert.Equal(3, allAfterInsert.Rows.Count);
        Assert.Equal("Restored", (string)allAfterInsert.Rows[1]["Name"]!);

        var result = engine.Execute("SELECT * FROM Customers WHERE Id = 2");
        Assert.Single(result.Rows);
        Assert.Equal("Restored", (string)result.Rows[0]["Name"]!);
    }

    [Fact]
    public void BPlusTree_DeleteSingle_ThenReInsert_SamePk_DirectTree()
    {
        var path = Path.Combine(_rootPath, "tree.ods");
        using var pager = new OdsPager(path, 4096, pageCacheCapacity: 0);
        using var tree = new BPlusTree(pager);

        static byte[] K(long rowId) => TableStore.BuildRowKey(1, rowId);

        tree.Upsert(K(1), new byte[] { 0x01 });
        tree.Upsert(K(2), new byte[] { 0x02 });
        tree.Upsert(K(3), new byte[] { 0x03 });

        Assert.True(tree.TryGet(K(2), out _));
        Assert.True(tree.Delete(K(2)));
        Assert.False(tree.TryGet(K(2), out _));

        tree.Upsert(K(2), new byte[] { 0x2A }); // 42

        Assert.True(tree.TryGet(K(2), out var value));
        Assert.NotNull(value);
        Assert.Equal(0x2A, value[0]);
    }

    [Fact]
    public void BPlusTree_DeleteByPkRange_ThenReInsert()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        for (int i = 1; i <= 10; i++)
            engine.Execute($"INSERT INTO T (Id, Val) VALUES ({i}, {i * 10})");

        Assert.Equal(3, engine.Execute("DELETE FROM T WHERE Id BETWEEN 3 AND 5").AffectedRows);
        engine.Execute("INSERT INTO T (Id, Val) VALUES (4, 999)");

        var result = engine.Execute("SELECT * FROM T WHERE Id = 4");
        Assert.Single(result.Rows);
        Assert.Equal(999, result.Rows[0]["Val"]);
    }

    [Fact]
    public void BPlusTree_Checkpoint_Reopen_DataPersists()
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
    public void BPlusTree_DeleteAllRows_TableStillExists()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (1, 10)");

        engine.Execute("DELETE FROM T");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2, 20)");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void BPlusTree_Index_Delete_MaintainsIndex()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE users (Id INT PRIMARY KEY, Email STRING)");
        engine.Execute("CREATE INDEX ix_email ON users (Email)");
        engine.Execute("INSERT INTO users (Id, Email) VALUES (1, 'a@b.com')");
        engine.Execute("INSERT INTO users (Id, Email) VALUES (2, 'c@d.com')");

        engine.Execute("DELETE FROM users WHERE Id = 2");

        var result = engine.Execute("SELECT * FROM users WHERE Email = 'c@d.com'");
        Assert.Empty(result.Rows);
    }
}
