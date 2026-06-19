using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using BenchmarkDotNet.Attributes;
using WalhallaSql.AdoNet;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// ADO.NET-Provider-Benchmark. Spiegelt <see cref="WalhallaBenchmarkBase"/>,
/// nutzt aber ausschließlich <see cref="WalhallaSqlDbConnection"/> und
/// <see cref="WalhallaSqlDbCommand"/> mit parametrisierten Abfragen.
/// </summary>
public abstract class WalhallaSqlAdoNetBenchmarkBase
{
    protected WalhallaEngine Engine = null!;
    protected WalhallaSqlDbConnection Connection = null!;

    // Reusable commands for the read-heavy benchmarks. They are prepared once
    // in GlobalSetup and reused across iterations to exercise the ADO.NET
    // prepared-statement cache and the engine plan cache.
    private WalhallaSqlDbCommand _cmdSelectById = null!;
    private WalhallaSqlDbCommand _cmdSelectRange = null!;
    private WalhallaSqlDbCommand _cmdSelectJoinOuter = null!;
    private WalhallaSqlDbCommand _cmdSelectJoinInner = null!;
    private WalhallaSqlDbCommand _cmdSelectByEmail = null!;
    private WalhallaSqlDbCommand _cmdSelectByRegion = null!;
    private WalhallaSqlDbCommand _cmdSelectByName = null!;
    private WalhallaSqlDbCommand _cmdInsert = null!;

    // Cached parameter references so we can update values without a dictionary lookup.
    private DbParameter _paramSelectByIdId = null!;
    private DbParameter _paramSelectRangeMin = null!;
    private DbParameter _paramSelectRangeMax = null!;
    private DbParameter _paramJoinOuterMin = null!;
    private DbParameter _paramJoinOuterMax = null!;
    private DbParameter _paramJoinInnerId = null!;
    private DbParameter _paramEmail = null!;
    private DbParameter _paramRegion = null!;
    private DbParameter _paramName = null!;
    private DbParameter _paramInsertId = null!;
    private DbParameter _paramInsertName = null!;
    private DbParameter _paramInsertEmail = null!;
    private DbParameter _paramInsertRegion = null!;

    protected const int SeedRowCount = 10_000;

    // Rolling offsets for Insert+Delete benchmarks so each iteration uses a
    // fresh, disjoint Id range, matching the engine-direct benchmark.
    private int _insertBatchOffset = SeedRowCount;
    private int _insertIdxOffset = SeedRowCount;

    private protected virtual WalhallaEngine CreateEngine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return new WalhallaEngine(new WalhallaOptions(tempDir));
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Engine = CreateEngine();
        Connection = new WalhallaSqlDbConnection(Engine);
        Connection.Open();

        foreach (var ddl in GetSchemaDdl())
        {
            using var cmd = CreateWalhallaCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        SeedData();
        Engine.Checkpoint();

        // Prepare reusable read commands.
        _cmdSelectById = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id", out _paramSelectByIdId, "@id");
        _cmdSelectRange = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Id BETWEEN @min AND @max", out _paramSelectRangeMin, "@min", out _paramSelectRangeMax, "@max");
        _cmdSelectJoinOuter = PrepareCommand("SELECT Id, TotalAmount, CustomerId FROM Orders WHERE Id BETWEEN @min AND @max", out _paramJoinOuterMin, "@min", out _paramJoinOuterMax, "@max");
        _cmdSelectJoinInner = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id", out _paramJoinInnerId, "@id");
        _cmdSelectByEmail = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Email = @email", out _paramEmail, "@email");
        _cmdSelectByRegion = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Region = @region", out _paramRegion, "@region");
        _cmdSelectByName = PrepareCommand("SELECT Id, Name, Email, Region FROM Customers WHERE Name = @name", out _paramName, "@name");

        _cmdInsert = CreateWalhallaCommand();
        _cmdInsert.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
        _paramInsertId = AddParameter(_cmdInsert, "@p0");
        _paramInsertName = AddParameter(_cmdInsert, "@p1");
        _paramInsertEmail = AddParameter(_cmdInsert, "@p2");
        _paramInsertRegion = AddParameter(_cmdInsert, "@p3");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _cmdSelectById?.Dispose();
        _cmdSelectRange?.Dispose();
        _cmdSelectJoinOuter?.Dispose();
        _cmdSelectJoinInner?.Dispose();
        _cmdSelectByEmail?.Dispose();
        _cmdSelectByRegion?.Dispose();
        _cmdSelectByName?.Dispose();
        _cmdInsert?.Dispose();
        Connection?.Dispose();
        Engine?.Dispose();
    }

    [Benchmark]
    public int SelectById()
    {
        _paramSelectByIdId.Value = 5000L;
        using var reader = _cmdSelectById.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectRange()
    {
        _paramSelectRangeMin.Value = 1000L;
        _paramSelectRangeMax.Value = 2000L;
        using var reader = _cmdSelectRange.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectRangeMaterialized()
    {
        _paramSelectRangeMin.Value = 1000L;
        _paramSelectRangeMax.Value = 2000L;
        using var reader = _cmdSelectRange.ExecuteReader();
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
        _paramJoinOuterMin.Value = 1000L;
        _paramJoinOuterMax.Value = 2000L;

        var customerIds = new List<long>();
        using (var reader = _cmdSelectJoinOuter.ExecuteReader())
        {
            while (reader.Read())
                customerIds.Add(reader.GetInt64(2));
        }

        int count = 0;
        foreach (var custId in customerIds)
        {
            _paramJoinInnerId.Value = custId;
            using var custReader = _cmdSelectJoinInner.ExecuteReader();
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
        _cmdInsert.Transaction = tx;
        for (int i = start; i <= end; i++)
        {
            _paramInsertId.Value = (long)i;
            _paramInsertName.Value = "User " + i;
            _paramInsertEmail.Value = "user" + i + "@test.com";
            _paramInsertRegion.Value = "R" + (i % 10);
            _cmdInsert.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = CreateWalhallaCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    [Benchmark]
    public int UpdateSingle()
    {
        using var cmd = CreateWalhallaCommand();
        cmd.CommandText = $"UPDATE Customers SET Name = 'Updated {DateTime.UtcNow.Ticks}' WHERE Id = 1";
        return cmd.ExecuteNonQuery();
    }

    [Benchmark]
    public int DeleteSingle()
    {
        using var cmd = CreateWalhallaCommand();
        cmd.CommandText = "DELETE FROM Customers WHERE Id = 2";
        int affected = cmd.ExecuteNonQuery();

        if (affected > 0)
        {
            using var restore = CreateWalhallaCommand();
            restore.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'Restored', 'restored@test.com', 'R0')";
            restore.ExecuteNonQuery();
        }
        return affected;
    }

    [Benchmark]
    public int SelectByIndexedEmail()
    {
        _paramEmail.Value = "cust5000@demo.local";
        using var reader = _cmdSelectByEmail.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectByIndexedRegion()
    {
        _paramRegion.Value = "R5";
        using var reader = _cmdSelectByRegion.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark]
    public int SelectByNonIndexedName()
    {
        _paramName.Value = "Customer 5000";
        using var reader = _cmdSelectByName.ExecuteReader();
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
        _cmdInsert.Transaction = tx;
        for (int i = start; i <= end; i++)
        {
            _paramInsertId.Value = (long)i;
            _paramInsertName.Value = "User " + i;
            _paramInsertEmail.Value = "user" + i + "@test.com";
            _paramInsertRegion.Value = "R" + (i % 10);
            _cmdInsert.ExecuteNonQuery();
        }
        tx.Commit();

        using var delCmd = CreateWalhallaCommand();
        delCmd.CommandText = $"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}";
        delCmd.ExecuteNonQuery();

        return 100;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private WalhallaSqlDbCommand PrepareCommand(string sql, out DbParameter param, string paramName)
    {
        var cmd = CreateWalhallaCommand();
        cmd.CommandText = sql;
        param = AddParameter(cmd, paramName);
        return cmd;
    }

    private WalhallaSqlDbCommand PrepareCommand(string sql,
        out DbParameter param1, string name1,
        out DbParameter param2, string name2)
    {
        var cmd = CreateWalhallaCommand();
        cmd.CommandText = sql;
        param1 = AddParameter(cmd, name1);
        param2 = AddParameter(cmd, name2);
        return cmd;
    }

    private static DbParameter AddParameter(WalhallaSqlDbCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    private WalhallaSqlDbCommand CreateWalhallaCommand()
        => (WalhallaSqlDbCommand)Connection.CreateCommand();

    private static IEnumerable<string> GetSchemaDdl()
    {
        yield return @"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL,
                Email VARCHAR(100) NOT NULL,
                Region VARCHAR(50) NOT NULL
            );";
        yield return @"
            CREATE TABLE Orders (
                Id INT PRIMARY KEY,
                CustomerId INT NOT NULL,
                OrderDate TEXT NOT NULL,
                TotalAmount REAL NOT NULL
            );";
        yield return @"
            CREATE TABLE OrderItems (
                Id INT PRIMARY KEY,
                OrderId INT NOT NULL,
                ProductName VARCHAR(100) NOT NULL,
                Quantity INT NOT NULL,
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

        using (var cmd = CreateWalhallaCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Customers (Id, Name, Email, Region) VALUES (@p0, @p1, @p2, @p3)";
            var p0 = AddParameter(cmd, "@p0");
            var p1 = AddParameter(cmd, "@p1");
            var p2 = AddParameter(cmd, "@p2");
            var p3 = AddParameter(cmd, "@p3");

            for (int i = 1; i <= SeedRowCount; i++)
            {
                p0.Value = (long)i;
                p1.Value = "Customer " + i;
                p2.Value = "cust" + i + "@demo.local";
                p3.Value = "R" + (i % 10);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = CreateWalhallaCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Orders (Id, CustomerId, OrderDate, TotalAmount) VALUES (@p0, @p1, @p2, @p3)";
            var p0 = AddParameter(cmd, "@p0");
            var p1 = AddParameter(cmd, "@p1");
            var p2 = AddParameter(cmd, "@p2");
            var p3 = AddParameter(cmd, "@p3");

            for (int i = 1; i <= SeedRowCount; i++)
            {
                p0.Value = (long)i;
                p1.Value = (long)((i % SeedRowCount) + 1);
                p2.Value = DateTime.UtcNow.AddDays(-rnd.Next(365)).ToString("O");
                p3.Value = rnd.NextDouble() * 1000;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = CreateWalhallaCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO OrderItems (Id, OrderId, ProductName, Quantity, UnitPrice) VALUES (@p0, @p1, @p2, @p3, @p4)";
            var p0 = AddParameter(cmd, "@p0");
            var p1 = AddParameter(cmd, "@p1");
            var p2 = AddParameter(cmd, "@p2");
            var p3 = AddParameter(cmd, "@p3");
            var p4 = AddParameter(cmd, "@p4");

            for (int i = 1; i <= SeedRowCount; i++)
            {
                p0.Value = (long)i;
                p1.Value = (long)((i % SeedRowCount) + 1);
                p2.Value = "Product " + i;
                p3.Value = (long)rnd.Next(1, 20);
                p4.Value = rnd.NextDouble() * 100;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlAdoNetInMemoryBenchmark : WalhallaSqlAdoNetBenchmarkBase
{
    private protected override WalhallaEngine CreateEngine() => WalhallaEngine.InMemory();
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlAdoNetDiskBenchmark : WalhallaSqlAdoNetBenchmarkBase { }
