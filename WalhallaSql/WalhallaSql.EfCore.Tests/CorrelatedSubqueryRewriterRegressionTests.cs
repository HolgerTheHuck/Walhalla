using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql;
using Xunit;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Regression tests verifying that the CorrelatedSubqueryRewriter correctly transforms
/// correlated scalar-subquery projections and WHERE EXISTS patterns to JOIN-based queries.
/// </summary>
[Trait("Category", "CorrelatedSubqueryRewriterGate")]
public sealed class CorrelatedSubqueryRewriterRegressionTests
{
    // ------------------------------------------------------------------------
    // Scalar subquery projections ? LEFT JOIN
    // ------------------------------------------------------------------------

    [Fact]
    public void Scalar_subquery_projections_rewritten_to_left_join_returns_correct_values()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (1, 'Alpha', 10, 20, 1)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (2, 'Beta',  11, 21, 2)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (3, 'Gamma', 12, 22, 3)");

        // Doc 10 has German name, doc 20 has German description
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'Alpha-Name-DE')");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'Alpha-Descr-DE')");
        // Doc 11 exists but has no German translation for name (only English)
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (11, 'en', 'Beta-Name-EN')");
        // Doc 21 has German description
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (21, 'de', 'Beta-Descr-DE')");
        // Row 3 has no documents at all � both LEFT JOIN columns should be NULL.

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name,
       (SELECT doc.Content FROM Documents doc WHERE doc.Id = d.NameResource         AND doc.Lang = 'de') AS NameContent,
       (SELECT doc.Content FROM Documents doc WHERE doc.Id = d.BeschreibungResource AND doc.Lang = 'de') AS BeschrContent
FROM ModuleDetails d
WHERE d.MandantId IN (1, 2, 3)");

        Assert.Equal(3, rows.Count);

        var alpha = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("Alpha-Name-DE", alpha["NameContent"]?.ToString());
        Assert.Equal("Alpha-Descr-DE", alpha["BeschrContent"]?.ToString());

        var beta = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Null(beta["NameContent"]);   // No German name doc for Beta
        Assert.Equal("Beta-Descr-DE", beta["BeschrContent"]?.ToString());

        var gamma = rows.Single(r => r["Id"]?.ToString() == "3");
        Assert.Null(gamma["NameContent"]);
        Assert.Null(gamma["BeschrContent"]);
    }

    [Fact]
    public void Scalar_subquery_projections_without_outer_where_returns_all_rows()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (1, 'A', 10, 20, 1)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (2, 'B', 11, 21, 2)");

        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'A-Name')");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'A-Descr')");

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name,
       (SELECT doc.Content FROM Documents doc WHERE doc.Id = d.NameResource         AND doc.Lang = 'de') AS NameContent,
       (SELECT doc.Content FROM Documents doc WHERE doc.Id = d.BeschreibungResource AND doc.Lang = 'de') AS BeschrContent
FROM ModuleDetails d");

        Assert.Equal(2, rows.Count);

        var a = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("A-Name",  a["NameContent"]?.ToString());
        Assert.Equal("A-Descr", a["BeschrContent"]?.ToString());

        var b = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Null(b["NameContent"]);
        Assert.Null(b["BeschrContent"]);
    }

    // ------------------------------------------------------------------------
    // WHERE EXISTS ? INNER JOIN
    // ------------------------------------------------------------------------

    [Fact]
    public void Exists_filters_rewritten_to_inner_join_returns_only_matching_rows()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (1, 'Alpha', 10, 20, 1)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (2, 'Beta',  11, 21, 2)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (3, 'Gamma', 12, 22, 3)");

        // Alpha: both name and description docs exist in German ? should appear.
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'Alpha-Name-DE')");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'Alpha-Descr-DE')");
        // Beta: only description doc exists ? name EXISTS fails ? should NOT appear.
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (21, 'de', 'Beta-Descr-DE')");
        // Gamma: no documents ? should NOT appear.

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name
FROM ModuleDetails d
WHERE EXISTS (SELECT 1 FROM Documents doc WHERE doc.Id = d.NameResource         AND doc.Lang = 'de')
  AND EXISTS (SELECT 1 FROM Documents doc WHERE doc.Id = d.BeschreibungResource AND doc.Lang = 'de')
  AND d.MandantId IN (1, 2, 3)");

        var only = Assert.Single(rows);
        Assert.Equal("1", only["Id"]?.ToString());
        Assert.Equal("Alpha", only["Name"]?.ToString());
    }

    [Fact]
    public void Exists_filters_without_residual_where_returns_only_matching_rows()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (1, 'X', 10, 20, 1)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (2, 'Y', 11, 21, 2)");

        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'X-Name')");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'X-Descr')");

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name
FROM ModuleDetails d
WHERE EXISTS (SELECT 1 FROM Documents doc WHERE doc.Id = d.NameResource         AND doc.Lang = 'de')
  AND EXISTS (SELECT 1 FROM Documents doc WHERE doc.Id = d.BeschreibungResource AND doc.Lang = 'de')");

        var only = Assert.Single(rows);
        Assert.Equal("1", only["Id"]?.ToString());
    }

    // ------------------------------------------------------------------------
    // TOP 1 scalar subquery projections ? LEFT JOIN  (A.2)
    // ------------------------------------------------------------------------

    [Fact]
    public void Top1_scalar_subquery_without_order_by_rewrites_to_left_join()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, MandantId) VALUES (1, 'Alpha', 10, 1)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, MandantId) VALUES (2, 'Beta', 11, 2)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, MandantId) VALUES (3, 'Gamma', 12, 3)");

        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'Alpha-DE')");
        // Row 2 has no German document ? NULL expected.
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (11, 'en', 'Beta-EN')");
        // Row 3 has no document at all ? NULL expected.

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name,
       (SELECT TOP 1 doc.Content FROM Documents doc WHERE doc.Id = d.NameResource AND doc.Lang = 'de') AS NameContent
FROM ModuleDetails d
WHERE d.MandantId IN (1, 2, 3)");

        Assert.Equal(3, rows.Count);

        var alpha = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("Alpha-DE", alpha["NameContent"]?.ToString());

        var beta = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Null(beta["NameContent"]);

        var gamma = rows.Single(r => r["Id"]?.ToString() == "3");
        Assert.Null(gamma["NameContent"]);
    }

    [Fact]
    public void Top1_scalar_subquery_multiple_projections_rewrite_correctly()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource, MandantId) VALUES (1, 'A', 10, 20, 1)");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'A-Name')");
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'A-Descr')");

        var rows = scope.QueryAll(@"
SELECT d.Id,
       (SELECT TOP 1 doc.Content FROM Documents doc WHERE doc.Id = d.NameResource         AND doc.Lang = 'de') AS NameContent,
       (SELECT TOP 1 doc.Content FROM Documents doc WHERE doc.Id = d.BeschreibungResource AND doc.Lang = 'de') AS BeschrContent
FROM ModuleDetails d");

        var only = Assert.Single(rows);
        Assert.Equal("A-Name",  only["NameContent"]?.ToString());
        Assert.Equal("A-Descr", only["BeschrContent"]?.ToString());
    }

    // ------------------------------------------------------------------------
    // Aggregate scalar subquery projections ? LEFT JOIN derived table  (A.2b)
    // ------------------------------------------------------------------------

    [Fact]
    public void Aggregate_MAX_subquery_rewritten_to_derived_table_left_join()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (3, 'Carol')");  // no orders

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 250)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 2, 80)");

        var rows = scope.QueryAll(@"
SELECT c.Id, c.Name,
       (SELECT MAX(o.Amount) FROM Orders o WHERE o.CustomerId = c.Id) AS MaxAmt
FROM Customers c");

        Assert.Equal(3, rows.Count);

        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("250", alice["MaxAmt"]?.ToString());

        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("80", bob["MaxAmt"]?.ToString());

        var carol = rows.Single(r => r["Id"]?.ToString() == "3");
        Assert.Null(carol["MaxAmt"]);
    }

    [Fact]
    public void Aggregate_MIN_subquery_rewritten_correctly()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 250)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 2, 80)");

        var rows = scope.QueryAll(@"
SELECT c.Id,
       (SELECT MIN(o.Amount) FROM Orders o WHERE o.CustomerId = c.Id) AS MinAmt
FROM Customers c");

        Assert.Equal(2, rows.Count);
        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("100", alice["MinAmt"]?.ToString());
        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("80", bob["MinAmt"]?.ToString());
    }

    [Fact]
    public void Aggregate_SUM_subquery_rewritten_correctly()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 250)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 2, 80)");

        var rows = scope.QueryAll(@"
SELECT c.Id,
       (SELECT SUM(o.Amount) FROM Orders o WHERE o.CustomerId = c.Id) AS TotalAmt
FROM Customers c");

        Assert.Equal(2, rows.Count);
        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("350", alice["TotalAmt"]?.ToString());
        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("80", bob["TotalAmt"]?.ToString());
    }

    [Fact]
    public void Aggregate_COUNT_star_returns_zero_for_no_match()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (3, 'Carol')");  // no orders

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (2, 1, 250)");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (3, 2, 80)");

        var rows = scope.QueryAll(@"
SELECT c.Id,
       (SELECT COUNT(*) FROM Orders o WHERE o.CustomerId = c.Id) AS OrderCount
FROM Customers c");

        Assert.Equal(3, rows.Count);

        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("2", alice["OrderCount"]?.ToString());

        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("1", bob["OrderCount"]?.ToString());

        var carol = rows.Single(r => r["Id"]?.ToString() == "3");
        // COUNT(*) must return 0, not NULL, for rows with no matches.
        Assert.Equal("0", carol["OrderCount"]?.ToString());
    }

    [Fact]
    public void Aggregate_COUNT_col_returns_zero_for_no_match()
    {
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount) VALUES (1, 1, 100)");
        // Bob has no orders.

        var rows = scope.QueryAll(@"
SELECT c.Id,
       (SELECT COUNT(o.Id) FROM Orders o WHERE o.CustomerId = c.Id) AS OrderCount
FROM Customers c");

        Assert.Equal(2, rows.Count);
        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("1", alice["OrderCount"]?.ToString());
        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("0", bob["OrderCount"]?.ToString());
    }

    [Fact]
    public void Aggregate_with_alias_only_residual_where_predicate()
    {
        // Inner WHERE has both a correlation predicate AND an alias-only filter.
        // The alias-only filter must end up inside the derived table's WHERE.
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Orders (Id INT PRIMARY KEY, CustomerId INT NOT NULL, Amount INT NOT NULL, Status VARCHAR(20) NOT NULL)");

        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        scope.Exec("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");

        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount, Status) VALUES (1, 1, 100, 'paid')");
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount, Status) VALUES (2, 1, 250, 'pending')");  // filtered out
        scope.Exec("INSERT INTO Orders (Id, CustomerId, Amount, Status) VALUES (3, 2, 80, 'paid')");

        var rows = scope.QueryAll(@"
SELECT c.Id,
       (SELECT SUM(o.Amount) FROM Orders o WHERE o.CustomerId = c.Id AND o.Status = 'paid') AS PaidTotal
FROM Customers c");

        Assert.Equal(2, rows.Count);
        var alice = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("100", alice["PaidTotal"]?.ToString());  // only the 'paid' order
        var bob = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("80", bob["PaidTotal"]?.ToString());
    }

    [Fact]
    public void Aggregate_multi_column_correlation()
    {
        // Two correlation keys: region + product.
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE Targets (Region VARCHAR(10) NOT NULL, Product VARCHAR(10) NOT NULL, Target INT NOT NULL, PRIMARY KEY (Region, Product))");
        scope.Exec("CREATE TABLE Sales (Id INT PRIMARY KEY, Region VARCHAR(10) NOT NULL, Product VARCHAR(10) NOT NULL, Amount INT NOT NULL)");

        scope.Exec("INSERT INTO Targets (Region, Product, Target) VALUES ('EU', 'A', 500)");
        scope.Exec("INSERT INTO Targets (Region, Product, Target) VALUES ('US', 'A', 300)");
        scope.Exec("INSERT INTO Targets (Region, Product, Target) VALUES ('EU', 'B', 200)");

        scope.Exec("INSERT INTO Sales (Id, Region, Product, Amount) VALUES (1, 'EU', 'A', 120)");
        scope.Exec("INSERT INTO Sales (Id, Region, Product, Amount) VALUES (2, 'EU', 'A', 200)");
        scope.Exec("INSERT INTO Sales (Id, Region, Product, Amount) VALUES (3, 'US', 'A', 90)");
        // EU/B has no sales ? NULL expected.

        var rows = scope.QueryAll(@"
SELECT t.Region, t.Product, t.Target,
       (SELECT SUM(s.Amount) FROM Sales s WHERE s.Region = t.Region AND s.Product = t.Product) AS ActualSales
FROM Targets t");

        Assert.Equal(3, rows.Count);

        var euA = rows.Single(r => r["Region"]?.ToString() == "EU" && r["Product"]?.ToString() == "A");
        Assert.Equal("320", euA["ActualSales"]?.ToString());

        var usA = rows.Single(r => r["Region"]?.ToString() == "US");
        Assert.Equal("90", usA["ActualSales"]?.ToString());

        var euB = rows.Single(r => r["Product"]?.ToString() == "B");
        Assert.Null(euB["ActualSales"]);
    }

    [Fact]
    public void Scalar_subquery_projection_with_existing_inner_join_rewrites_correctly()
    {
        // Pattern 3: outer query already has an explicit JOIN; the scalar subquery in SELECT
        // must still be rewritten to a LEFT JOIN (the guard must not block it).
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, BeschreibungResource INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource) VALUES (1, 'Alpha', 10, 20)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource) VALUES (2, 'Beta',  11, 21)");
        scope.Exec("INSERT INTO ModuleDetails (Id, Name, NameResource, BeschreibungResource) VALUES (3, 'Gamma', 12, 22)"); // no name doc ? excluded by JOIN

        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (10, 'de', 'Alpha-Name-DE')");   // name for Alpha
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (20, 'de', 'Alpha-Descr-DE')");  // descr for Alpha
        scope.Exec("INSERT INTO Documents (Id, Lang, Content) VALUES (11, 'de', 'Beta-Name-DE')");    // name for Beta
        // Beta has no German description ? BeschrContent must be NULL

        var rows = scope.QueryAll(@"
SELECT d.Id, d.Name,
       (SELECT doc2.Content FROM Documents doc2 WHERE doc2.Id = d.BeschreibungResource AND doc2.Lang = 'de') AS BeschrContent
FROM ModuleDetails d
JOIN Documents doc ON doc.Id = d.NameResource AND doc.Lang = 'de'");

        // Gamma has no name document ? the INNER JOIN filters it out
        Assert.Equal(2, rows.Count);

        var alpha = rows.Single(r => r["Id"]?.ToString() == "1");
        Assert.Equal("Alpha",          alpha["Name"]?.ToString());
        Assert.Equal("Alpha-Descr-DE", alpha["BeschrContent"]?.ToString());

        var beta = rows.Single(r => r["Id"]?.ToString() == "2");
        Assert.Equal("Beta", beta["Name"]?.ToString());
        Assert.Null(beta["BeschrContent"]);
    }

    [Fact]
    public void Top1_scalar_subquery_with_order_by_is_not_rewritten()
    {
        // TOP 1 + ORDER BY is order-dependent ? the rewriter must leave it untouched.
        // Proof: if it were rewritten to a LEFT JOIN the query would execute without exception;
        // since it is NOT rewritten, the in-process engine rejects the TOP keyword.
        using var scope = RewriterScope.Create();

        scope.Exec("CREATE TABLE ModuleDetails (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, NameResource INT, MandantId INT)");
        scope.Exec("CREATE TABLE Documents (Id INT PRIMARY KEY, Lang VARCHAR(10) NOT NULL, Content VARCHAR(500))");

        Assert.Throws<WalhallaException>(() => scope.QueryAll(@"
SELECT d.Id,
       (SELECT TOP 1 doc.Content FROM Documents doc WHERE doc.Id = d.NameResource AND doc.Lang = 'de' ORDER BY doc.Id) AS NameContent
FROM ModuleDetails d"));
    }

    // ------------------------------------------------------------------------
    // Scope helper
    // ------------------------------------------------------------------------

    private sealed class RewriterScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private RewriterScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static RewriterScope Create()
        {
            var dbPath = Path.Combine(
                Path.GetTempPath(), "LayeredSql", "CorrelatedSubqueryRewriterTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);
            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            return new RewriterScope(dbPath, engine, database);
        }

        public void Exec(string sql)
        {
            using var conn = new WalhallaSqlDbConnection(_database);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryAll(string sql)
        {
            using var conn = new WalhallaSqlDbConnection(_database);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var results = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }

            return results;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dbPath, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
