using System;
using System.Collections.Generic;
using System.Data;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// MSSQL-Comparison benchmark. Nutzt immer dieselbe Datenbank, damit
/// der SQL-Server-Buffer-Pool "warm" bleibt (Nachteile bewusst).
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class MsSqlBenchmark
{
    private const int SeedRowCount = 10_000;
    private const string DatabaseName = "WalhallaSql_Benchmarks_MsSql";

    private SqlConnection _connection = null!;

    // Rolling offsets, damit Insert-/Delete-Benchmarks immer frische Id-Bereiche
    // verwenden und symmetrisch zu SQLite/Walhalla bleiben.
    private int _insertBatchOffset = SeedRowCount;
    private int _insertIdxOffset = SeedRowCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var masterConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
        using (var master = new SqlConnection(masterConnectionString))
        {
            master.Open();
            using var cmd = master.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];";
            cmd.ExecuteNonQuery();
        }

        _connection = new SqlConnection($"Server=(localdb)\\MSSQLLocalDB;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True;");
        _connection.Open();

        ResetSchema();
        SeedData();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    private void ResetSchema()
    {
        var drops = new[]
        {
            "IF OBJECT_ID('dbo.OrderItems', 'U') IS NOT NULL DROP TABLE OrderItems;",
            "IF OBJECT_ID('dbo.Orders', 'U') IS NOT NULL DROP TABLE Orders;",
            "IF OBJECT_ID('dbo.Customers', 'U') IS NOT NULL DROP TABLE Customers;"
        };

        foreach (var ddl in drops)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        foreach (var ddl in GetSchemaDdl())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }

    [Benchmark]
    public int SelectById()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", 5000);
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectRange()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Email, Region FROM Customers WHERE Id BETWEEN @min AND @max";
        cmd.Parameters.AddWithValue("@min", 1000);
        cmd.Parameters.AddWithValue("@max", 2000);
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectJoin()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, TotalAmount, CustomerId FROM Orders WHERE Id BETWEEN @min AND @max";
        cmd.Parameters.AddWithValue("@min", 1000);
        cmd.Parameters.AddWithValue("@max", 2000);
        using var reader = cmd.ExecuteReader();

        var customerIds = new List<int>();
        while (reader.Read())
            customerIds.Add(reader.GetInt32(2));

        int count = 0;
        foreach (var custId in customerIds)
        {
            using var custCmd = _connection.CreateCommand();
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

        using var tx = _connection.BeginTransaction();
        for (int i = start; i <= end; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", i);
            cmd.Parameters.AddWithValue("@p1", "User " + i);
            cmd.Parameters.AddWithValue("@p2", "user" + i + "@test.com");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = _connection.CreateCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    [Benchmark]
    public int UpdateSingle()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"UPDATE Customers SET Name = 'Updated {DateTime.UtcNow.Ticks}' WHERE Id = 1";
        return cmd.ExecuteNonQuery();
    }

    [Benchmark]
    public int DeleteSingle()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Customers WHERE Id = 2";
        int affected = cmd.ExecuteNonQuery();

        if (affected > 0)
        {
            using var restore = _connection.CreateCommand();
            restore.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'Restored', 'restored@test.com', 'R0')";
            restore.ExecuteNonQuery();
        }
        return affected;
    }

    [Benchmark]
    public int SelectByIndexedEmail()
    {
        using var cmd = _connection.CreateCommand();
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
        using var cmd = _connection.CreateCommand();
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
        using var cmd = _connection.CreateCommand();
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

        using var tx = _connection.BeginTransaction();
        for (int i = start; i <= end; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", i);
            cmd.Parameters.AddWithValue("@p1", "User " + i);
            cmd.Parameters.AddWithValue("@p2", "user" + i + "@test.com");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = _connection.CreateCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    private static IEnumerable<string> GetSchemaDdl()
    {
        yield return @"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name NVARCHAR(100) NOT NULL,
                Email NVARCHAR(100) NOT NULL,
                Region NVARCHAR(50) NOT NULL
            );";
        yield return @"
            CREATE TABLE Orders (
                Id INT PRIMARY KEY,
                CustomerId INT NOT NULL,
                OrderDate DATETIME NOT NULL,
                TotalAmount FLOAT NOT NULL
            );";
        yield return @"
            CREATE TABLE OrderItems (
                Id INT PRIMARY KEY,
                OrderId INT NOT NULL,
                ProductName NVARCHAR(100) NOT NULL,
                Quantity INT NOT NULL,
                UnitPrice FLOAT NOT NULL
            );";
        yield return "CREATE INDEX ix_Customers_Email ON Customers (Email);";
        yield return "CREATE INDEX ix_Customers_Region ON Customers (Region);";
        yield return "CREATE INDEX ix_Orders_CustomerId ON Orders (CustomerId);";
    }

    private void SeedData()
    {
        var rnd = new Random(42);

        using var tx = _connection.BeginTransaction();

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", i);
            cmd.Parameters.AddWithValue("@p1", "Customer " + i);
            cmd.Parameters.AddWithValue("@p2", "cust" + i + "@demo.local");
            cmd.Parameters.AddWithValue("@p3", "R" + (i % 10));
            cmd.ExecuteNonQuery();
        }

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Orders (Id, CustomerId, OrderDate, TotalAmount) VALUES (@p0, @p1, @p2, @p3)";
            cmd.Parameters.AddWithValue("@p0", i);
            cmd.Parameters.AddWithValue("@p1", (i % SeedRowCount) + 1);
            cmd.Parameters.AddWithValue("@p2", DateTime.UtcNow.AddDays(-rnd.Next(365)));
            cmd.Parameters.AddWithValue("@p3", rnd.NextDouble() * 1000);
            cmd.ExecuteNonQuery();
        }

        for (int i = 1; i <= SeedRowCount; i++)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO OrderItems (Id, OrderId, ProductName, Quantity, UnitPrice) VALUES (@p0, @p1, @p2, @p3, @p4)";
            cmd.Parameters.AddWithValue("@p0", i);
            cmd.Parameters.AddWithValue("@p1", (i % SeedRowCount) + 1);
            cmd.Parameters.AddWithValue("@p2", "Product " + i);
            cmd.Parameters.AddWithValue("@p3", rnd.Next(1, 20));
            cmd.Parameters.AddWithValue("@p4", rnd.NextDouble() * 100);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
