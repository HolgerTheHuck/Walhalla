using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

public abstract class SqliteBenchmarkBase
{
    protected SqliteConnection Connection = null!;
    protected string? DbFile;
    protected const int SeedRowCount = 10_000;

    // Rolling offsets so Insert+Delete benchmarks operate on a fresh Id range
    // each iteration. Kept symmetric with WalhallaBenchmarkBase for fairness.
    private int _insertBatchOffset = SeedRowCount;
    private int _insertIdxOffset = SeedRowCount;

    protected abstract SqliteConnection CreateConnection();

    [GlobalSetup]
    public void GlobalSetup()
    {
        Connection = CreateConnection();
        Connection.Open();

        foreach (var ddl in GetSchemaDdl())
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        SeedData();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Connection?.Close();
        Connection?.Dispose();
        if (DbFile != null)
        {
            try { File.Delete(DbFile); } catch { }
            try { File.Delete(DbFile + "-wal"); } catch { }
            try { File.Delete(DbFile + "-shm"); } catch { }
        }
    }

    [Benchmark]
    public int SelectById()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", 5000L);
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectRange()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id BETWEEN @min AND @max";
        cmd.Parameters.AddWithValue("@min", 1000L);
        cmd.Parameters.AddWithValue("@max", 2000L);
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectRangeMaterialized()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id BETWEEN @min AND @max";
        cmd.Parameters.AddWithValue("@min", 1000L);
        cmd.Parameters.AddWithValue("@max", 2000L);
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            rows.Add(new object?[]
            {
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)
            });
        }
        return rows.Count;
    }

    [Benchmark]
    public int SelectJoin()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, TotalAmount, CustomerId FROM Orders WHERE Id BETWEEN @min AND @max";
        cmd.Parameters.AddWithValue("@min", 1000L);
        cmd.Parameters.AddWithValue("@max", 2000L);
        using var reader = cmd.ExecuteReader();

        var customerIds = new List<long>();
        while (reader.Read())
            customerIds.Add(reader.GetInt64(2));

        int count = 0;
        foreach (var custId in customerIds)
        {
            using var custCmd = Connection.CreateCommand();
            custCmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id";
            custCmd.Parameters.AddWithValue("@id", custId);
            using var custReader = custCmd.ExecuteReader();
            while (custReader.Read()) count++;
        }
        return count;
    }

    [Benchmark]
    public int InsertBatch()
    {
        int start = _insertBatchOffset + 1;
        int end = _insertBatchOffset + 100;
        _insertBatchOffset += 100;

        using var tx = Connection.BeginTransaction();
        for (int i = start; i <= end; i++)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", (long)i);
            cmd.Parameters.AddWithValue("@p1", "User " + i);
            cmd.Parameters.AddWithValue("@p2", "user" + i + "@test.com");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = Connection.CreateCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    [Benchmark]
    public int UpdateSingle()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"UPDATE Customers SET Name = 'Updated {DateTime.UtcNow.Ticks}' WHERE Id = 1";
        return cmd.ExecuteNonQuery();
    }

    [Benchmark]
    public int DeleteSingle()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Customers WHERE Id = 2";
        int affected = cmd.ExecuteNonQuery();

        if (affected > 0)
        {
            using var restore = Connection.CreateCommand();
            restore.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'Restored', 'restored@test.com', 'R0')";
            restore.ExecuteNonQuery();
        }
        return affected;
    }

    [Benchmark]
    public int SelectByIndexedEmail()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Email = @email";
        cmd.Parameters.AddWithValue("@email", "cust5000@demo.local");
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectByIndexedRegion()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Region = @region";
        cmd.Parameters.AddWithValue("@region", "R5");
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectByNonIndexedName()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Name = @name";
        cmd.Parameters.AddWithValue("@name", "Customer 5000");
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int InsertWithIndexMaintenance()
    {
        int start = _insertIdxOffset + 1;
        int end = _insertIdxOffset + 100;
        _insertIdxOffset += 100;

        using var tx = Connection.BeginTransaction();
        for (int i = start; i <= end; i++)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", (long)i);
            cmd.Parameters.AddWithValue("@p1", "User " + i);
            cmd.Parameters.AddWithValue("@p2", "user" + i + "@test.com");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = Connection.CreateCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    // ── Schema + Seed ──────────────────────────────────────────────────

    private static IEnumerable<string> GetSchemaDdl()
    {
        yield return @"
            CREATE TABLE Customers (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                Region TEXT NOT NULL
            );";
        yield return @"
            CREATE TABLE Orders (
                Id INTEGER PRIMARY KEY,
                CustomerId INTEGER NOT NULL,
                OrderDate TEXT NOT NULL,
                TotalAmount REAL NOT NULL
            );";
        yield return @"
            CREATE TABLE OrderItems (
                Id INTEGER PRIMARY KEY,
                OrderId INTEGER NOT NULL,
                ProductName TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                UnitPrice REAL NOT NULL
            );";
        yield return "CREATE INDEX ix_Customers_Email ON Customers (Email);";
        yield return "CREATE INDEX ix_Customers_Region ON Customers (Region);";
        yield return "CREATE INDEX ix_Orders_CustomerId ON Orders (CustomerId);";
    }

    private void SeedData()
    {
        var rnd = new Random(42);

        using var tx = Connection.BeginTransaction();

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", (long)i);
            cmd.Parameters.AddWithValue("@p1", "Customer " + i);
            cmd.Parameters.AddWithValue("@p2", "cust" + i + "@demo.local");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Orders (Id, CustomerId, OrderDate, TotalAmount) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", (long)i);
            cmd.Parameters.AddWithValue("@p1", (long)((i % SeedRowCount) + 1));
            cmd.Parameters.AddWithValue("@p2", DateTime.UtcNow.AddDays(-rnd.Next(365)).ToString("O"));
            cmd.Parameters.AddWithValue("@p3", rnd.NextDouble() * 1000);
            cmd.ExecuteNonQuery();
        }

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO OrderItems (Id, OrderId, ProductName, Quantity, UnitPrice) VALUES (@p0, @p1, @p2, @p3, @p4)";
            cmd.Parameters.AddWithValue("@p0", (long)i);
            cmd.Parameters.AddWithValue("@p1", (long)((i % SeedRowCount) + 1));
            cmd.Parameters.AddWithValue("@p2", "Product " + i);
            cmd.Parameters.AddWithValue("@p3", (long)rnd.Next(1, 20));
            cmd.Parameters.AddWithValue("@p4", rnd.NextDouble() * 100);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    protected static string CreateTempDbFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks.Sqlite");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".sqlite3");
    }
}
