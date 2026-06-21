using System;
using System.IO;
using System.Linq;
using WalhallaSql.Core;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// End-to-End-Test fuer das manuelle DbUI-Szenario: frische embedded DB,
/// Tabellen, AFTER-INSERT-Trigger mit INSERTED, C# Stored Procedure,
/// INNER/LEFT/RIGHT JOINs und UNION.
/// </summary>
public sealed class ManualDbUiScenarioTests : IDisposable
{
    private readonly string _rootPath;

    public ManualDbUiScenarioTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"walhalla_manual_dbui_{Guid.NewGuid():N}");
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
        catch
        {
            // Aufräumen best-effort.
        }
    }

    [Fact]
    public void Manual_DbUi_Scenario_Full_Run()
    {
        using var engine = CreateEngine();

        // 1. Tabellen anlegen
        engine.Execute("""
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name STRING
            )
            """);
        engine.Execute("""
            CREATE TABLE Products (
                Id INT PRIMARY KEY,
                Name STRING,
                Price INT
            )
            """);
        engine.Execute("""
            CREATE TABLE Orders (
                Id INT PRIMARY KEY,
                CustomerId INT,
                OrderDate STRING
            )
            """);
        engine.Execute("""
            CREATE TABLE OrderDetails (
                Id INT PRIMARY KEY,
                OrderId INT,
                ProductId INT,
                Quantity INT
            )
            """);
        engine.Execute("""
            CREATE TABLE AuditLog (
                Id INT PRIMARY KEY,
                Message STRING
            )
            """);

        // 2. AFTER-INSERT-Trigger mit INSERTED-Pseudo-Tabelle
        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_AuditOrders
            ON Orders AFTER INSERT AS
            BEGIN
                INSERT INTO AuditLog (Id, Message)
                SELECT INSERTED.Id, 'Neue Bestellung eingefuegt'
                FROM INSERTED;
            END
            """);

        // Trigger muss bekannt sein
        var triggers = engine.GetTriggers("Orders");
        Assert.Single(triggers);
        Assert.Equal("trg_AuditOrders", triggers[0].Name);

        // 3. C# Stored Procedure
        engine.Execute("""
            CREATE OR REPLACE PROCEDURE GetCustomerOrders(@customerId INT)
            AS CSHARP BEGIN
                var rows = ctx.Query($"SELECT o.Id, o.OrderDate FROM Orders o WHERE o.CustomerId = {customerId} ORDER BY o.Id");
                return WalhallaResultSet.FromRows(rows);
            END
            """);

        // 4. Stammdaten
        engine.Execute("""
            INSERT INTO Customers (Id, Name) VALUES
            (1, 'Alice'),
            (2, 'Bob'),
            (99, 'Orphan')
            """);

        engine.Execute("""
            INSERT INTO Products (Id, Name, Price) VALUES
            (1, 'Walnuss', 5),
            (2, 'Eiche', 10)
            """);

        // 5. Multi-Row-INSERT, der durch den Trigger AuditLog-Eintraege erzeugt.
        // Jeder Customer bekommt genau eine Order, damit die Join-Tests robust
        // gegen das aktuelle kartesische-Produkt-Verhalten der Engine sind.
        engine.Execute("""
            INSERT INTO Orders (Id, CustomerId, OrderDate) VALUES
            (1, 1, '2026-06-01'),
            (2, 2, '2026-06-10'),
            (3, 99, '2026-06-15')
            """);

        // 6. Order-Details fuer Joins
        engine.Execute("""
            INSERT INTO OrderDetails (Id, OrderId, ProductId, Quantity) VALUES
            (1, 1, 1, 3),
            (2, 2, 2, 5)
            """);

        // 7. AuditLog-Pruefung: 3 Eintraege, beginnend bei Id 1
        var audit = engine.Execute("SELECT * FROM AuditLog ORDER BY Id");
        Assert.Equal(3, audit.Rows.Count);
        Assert.Equal(1, audit.Rows[0]["Id"]);
        Assert.Equal(2, audit.Rows[1]["Id"]);
        Assert.Equal(3, audit.Rows[2]["Id"]);
        Assert.Equal("Neue Bestellung eingefuegt", audit.Rows[0]["Message"]);

        // 8. INNER JOIN
        var innerJoin = engine.Execute("""
            SELECT c.Name, o.Id AS OrderId, o.OrderDate
            FROM Customers c
            INNER JOIN Orders o ON c.Id = o.CustomerId
            ORDER BY o.Id
            """);
        Assert.Equal(3, innerJoin.Rows.Count);
        Assert.Equal("Alice", innerJoin.Rows[0]["Name"]);
        Assert.Equal(1, innerJoin.Rows[0]["OrderId"]);
        Assert.Equal("Bob", innerJoin.Rows[1]["Name"]);
        Assert.Equal(2, innerJoin.Rows[1]["OrderId"]);
        Assert.Equal("Orphan", innerJoin.Rows[2]["Name"]);
        Assert.Equal(3, innerJoin.Rows[2]["OrderId"]);

        // 9. LEFT JOIN
        var leftJoin = engine.Execute("""
            SELECT c.Name, o.Id AS OrderId
            FROM Customers c
            LEFT JOIN Orders o ON c.Id = o.CustomerId
            ORDER BY c.Name
            """);
        Assert.Equal(3, leftJoin.Rows.Count);
        Assert.Equal("Alice", leftJoin.Rows[0]["Name"]);
        Assert.Equal("Bob", leftJoin.Rows[1]["Name"]);
        Assert.Equal("Orphan", leftJoin.Rows[2]["Name"]);

        // 10. RIGHT JOIN
        var rightJoin = engine.Execute("""
            SELECT o.Id AS OrderId, c.Name
            FROM Orders o
            RIGHT JOIN Customers c ON o.CustomerId = c.Id
            ORDER BY o.Id
            """);
        Assert.Equal(3, rightJoin.Rows.Count);
        Assert.Equal(1, rightJoin.Rows[0]["OrderId"]);
        Assert.Equal("Alice", rightJoin.Rows[0]["Name"]);
        Assert.Equal(2, rightJoin.Rows[1]["OrderId"]);
        Assert.Equal("Bob", rightJoin.Rows[1]["Name"]);
        Assert.Equal(3, rightJoin.Rows[2]["OrderId"]);
        Assert.Equal("Orphan", rightJoin.Rows[2]["Name"]);

        // 11. UNION
        var union = engine.Execute("""
            SELECT Id, Name FROM Customers WHERE Id = 1
            UNION
            SELECT Id, Name FROM Customers WHERE Id = 2
            """);
        Assert.Equal(2, union.Rows.Count);
        var unionNames = union.Rows.Select(r => r["Name"]).ToHashSet();
        Assert.Contains("Alice", unionNames);
        Assert.Contains("Bob", unionNames);

        // 12. Stored Procedure ausfuehren
        var spResult = engine.Execute("EXEC GetCustomerOrders @customerId = 1");
        Assert.Single(spResult.Rows);
        Assert.Equal(1, spResult.Rows[0]["Id"]);
        Assert.Equal("2026-06-01", spResult.Rows[0]["OrderDate"]);
    }

    [Fact]
    public void VarBinary_LargeValue_MvccBPlusTree_RoundTrip_ViaPreparedStatement()
    {
        using var engine = CreateEngine();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Data VARBINARY)");

        var payload = new byte[5000];
        new Random(42).NextBytes(payload);
        var hex = Convert.ToHexString(payload);
        engine.Execute($"INSERT INTO T (Id, Data) VALUES (1, X'{hex}')");

        // DbUI nutzt PreparedStatement/ADO.NET, daher diesen Pfad explizit testen.
        var stmt = engine.Prepare("SELECT Data FROM T WHERE Id = 1");
        var result = stmt.Execute();
        Assert.Single(result.Rows);
        var bytes = Assert.IsType<byte[]>(result.Rows[0]["Data"]);
        Assert.Equal(payload, bytes);
    }
}
