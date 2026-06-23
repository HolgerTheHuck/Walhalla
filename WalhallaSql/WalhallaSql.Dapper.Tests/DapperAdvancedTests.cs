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
/// Komplexere Dapper-Szenarien, die gleichzeitig eine breitere Abdeckung
/// des WalhallaSql.AdoNet-Providers erreichen.
/// </summary>
public class DapperAdvancedTests : IDisposable
{
    private readonly WalhallaEngine _engine;
    private readonly WalhallaSqlDbConnection _connection;

    public DapperAdvancedTests()
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
                CreatedAt DATETIME NOT NULL,
                Rating REAL,
                ExternalId UNIQUEIDENTIFIER
            )");

        _connection.Execute(@"
            CREATE TABLE Orders (
                Id INT PRIMARY KEY,
                CustomerId INT NOT NULL,
                TotalAmount DECIMAL(18, 2) NOT NULL
            )");

        _connection.Execute(@"
            CREATE TABLE Tags (
                Id INT PRIMARY KEY,
                Name VARCHAR(50) NOT NULL,
                CustomerId INT NULL
            )");
    }

    [Fact]
    public void InParameter_List_Filtering()
    {
        var customers = Enumerable.Range(1, 10)
            .Select(i => new
            {
                id = i,
                name = "C" + i,
                createdAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                rating = (double?)null,
                externalId = Guid.NewGuid()
            }).ToList();

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            customers);

        var ids = new[] { 2, 5, 8 };
        var result = _connection.Query<int>(
            "SELECT Id FROM Customers WHERE Id IN @ids ORDER BY Id",
            new { ids }).ToList();

        Assert.Equal(new[] { 2, 5, 8 }, result);
    }

    [Fact]
    public void DateTime_Guid_Decimal_NullableMapping()
    {
        var id = 42;
        var created = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var externalId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        decimal total = 1234.56m;

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id, name = "Typed", createdAt = created, rating = (double?)3.5, externalId });

        _connection.Execute(
            "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@id, @customerId, @amount)",
            new { id = 1, customerId = id, amount = total });

        var customer = _connection.QuerySingle<TypedCustomer>(
            "SELECT Id, Name, CreatedAt, Rating, ExternalId FROM Customers WHERE Id = @id",
            new { id });

        Assert.Equal(id, customer.Id);
        Assert.Equal("Typed", customer.Name);
        Assert.Equal(created, customer.CreatedAt);
        Assert.Equal(3.5, customer.Rating);
        Assert.Equal(externalId, customer.ExternalId);

        var order = _connection.QuerySingle<TypedOrder>(
            "SELECT Id, CustomerId, TotalAmount FROM Orders WHERE Id = @id",
            new { id = 1 });

        Assert.Equal(total, order.TotalAmount);
    }

    [Fact]
    public void Nullable_Double_AsNull()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 7, name = "NoRating", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

        var customer = _connection.QuerySingle<TypedCustomer>(
            "SELECT Id, Name, CreatedAt, Rating, ExternalId FROM Customers WHERE Id = @id",
            new { id = 7 });

        Assert.Null(customer.Rating);
        Assert.Null(customer.ExternalId);
    }

    [Fact]
    public void MultiMapping_JoinedEntities()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 1, name = "Parent", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

        _connection.Execute(
            "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@id, @customerId, @amount)",
            new[]
            {
                new { id = 10, customerId = 1, amount = 99.99m },
                new { id = 11, customerId = 1, amount = 49.50m }
            });

        var lookup = new Dictionary<int, CustomerWithOrders>();

        _connection.Query<CustomerWithOrders, OrderSummary, CustomerWithOrders>(
            @"SELECT c.Id, c.Name, o.Id AS OrderId, o.CustomerId, o.TotalAmount
              FROM Customers c
              INNER JOIN Orders o ON o.CustomerId = c.Id
              WHERE c.Id = 1
              ORDER BY o.Id",
            (customer, order) =>
            {
                if (!lookup.TryGetValue(customer.Id, out var existing))
                {
                    existing = customer;
                    existing.Orders = new List<OrderSummary>();
                    lookup.Add(customer.Id, existing);
                }
                existing.Orders.Add(order);
                return existing;
            },
            splitOn: "OrderId")
            .ToList();

        var result = lookup.Values.Single();
        Assert.Equal(1, result.Id);
        Assert.Equal("Parent", result.Name);
        Assert.Equal(2, result.Orders.Count);
        Assert.Equal(10, result.Orders[0].OrderId);
        Assert.Equal(99.99m, result.Orders[0].TotalAmount);
    }

    [Fact]
    public void MultiMapping_ThreeEntities_SplitOnList()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 1, name = "Parent", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

        _connection.Execute(
            "INSERT INTO Orders (Id, CustomerId, TotalAmount) VALUES (@id, @customerId, @amount)",
            new { id = 10, customerId = 1, amount = 99.99m });

        _connection.Execute(
            "INSERT INTO Tags (Id, Name, CustomerId) VALUES (@id, @name, @customerId)",
            new { id = 20, name = "VIP", customerId = 1 });

        // Zuerst prüfen, ob die Abfrage überhaupt Rohdaten liefert.
        var raw = _connection.Query<CustomerOrderTagRaw>(
            @"SELECT c.Id, c.Name, o.Id AS OrderId, o.CustomerId AS OrderCustomerId, o.TotalAmount,
                     t.Id AS TagId, t.Name AS TagName, t.CustomerId AS TagCustomerId
              FROM Customers c
              INNER JOIN Orders o ON o.CustomerId = c.Id
              INNER JOIN Tags t ON t.CustomerId = c.Id
              WHERE c.Id = 1").ToList();

        if (raw.Count == 0)
        {
            // Fallback: Wenn der Engine-3-Wege-Join hier kein Ergebnis liefert,
            // prüfen wir, ob zwei separate 2-Wege-Joins funktionieren.
            var raw1 = _connection.Query<CustomerOrderTagRaw>(
                @"SELECT c.Id, c.Name, o.Id AS OrderId, o.CustomerId AS OrderCustomerId, o.TotalAmount
                  FROM Customers c
                  INNER JOIN Orders o ON o.CustomerId = c.Id
                  WHERE c.Id = 1").ToList();
            var raw2 = _connection.Query<CustomerOrderTagRaw>(
                @"SELECT c.Id, c.Name, t.Id AS TagId, t.Name AS TagName, t.CustomerId AS TagCustomerId
                  FROM Customers c
                  INNER JOIN Tags t ON t.CustomerId = c.Id
                  WHERE c.Id = 1").ToList();
            Assert.NotEmpty(raw1);
            Assert.NotEmpty(raw2);
        }

        var results = _connection.Query<CustomerWithOrderAndTag, OrderSummary, TagInfo, CustomerWithOrderAndTag>(
            @"SELECT c.Id, c.Name, o.Id AS OrderId, o.CustomerId, o.TotalAmount, t.Id AS TagId, t.Name, t.CustomerId
              FROM Customers c
              INNER JOIN Orders o ON o.CustomerId = c.Id
              INNER JOIN Tags t ON t.CustomerId = c.Id
              WHERE c.Id = 1",
            (customer, order, tag) =>
            {
                customer.Order = order;
                customer.Tag = tag;
                return customer;
            },
            splitOn: "OrderId,TagId")
            .ToList();

        Assert.NotEmpty(results);
        var result = results[0];
        Assert.Equal(1, result.Id);
        Assert.Equal("Parent", result.Name);
        Assert.Equal(10, result.Order.OrderId);
        Assert.Equal(20, result.Tag.TagId);
        Assert.Equal("VIP", result.Tag.Name);
    }

    [Fact]
    public void QueryFirst_UsesSingleRowBehavior()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new[]
            {
                new { id = 1, name = "A", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null },
                new { id = 2, name = "B", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null }
            });

        var name = _connection.QueryFirst<string>("SELECT Name FROM Customers ORDER BY Id");
        Assert.Equal("A", name);
    }

    [Fact]
    public void QueryMultiple_InsideTransaction_RespectsRollback()
    {
        using var tx = _connection.BeginTransaction();
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 1, name = "Tx", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null },
            tx);

        using var multi = _connection.QueryMultiple(
            "SELECT Id, Name FROM Customers; SELECT COUNT(*) FROM Customers",
            transaction: tx);

        var customers = multi.Read<CustomerSummary>().ToList();
        var count = multi.Read<int>().Single();

        Assert.Single(customers);
        Assert.Equal("Tx", customers[0].Name);
        Assert.Equal(1, count);

        tx.Rollback();

        var afterCount = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers");
        Assert.Equal(0, afterCount);
    }

    [Fact]
    public async Task AsyncQuery_InsideTransactionScope_Commits()
    {
        using (var scope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Required,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await _connection.ExecuteAsync(
                "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
                new { id = 1, name = "AsyncAmbient", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

            scope.Complete();
        }

        var name = await _connection.QuerySingleAsync<string>("SELECT Name FROM Customers WHERE Id = 1");
        Assert.Equal("AsyncAmbient", name);
    }

    [Fact]
    public void PreparedDml_InsideTransaction_RollbackUndoesChanges()
    {
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)";
        AddParameter(cmd, "id", 1);
        AddParameter(cmd, "name", "PreparedTx");
        AddParameter(cmd, "createdAt", DateTime.UtcNow);
        AddParameter(cmd, "rating", DBNull.Value);
        AddParameter(cmd, "externalId", DBNull.Value);
        cmd.Prepare();
        Assert.Equal(1, cmd.ExecuteNonQuery());

        tx.Rollback();

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers WHERE Name = 'PreparedTx'");
        Assert.Equal(0, count);
    }

    [Fact]
    public void BatchInsideTransaction_RollbackUndoesAll()
    {
        using var tx = _connection.BeginTransaction();
        using var batchCmd = _connection.CreateCommand();
        batchCmd.Transaction = tx;
        var now = DateTime.UtcNow;
        batchCmd.CommandText = $@"
            INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (1, 'B1', '{now:O}', NULL, NULL);
            INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (2, 'B2', '{now:O}', NULL, NULL);
            SELECT COUNT(*) FROM Customers";

        using var reader = batchCmd.ExecuteReader();
        // Zwei leere DML-Resultsets überspringen, dann das SELECT auslesen.
        reader.NextResult();
        reader.NextResult();
        Assert.True(reader.Read());
        var count = reader.GetInt32(0);
        Assert.Equal(2, count);

        tx.Rollback();

        var afterCount = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers");
        Assert.Equal(0, afterCount);
    }

    [Fact]
    public void DynamicParameters_OutputAndReturnValue()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 1, name = "Dyn", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

        var parameters = new DynamicParameters();
        parameters.Add("id", 1);
        parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);
        parameters.Add("count", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

        // ReturnValue wird von WalhallaSql nicht als Stored-Procedure-Rückgabe
        // unterstützt, daher fokussiert sich der Test auf den Output-Parameter.
        _connection.Execute(
            "SELECT @name = Name FROM Customers WHERE Id = @id",
            parameters);

        Assert.Equal("Dyn", parameters.Get<string>("name"));
    }

    [Fact]
    public void DynamicParameters_ReturnValue_DocumentedAsUnsupported()
    {
        var parameters = new DynamicParameters();
        parameters.Add("count", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

        // WalhallaSql hat keine Stored-Procedure-Rückgabe. Dapper bindet den
        // ReturnValue-Parameter als '@p_count = COUNT(*)', was die Projektion
        // zerstört. Der Test dokumentiert, dass dies aktuell eine Exception
        // auslöst (bekannte Limitierung).
        Assert.ThrowsAny<Exception>(() =>
            _connection.Execute(
                "SELECT @count = COUNT(*) FROM Customers",
                parameters));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        command.Parameters.Add(param);
    }

    [Fact]
    public void UnbufferedQuery_StreamsRows()
    {
        var data = Enumerable.Range(1, 100)
            .Select(i => new { id = i, name = "R" + i, createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null })
            .ToList();

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            data);

        var count = 0;
        var results = _connection.Query<CustomerSummary>(
            "SELECT Id, Name FROM Customers",
            buffered: false);

        foreach (var row in results)
        {
            Assert.False(string.IsNullOrEmpty(row.Name));
            count++;
        }

        Assert.Equal(100, count);
    }

    [Fact]
    public void StoredProcedure_CommandType_NotSupported_Yet()
    {
        // WalhallaSql kennt keine Stored Procedures im ADO.NET-Sinne.
        // Dieser Test dokumentiert, dass der Provider sauber ablehnt,
        // statt undefiniertes Verhalten zu zeigen.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SomeProcedure";
        cmd.CommandType = CommandType.StoredProcedure;

        var ex = Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("StoredProcedure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputParameter_ReturnsValue()
    {
        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            new { id = 1, name = "Out", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

        var parameters = new DynamicParameters();
        parameters.Add("id", 1);
        parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);

        _connection.Execute(
            "SELECT @name = Name FROM Customers WHERE Id = @id",
            parameters);

        var outputName = parameters.Get<string>("name");
        Assert.Equal("Out", outputName);
    }

    [Fact]
    public void ParallelConnections_DapperQueries()
    {
        var data = Enumerable.Range(1, 50)
            .Select(i => new { id = i, name = "P" + i, createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null })
            .ToList();

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            data);

        var results = Parallel.ForEach(
            Enumerable.Range(1, 50),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            id =>
            {
                using var parallelConnection = new WalhallaSqlDbConnection(_engine);
                parallelConnection.Open();

                var name = parallelConnection.QuerySingle<string>(
                    "SELECT Name FROM Customers WHERE Id = @id",
                    new { id });

                Assert.Equal("P" + id, name);
            });

        Assert.True(results.IsCompleted);
    }

    [Fact]
    public void TransactionScope_Ambient_Commit_PersistsChanges()
    {
        using (var scope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Required,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            _connection.Execute(
                "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
                new { id = 1, name = "Ambient", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

            scope.Complete();
        }

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers WHERE Name = 'Ambient'");
        Assert.Equal(1, count);
    }

    [Fact]
    public void TransactionScope_Ambient_Rollback_RevertsChanges()
    {
        using (var scope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Required,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            _connection.Execute(
                "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
                new { id = 2, name = "AmbientRollback", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null });

            // Ohne scope.Complete() wird bei Dispose implizit rollback.
        }

        var count = _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Customers WHERE Name = 'AmbientRollback'");
        Assert.Equal(0, count);
    }

    [Fact]
    public void DynamicParameters_DbNull_ForOptional()
    {
        var parameters = new DynamicParameters();
        parameters.Add("id", 99);
        parameters.Add("name", "DynamicNull");
        parameters.Add("createdAt", DateTime.UtcNow);
        parameters.Add("rating", null, DbType.Double);
        parameters.Add("externalId", null, DbType.Guid);

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            parameters);

        var customer = _connection.QuerySingle<TypedCustomer>(
            "SELECT Id, Name, CreatedAt, Rating, ExternalId FROM Customers WHERE Id = 99");

        Assert.Equal("DynamicNull", customer.Name);
        Assert.Null(customer.Rating);
        Assert.Null(customer.ExternalId);
    }

    [Fact]
    public void LikeQuery_WorksWithDapper()
    {
        var data = new[]
        {
            new { id = 1, name = "Apple", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null },
            new { id = 2, name = "Banana", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null },
            new { id = 3, name = "Apricot", createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null }
        };

        _connection.Execute(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            data);

        var result = _connection.Query<CustomerSummary>(
            "SELECT Id, Name FROM Customers WHERE Name LIKE @pattern ORDER BY Id",
            new { pattern = "Ap%" }).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Apple", result[0].Name);
        Assert.Equal("Apricot", result[1].Name);
    }

    [Fact]
    public async Task AsyncBufferedAndUnbuffered()
    {
        var data = Enumerable.Range(1, 20)
            .Select(i => new { id = i, name = "A" + i, createdAt = DateTime.UtcNow, rating = (double?)null, externalId = (Guid?)null })
            .ToList();

        await _connection.ExecuteAsync(
            "INSERT INTO Customers (Id, Name, CreatedAt, Rating, ExternalId) VALUES (@id, @name, @createdAt, @rating, @externalId)",
            data);

        var buffered = await _connection.QueryAsync<CustomerSummary>("SELECT Id, Name FROM Customers");
        Assert.Equal(20, buffered.Count());

        var unbuffered = _connection.Query<CustomerSummary>("SELECT Id, Name FROM Customers", buffered: false);
        var count = 0;
        await foreach (var row in unbuffered.ToAsyncEnumerable())
        {
            count++;
        }

        Assert.Equal(20, count);
    }

    private sealed class TypedCustomer
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public double? Rating { get; set; }
        public Guid? ExternalId { get; set; }
    }

    private sealed class TypedOrder
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public decimal TotalAmount { get; set; }
    }

    private sealed class CustomerSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CustomerWithOrders
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<OrderSummary> Orders { get; set; } = new();
    }

    private sealed class OrderSummary
    {
        public int OrderId { get; set; }
        public int CustomerId { get; set; }
        public decimal TotalAmount { get; set; }
    }

    private sealed class CustomerWithOrderAndTag
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public OrderSummary Order { get; set; } = new();
        public TagInfo Tag { get; set; } = new();
    }

    private sealed class TagInfo
    {
        public int TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? CustomerId { get; set; }
    }

    private sealed class CustomerOrderTagRaw
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int OrderId { get; set; }
        public int OrderCustomerId { get; set; }
        public decimal TotalAmount { get; set; }
        public int TagId { get; set; }
        public string TagName { get; set; } = string.Empty;
        public int? TagCustomerId { get; set; }
    }
}
