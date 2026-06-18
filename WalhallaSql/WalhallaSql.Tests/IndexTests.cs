using System;
using System.Linq;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class IndexTests
{
    [Fact]
    public void CreateIndex_ThenQuery_UsesIndex()
    {
        using var engine = WalhallaEngine.InMemory();

        // Create table.
        engine.Execute("CREATE TABLE users (Id INT PRIMARY KEY, Name STRING, Age INT, City STRING)");

        // Insert rows.
        engine.Execute("INSERT INTO users (Id, Name, Age, City) VALUES (1, 'Alice', 30, 'Berlin')");
        engine.Execute("INSERT INTO users (Id, Name, Age, City) VALUES (2, 'Bob', 25, 'Paris')");
        engine.Execute("INSERT INTO users (Id, Name, Age, City) VALUES (3, 'Charlie', 35, 'Berlin')");
        engine.Execute("INSERT INTO users (Id, Name, Age, City) VALUES (4, 'Diana', 28, 'London')");

        // Create index on City.
        engine.Execute("CREATE INDEX ix_city ON users (City)");

        // Query using the indexed column.
        var result = engine.Execute("SELECT Name, City FROM users WHERE City = 'Berlin'");
        Assert.Equal(2, result.Rows.Count);

        var names = result.Rows
            .Select(r => (string)r["Name"]!)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(new[] { "Alice", "Charlie" }, names);
    }

    [Fact]
    public void CreateUniqueIndex_RejectsDuplicate()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE products (Id INT PRIMARY KEY, Sku STRING, Name STRING)");
        engine.Execute("INSERT INTO products (Id, Sku, Name) VALUES (1, 'SKU-001', 'Widget')");

        engine.Execute("CREATE UNIQUE INDEX ix_sku ON products (Sku)");

        var ex = Assert.Throws<WalhallaException>(() =>
            engine.Execute("INSERT INTO products (Id, Sku, Name) VALUES (2, 'SKU-001', 'Duplicate')"));
        Assert.Contains("UNIQUE constraint", ex.Message);
    }

    [Fact]
    public void IndexPersistsAcrossCheckpoint()
    {
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempPath);

        try
        {
            // Create engine, insert data, create index, checkpoint.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute("CREATE TABLE items (Id INT PRIMARY KEY, Code STRING, Value INT)");
                engine.Execute("INSERT INTO items (Id, Code, Value) VALUES (1, 'A', 100)");
                engine.Execute("INSERT INTO items (Id, Code, Value) VALUES (2, 'B', 200)");
                engine.Execute("CREATE INDEX ix_code ON items (Code)");
                engine.Checkpoint();
            }

            // Reopen and verify.
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                var table = engine.GetTable("items");
                Assert.NotNull(table);
                Assert.Single(table!.Indexes);
                Assert.Equal("ix_code", table.Indexes[0].IndexName);

                // Full scan first to verify data persisted.
                var all = engine.Execute("SELECT * FROM items");
                Assert.Equal(2, all.Rows.Count);

                // Index scan.
                var result = engine.Execute("SELECT Value FROM items WHERE Code = 'A'");
                Assert.Single(result.Rows);
                Assert.Equal(100, (int)result.Rows[0]["Value"]!);
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(tempPath, true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void UpdateMaintainsIndex()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE people (Id INT PRIMARY KEY, Email STRING, Name STRING)");
        engine.Execute("INSERT INTO people (Id, Email, Name) VALUES (1, 'a@b.com', 'Alex')");
        engine.Execute("INSERT INTO people (Id, Email, Name) VALUES (2, 'c@d.com', 'Chris')");
        engine.Execute("CREATE INDEX ix_email ON people (Email)");

        // Update changes the indexed column.
        engine.Execute("UPDATE people SET Email = 'x@y.com' WHERE Id = 1");

        // Old value should not be found.
        var empty = engine.Execute("SELECT Name FROM people WHERE Email = 'a@b.com'");
        Assert.Empty(empty.Rows);

        // New value should be found.
        var found = engine.Execute("SELECT Name FROM people WHERE Email = 'x@y.com'");
        Assert.Single(found.Rows);
        Assert.Equal("Alex", (string)found.Rows[0]["Name"]!);

        // Other row still works.
        var other = engine.Execute("SELECT Name FROM people WHERE Email = 'c@d.com'");
        Assert.Single(other.Rows);
    }

    [Fact]
    public void DeleteMaintainsIndex()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE logs (Id INT PRIMARY KEY, Level STRING)");
        engine.Execute("INSERT INTO logs (Id, Level) VALUES (1, 'INFO')");
        engine.Execute("INSERT INTO logs (Id, Level) VALUES (2, 'ERROR')");
        engine.Execute("CREATE INDEX ix_level ON logs (Level)");

        engine.Execute("DELETE FROM logs WHERE Id = 2");

        var result = engine.Execute("SELECT * FROM logs WHERE Level = 'ERROR'");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void DropIndex_RemovesIndex()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE data (Id INT PRIMARY KEY, Tag STRING)");
        engine.Execute("INSERT INTO data (Id, Tag) VALUES (1, 'fast')");
        engine.Execute("CREATE INDEX ix_tag ON data (Tag)");

        engine.Execute("DROP INDEX ix_tag ON data");

        var table = engine.GetTable("data");
        Assert.NotNull(table);
        Assert.Empty(table!.Indexes);
    }

    [Fact]
    public void SelectWithoutIndex_StillWorks()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE noblet (Id INT PRIMARY KEY, X INT, Y STRING)");
        engine.Execute("INSERT INTO noblet (Id, X, Y) VALUES (1, 10, 'hello')");
        engine.Execute("INSERT INTO noblet (Id, X, Y) VALUES (2, 20, 'world')");

        // No index — full scan.
        var result = engine.Execute("SELECT Y FROM noblet WHERE X > 5");
        Assert.Equal(2, result.Rows.Count);
    }
}
