using System;
using System.Linq;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class GinIndexTests
{
    [Fact]
    public void CreateGinIndex_BasicSyntax()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        var table = engine.GetTable("t");
        Assert.NotNull(table);
        var idx = table!.Indexes.FirstOrDefault(i => i.IndexName == "idx");
        Assert.NotNull(idx);
        Assert.Equal(SqlIndexType.Gin, idx!.IndexType);
    }

    [Fact]
    public void GinIndex_RejectsNonJsonbColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, Name STRING)");

        var ex = Assert.Throws<WalhallaException>(() =>
            engine.Execute("CREATE INDEX idx ON t USING GIN (Name)"));
        Assert.Contains("JSONB", ex.Message);
    }

    [Fact]
    public void GinIndex_RejectsMultipleColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, a JSON, b JSON)");

        var ex = Assert.Throws<WalhallaException>(() =>
            engine.Execute("CREATE INDEX idx ON t USING GIN (a, b)"));
        Assert.Contains("exactly one", ex.Message);
    }

    [Fact]
    public void GinIndex_ContainsQueryBasic()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"name\":\"Alice\",\"age\":30}')");
        engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"name\":\"Bob\",\"age\":25}')");
        engine.Execute("INSERT INTO t (Id, data) VALUES (3, '{\"name\":\"Charlie\",\"city\":\"Berlin\"}')");

        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // @> query should find rows containing the key-value pair
        var result = engine.Execute("SELECT Id FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void GinIndex_ContainsQueryMultipleMatches()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        for (int i = 1; i <= 10; i++)
        {
            engine.Execute($"INSERT INTO t (Id, data) VALUES ({i}, '{{\"type\":\"doc\",\"idx\":{i}}}')");
        }

        // Verify data is correct without GIN index first
        var all = engine.Execute("SELECT COUNT(*) FROM t");
        Assert.Equal(10L, all.Rows[0]["COUNT(*)"]);

        var fullScan = engine.Execute("SELECT * FROM t WHERE data @> '{\"type\":\"doc\"}'");
        Assert.Equal(10, fullScan.Rows.Count);

        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        var result = engine.Execute("SELECT COUNT(*) FROM t WHERE data @> '{\"type\":\"doc\"}'");
        Assert.Equal(10L, result.Rows[0]["COUNT(*)"]);
    }

    [Fact]
    public void GinIndex_ContainsQueryEmptyResult()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"name\":\"Alice\"}')");

        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        var result = engine.Execute("SELECT * FROM t WHERE data @> '{\"name\":\"Bob\"}'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void GinIndex_HasKeyQuery()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"name\":\"Alice\",\"age\":30}')");
        engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"name\":\"Bob\"}')");

        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // ? operator: rows with key "age"
        var result = engine.Execute("SELECT Id FROM t WHERE data ? 'age'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void GinIndex_NestedObject()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"address\":{\"city\":\"Berlin\",\"zip\":\"10115\"}}')");
        engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"address\":{\"city\":\"Paris\"}}')");

        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // @> on nested object
        var result = engine.Execute("SELECT Id FROM t WHERE data @> '{\"address\":{\"city\":\"Berlin\"}}'");
        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0]["Id"]);
    }

    [Fact]
    public void GinIndex_PersistsAcrossCheckpoint()
    {
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempPath);

        try
        {
            // Create engine with storage, insert data, create GIN index.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
                engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"key\":\"val1\"}')");
                engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"key\":\"val2\"}')");
                engine.Execute("CREATE INDEX idx ON t USING GIN (data)");
                engine.Checkpoint();
            }

            // Reopen and query.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                var table = engine.GetTable("t");
                Assert.NotNull(table);
                Assert.Single(table!.Indexes);
                Assert.Equal(SqlIndexType.Gin, table.Indexes[0].IndexType);

                var result = engine.Execute("SELECT Id FROM t WHERE data @> '{\"key\":\"val1\"}'");
                Assert.Single(result.Rows);
                Assert.Equal(1, result.Rows[0]["Id"]);
            }
        }
        finally
        {
            System.IO.Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void GinIndex_Update_MaintainsIndex()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"name\":\"Alice\"}')");
        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // Before update: should find Alice
        var r1 = engine.Execute("SELECT * FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Single(r1.Rows);

        // Update the JSON
        engine.Execute("UPDATE t SET data = '{\"name\":\"Bob\"}' WHERE Id = 1");

        // Alice should no longer be found
        var r2 = engine.Execute("SELECT * FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Empty(r2.Rows);

        // Bob should now be found
        var r3 = engine.Execute("SELECT * FROM t WHERE data @> '{\"name\":\"Bob\"}'");
        Assert.Single(r3.Rows);
    }

    [Fact]
    public void GinIndex_Delete_RemovesEntries()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, '{\"name\":\"Alice\"}')");
        engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"name\":\"Bob\"}')");
        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // Both rows should be found
        var r1 = engine.Execute("SELECT COUNT(*) FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Equal(1L, r1.Rows[0]["COUNT(*)"]);

        engine.Execute("DELETE FROM t WHERE Id = 1");

        var r2 = engine.Execute("SELECT COUNT(*) FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Equal(0L, r2.Rows[0]["COUNT(*)"]);

        // Bob should still be there
        var r3 = engine.Execute("SELECT COUNT(*) FROM t WHERE data @> '{\"name\":\"Bob\"}'");
        Assert.Equal(1L, r3.Rows[0]["COUNT(*)"]);
    }

    [Fact]
    public void GinIndex_HandlesNullJsonbValues()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE t (Id INT PRIMARY KEY, data JSON)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (1, NULL)");
        engine.Execute("INSERT INTO t (Id, data) VALUES (2, '{\"name\":\"Alice\"}')");
        engine.Execute("CREATE INDEX idx ON t USING GIN (data)");

        // Query should still work — null rows just have no GIN entries
        var result = engine.Execute("SELECT Id FROM t WHERE data @> '{\"name\":\"Alice\"}'");
        Assert.Single(result.Rows);
        Assert.Equal(2, result.Rows[0]["Id"]);
    }

    [Fact]
    public void GinIndex_ContainsQuery_PerformanceComparison()
    {
        // Verify that a @> query with a GIN index completes correctly on 100 rows.
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE perf (Id INT PRIMARY KEY, data JSON)");

        for (int i = 0; i < 100; i++)
        {
            engine.Execute($"INSERT INTO perf (Id, data) VALUES ({i}, '{{\"kind\":\"row\",\"value\":{i}}}')");
        }

        engine.Execute("CREATE INDEX idx ON perf USING GIN (data)");

        // Query using @> with the GIN index
        var result = engine.Execute("SELECT COUNT(*) FROM perf WHERE data @> '{\"kind\":\"row\"}'");
        Assert.Equal(100L, result.Rows[0]["COUNT(*)"]);

        // Query for a specific value
        var result2 = engine.Execute("SELECT Id FROM perf WHERE data @> '{\"value\":42}'");
        Assert.Single(result2.Rows);
        Assert.Equal(42, result2.Rows[0]["Id"]);
    }
}
