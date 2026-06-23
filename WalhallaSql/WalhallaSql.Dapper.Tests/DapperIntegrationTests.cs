using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using WalhallaSql.AdoNet;
using Xunit;

namespace WalhallaSql.Dapper.Tests;

/// <summary>
/// Integrationstests für Dapper über die WalhallaSql ADO.NET-Anbieterschnittstelle.
/// Deckt gängige Micro-ORM-Szenarien ab: Queries, Inserts, Updates, Deletes,
/// Transaktionen, dynamische Ergebnisse und asynchrone Aufrufe.
/// </summary>
public class DapperIntegrationTests : IDisposable
{
    private readonly WalhallaEngine _engine;
    private readonly WalhallaSqlDbConnection _connection;

    public DapperIntegrationTests()
    {
        _engine = WalhallaEngine.InMemory();
        _connection = new WalhallaSqlDbConnection(_engine);
        _connection.Open();
        InitializeSchema();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _engine?.Dispose();
    }

    private void InitializeSchema()
    {
        _connection.Execute(@"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL,
                Email VARCHAR(100) NOT NULL,
                Region VARCHAR(50) NOT NULL
            )");

        _connection.Execute(@"
            CREATE TABLE Orders (
                Id INT PRIMARY KEY,
                CustomerId INT NOT NULL,
                TotalAmount REAL NOT NULL
            )");
    }

    [Fact]
    public void QuerySingle_ReturnsMappedRow()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 1, name = "Alice", email = "alice@example.com", region = "EU" });

        var customer = _connection.QuerySingle<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 1 });

        Assert.Equal(1, customer.Id);
        Assert.Equal("Alice", customer.Name);
        Assert.Equal("alice@example.com", customer.Email);
        Assert.Equal("EU", customer.Region);
    }

    [Fact]
    public void QueryFirstOrDefault_UnknownId_ReturnsNull()
    {
        var customer = _connection.QueryFirstOrDefault<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 999 });

        Assert.Null(customer);
    }

    [Fact]
    public void QueryMany_ReturnsMappedList()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new[]
            {
                new { id = 1, name = "Alice", email = "alice@example.com", region = "EU" },
                new { id = 2, name = "Bob", email = "bob@example.com", region = "US" },
                new { id = 3, name = "Carol", email = "carol@example.com", region = "EU" }
            });

        var customers = _connection.Query<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Region = @region ORDER BY Id",
            new { region = "EU" }).ToList();

        Assert.Equal(2, customers.Count);
        Assert.Equal("Alice", customers[0].Name);
        Assert.Equal("Carol", customers[1].Name);
    }

    [Fact]
    public void Execute_InsertUpdateDelete_AffectsRows()
    {
        var inserted = _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 10, name = "Dave", email = "dave@example.com", region = "US" });
        Assert.Equal(1, inserted);

        var updated = _connection.Execute(
            "UPDATE Customers SET Name = @name WHERE Id = @id",
            new { id = 10, name = "David" });
        Assert.Equal(1, updated);

        var deleted = _connection.Execute(
            "DELETE FROM Customers WHERE Id = @id",
            new { id = 10 });
        Assert.Equal(1, deleted);

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers");
        Assert.Equal(0, count);
    }

    [Fact]
    public void ExecuteScalar_ReturnsSingleValue()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new[]
            {
                new { id = 1, name = "Alice", email = "alice@example.com", region = "EU" },
                new { id = 2, name = "Bob", email = "bob@example.com", region = "US" }
            });

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers");
        Assert.Equal(2, count);
    }

    [Fact]
    public void Transaction_Commit_PersistsChanges()
    {
        using var tx = _connection.BeginTransaction();
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 20, name = "Eve", email = "eve@example.com", region = "EU" },
            tx);
        tx.Commit();

        var customer = _connection.QuerySingle<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 20 });
        Assert.Equal("Eve", customer.Name);
    }

    [Fact]
    public void Transaction_Rollback_RevertsChanges()
    {
        using var tx = _connection.BeginTransaction();
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 21, name = "Frank", email = "frank@example.com", region = "US" },
            tx);
        tx.Rollback();

        var customer = _connection.QueryFirstOrDefault<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 21 });
        Assert.Null(customer);
    }

    [Fact]
    public void DynamicResult_MapsToDynamic()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 30, name = "Grace", email = "grace@example.com", region = "EU" });

        var row = _connection.QueryFirst(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 30 });

        Assert.Equal(30, (int)row.Id);
        Assert.Equal("Grace", (string)row.Name);
    }

    [Fact]
    public void QueryMultiple_ReturnsMultipleResultSets()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 1, name = "Alice", email = "alice@example.com", region = "EU" });
        _connection.Execute(
            "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@id, @customerId, @amount)",
            new { id = 100, customerId = 1, amount = 99.99 });

        using var multi = _connection.QueryMultiple(
            "SELECT Id, Name FROM Customers; SELECT Id, CustomerId, TotalAmount FROM Orders");

        var customers = multi.Read<CustomerSummary>().ToList();
        var orders = multi.Read<Order>().ToList();

        Assert.Single(customers);
        Assert.Equal("Alice", customers[0].Name);
        Assert.Single(orders);
        Assert.Equal(100, orders[0].Id);
    }

    [Fact]
    public async Task AsyncQueryAndExecute_Works()
    {
        await _connection.ExecuteAsync(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 40, name = "Heidi", email = "heidi@example.com", region = "EU" });

        var customer = await _connection.QuerySingleAsync<Customer>(
            "SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id",
            new { id = 40 });

        Assert.Equal("Heidi", customer.Name);
    }

    [Fact]
    public void BulkInsertWithDapper_InsertsManyRows()
    {
        var customers = Enumerable.Range(1, 100)
            .Select(i => new { id = i, name = "Customer " + i, email = "cust" + i + "@example.com", region = "R" + (i % 10) })
            .ToList();

        var affected = _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            customers);

        Assert.Equal(100, affected);

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers");
        Assert.Equal(100, count);
    }

    [Fact]
    public void AnonTypeParameterMapping_WorksWithoutExplicitDbType()
    {
        var affected = _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 50, name = "Ivan", email = "ivan@example.com", region = "US" });

        Assert.Equal(1, affected);
    }

    [Fact]
    public void PreparedInsert_WithParameters_InsertsRows()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)";
        AddParameter(cmd, "id", 100);
        AddParameter(cmd, "name", "Prepared");
        AddParameter(cmd, "email", "prep@example.com");
        AddParameter(cmd, "region", "EU");
        cmd.Prepare();

        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(1, affected);

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers WHERE Id = 100");
        Assert.Equal(1, count);
    }

    [Fact]
    public void PreparedUpdate_WithParameters_UpdatesRows()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 200, name = "Before", email = "before@example.com", region = "US" });

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Customers SET Name = @name WHERE Id = @id";
        AddParameter(cmd, "id", 200);
        AddParameter(cmd, "name", "After");
        cmd.Prepare();

        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(1, affected);

        var updatedName = _connection.QuerySingle<string>("SELECT Name FROM Customers WHERE Id = 200");
        Assert.Equal("After", updatedName);
    }

    [Fact]
    public void PreparedDelete_WithParameters_DeletesRows()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@id, @name, @email, @region)",
            new { id = 300, name = "ToDelete", email = "del@example.com", region = "EU" });

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Customers WHERE Id = @id";
        AddParameter(cmd, "id", 300);
        cmd.Prepare();

        var affected = cmd.ExecuteNonQuery();
        Assert.Equal(1, affected);

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers WHERE Id = 300");
        Assert.Equal(0, count);
    }

    [Fact]
    public void Batch_InsertAndSelectViaExecuteReader()
    {
        using var batchCmd = _connection.CreateCommand();
        batchCmd.CommandText = @"
            INSERT INTO Customers (Id, Name, Email, Region) VALUES (1000, 'Batch', 'batch@example.com', 'US');
            SELECT Id, Name FROM Customers WHERE Id = 1000;
            UPDATE Customers SET Name = 'BatchUpdated' WHERE Id = 1000;
            SELECT Name FROM Customers WHERE Id = 1000";

        using var reader = batchCmd.ExecuteReader();
        reader.NextResult(); // überspringe leeres DML-Resultset

        Assert.True(reader.Read());
        Assert.Equal(1000, reader.GetInt32(0));
        Assert.Equal("Batch", reader.GetString(1));
        reader.Close();

        var name = _connection.QuerySingle<string>("SELECT Name FROM Customers WHERE Id = 1000");
        Assert.Equal("BatchUpdated", name);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        command.Parameters.Add(param);
    }

    private sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
    }

    private sealed class CustomerSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public double TotalAmount { get; set; }
    }
}
