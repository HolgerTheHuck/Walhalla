using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using WalhallaSql;
using WalhallaSql.Execution;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;

namespace WalhallaSql.Benchmarks;

internal class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--profile")
        {
            ProfileUpdateDelete();
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    static void ProfileUpdateDelete()
    {
        // Create engine same way as the InMemory benchmark
        var engine = new WalhallaEngine(new WalhallaOptions(":memory:")
        {
            StorageMode = WalhallaSql.Core.StorageMode.InMemory,
            WalSyncMode = WalhallaSql.Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });

        // Setup schema
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL, Email VARCHAR(100) NOT NULL, Region VARCHAR(50) NOT NULL)");
        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (1, 'test', 'test@test.com', 'EU')");
        engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'test2', 'test2@test.com', 'EU')");

        Console.WriteLine("Warmup...");
        for (int i = 0; i < 1000; i++)
            engine.Execute("UPDATE Customers SET Name = 'Updated' WHERE Id = 1");

        const int N = 50000;
        var sw = new System.Diagnostics.Stopwatch();
        var updateSql = "UPDATE Customers SET Name = 'Updated' WHERE Id = 1";
        var deleteSql = "DELETE FROM Customers WHERE Id = 2";

        // 1. Parse only (UPDATE)
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlStatementParser.Parse(updateSql);
        sw.Stop();
        Console.WriteLine($"Parse only (UPDATE): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // 2. Rewrite only (UPDATE)
        sw.Restart();
        for (int i = 0; i < N; i++)
            CorrelatedSubqueryRewriter.Rewrite(updateSql);
        sw.Stop();
        Console.WriteLine($"Rewrite only (UPDATE): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // 3. Full Execute (UPDATE)
        sw.Restart();
        for (int i = 0; i < N; i++)
            engine.Execute(updateSql);
        sw.Stop();
        Console.WriteLine($"Full Execute (UPDATE): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // 4. Parse only (DELETE)
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlStatementParser.Parse(deleteSql);
        sw.Stop();
        Console.WriteLine($"Parse only (DELETE): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // 5. Full Execute (DELETE) — re-insert between each delete
        sw.Restart();
        for (int i = 0; i < N; i++)
        {
            engine.Execute(deleteSql);
            engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'test2', 'test2@test.com', 'EU')");
        }
        sw.Stop();
        Console.WriteLine($"Full Execute (DELETE+INSERT): {sw.Elapsed.TotalNanoseconds / N:F1} ns (includes re-insert)");

        // 6. Profile sub-components of ParseUpdate in detail
        var testSql = "UPDATE Customers SET Name = 'Updated' WHERE Id = 1";
        var normalized = SqlSyntaxText.RemoveTrailingSemicolon(testSql).Trim();

        // Step 1: Full dispatch (all StartsWithKeyword checks)
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlStatementParser.Parse(testSql);
        sw.Stop();
        Console.WriteLine($"  Full Parse (UPDATE): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // Step 2: Parse a simple SELECT for comparison
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlStatementParser.Parse("SELECT Id FROM Customers");
        sw.Stop();
        Console.WriteLine($"  Full Parse (SELECT): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // Step 3: Just the dispatch + ParseUpdate overhead without ParseAssignments/WHERE
        var setIdx = SqlSyntaxText.FindTopLevelKeyword(normalized, "SET", "UPDATE".Length);
        var afterSet = setIdx + "SET".Length;
        var whereIdx = SqlSyntaxText.FindTopLevelKeyword(normalized, "WHERE", afterSet);
        var assignText = normalized[afterSet..(whereIdx >= 0 ? whereIdx : normalized.Length)].Trim();
        var whereText2 = normalized[(whereIdx + "WHERE".Length)..].Trim();

        sw.Restart();
        for (int i = 0; i < N; i++)
        {
            SqlSyntaxText.FindTopLevelKeyword(normalized, "SET", "UPDATE".Length);
            SqlSyntaxText.FindTopLevelKeyword(normalized, "WHERE", afterSet);
            var t = normalized["UPDATE".Length..setIdx].Trim();
            SqlSyntaxText.NormalizeIdentifier(t);
            var a = normalized[afterSet..(whereIdx >= 0 ? whereIdx : normalized.Length)].Trim();
        }
        sw.Stop();
        Console.WriteLine($"  ParseUpdate slicing+keywords: {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // Step 4: ParseAssignments span-based
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlSyntaxText.SplitTopLevel(assignText.AsSpan(), ',', _ => { });
        sw.Stop();
        Console.WriteLine($"  SplitTopLevel (span, noop): {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        // Step 5: ParseWithParameters
        sw.Restart();
        for (int i = 0; i < N; i++)
            SqlWhereParser.ParseWithParameters(whereText2);
        sw.Stop();
        Console.WriteLine($"  ParseWithParameters: {sw.Elapsed.TotalNanoseconds / N:F1} ns");

        ProfileInsertBatchAllocations();

        engine.Dispose();
    }

    static void ProfileInsertBatchAllocations()
    {
        Console.WriteLine();
        Console.WriteLine("=== InsertBatch allocation profiling ===");

        ProfileStorageMode("BPlusTree", WalhallaSql.Core.StorageMode.BPlusTree);
        ProfileStorageMode("MvccBPlusTree", WalhallaSql.Core.StorageMode.MvccBPlusTree);
    }

    static void ProfileStorageMode(string label, WalhallaSql.Core.StorageMode mode)
    {
        var engine = new WalhallaEngine(new WalhallaOptions(Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks", Guid.NewGuid().ToString("N")))
        {
            StorageMode = mode,
            WalSyncMode = WalhallaSql.Core.WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        });

        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL, Email VARCHAR(100) NOT NULL, Region VARCHAR(50) NOT NULL)");
        engine.Execute("CREATE INDEX ix_email ON Customers (Email)");
        engine.Execute("CREATE INDEX ix_region ON Customers (Region)");

        const int BatchSize = 100;
        var rows = new List<object?[]>(BatchSize);
        for (int i = 1; i <= BatchSize; i++)
            rows.Add(new object?[] { i + 100000, "User " + i, "user" + i + "@test.com", "R" + (i % 10) });

        // Warmup
        engine.InsertBatch("Customers", rows);
        engine.Execute("DELETE FROM Customers WHERE Id > 100000");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetTotalAllocatedBytes(true);
        engine.InsertBatch("Customers", rows);
        long after = GC.GetTotalAllocatedBytes(true);
        Console.WriteLine($"[{label}] InsertBatch total allocated ({BatchSize} rows): {(after - before) / 1024.0:F2} KB = {(after - before) / (double)BatchSize:F2} B/row");

        engine.Execute("DELETE FROM Customers WHERE Id > 100000");

        before = GC.GetTotalAllocatedBytes(true);
        engine.Execute("DELETE FROM Customers WHERE Id BETWEEN 100001 AND 100100");
        after = GC.GetTotalAllocatedBytes(true);
        Console.WriteLine($"[{label}] DELETE BETWEEN total allocated: {(after - before) / 1024.0:F2} KB");

        engine.Dispose();
    }
}

public abstract class WalhallaBenchmarkBase
{
    protected WalhallaEngine _engine = null!;
    private protected string? _tempDir;

    // Prepared statements — created once in GlobalSetup.
    private WalhallaPreparedStatement _stmtSelectById = null!;
    private WalhallaPreparedStatement _stmtSelectRange = null!;
    private WalhallaPreparedStatement _stmtSelectJoinOuter = null!;
    private WalhallaPreparedStatement _stmtSelectJoinInner = null!;
    private WalhallaPreparedStatement _stmtSelectByEmail = null!;
    private WalhallaPreparedStatement _stmtSelectByRegion = null!;
    private WalhallaPreparedStatement _stmtSelectByName = null!;
    private WalhallaPreparedStatement _stmtSelectInSubquery = null!;
    private WalhallaPreparedStatement _stmtSelectExistsSubquery = null!;
    private WalhallaPreparedStatement _stmtSelectJoin = null!;
    private WalhallaPreparedStatement _stmtSelectSelfJoin = null!;

    protected const int SeedRowCount = 10_000;

    // Rolling offsets for Insert+Delete benchmarks so each iteration uses a
    // fresh, disjoint Id range. This keeps the user-supplied Id column value
    // aligned with the auto-assigned storage rowid (Walhalla auto-increments
    // rowid per Insert and ignores the provided PK literal). Without this,
    // iterations 2+ would DELETE an empty range and storage would grow
    // unbounded across the benchmark run — masking the actual hot path.
    private int _insertBatchOffset = SeedRowCount;
    private int _insertIdxOffset = SeedRowCount;

    // Reusable row buffer for InsertBatch to avoid per-iteration List/object[] allocations.
    private const int BatchSize = 100;
    private object?[][] _insertBatchRows = null!;

    private protected virtual WalhallaEngine CreateEngine()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WalhallaSql.Benchmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return new WalhallaEngine(CreateOptions(_tempDir));
    }

    private protected virtual WalhallaOptions CreateOptions(string rootPath)
        => new WalhallaOptions(rootPath);

    [GlobalSetup]
    public void GlobalSetup()
    {
        _engine = CreateEngine();

        foreach (var ddl in GetSchemaDdl())
            _engine.Execute(ddl);

        SeedData(_engine, SeedRowCount);
        _engine.Checkpoint();

        // Prepare all statements once.
        _stmtSelectById = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id");
        _stmtSelectRange = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Id BETWEEN @min AND @max");
        _stmtSelectJoinOuter = _engine.Prepare("SELECT Id, TotalAmount, CustomerId FROM Orders WHERE Id BETWEEN @min AND @max");
        _stmtSelectJoinInner = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Id = @id");
        _stmtSelectByEmail = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Email = @email");
        _stmtSelectByRegion = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Region = @region");
        _stmtSelectByName = _engine.Prepare("SELECT Id, Name, Email, Region FROM Customers WHERE Name = @name");
        _stmtSelectInSubquery = _engine.Prepare("SELECT Id, Name FROM Customers WHERE Id IN (SELECT CustomerId FROM Orders WHERE TotalAmount > 500)");
        _stmtSelectExistsSubquery = _engine.Prepare("SELECT Id, Name FROM Customers WHERE EXISTS (SELECT TotalAmount FROM Orders WHERE TotalAmount > 999)");
        _stmtSelectJoin = _engine.Prepare("SELECT c.Name, o.TotalAmount FROM Customers c INNER JOIN Orders o ON c.Id = o.CustomerId WHERE c.Id BETWEEN @min AND @max");
        _stmtSelectSelfJoin = _engine.Prepare("SELECT c1.Name, c2.Name FROM Customers c1 INNER JOIN Customers c2 ON c1.Region = c2.Region WHERE c1.Id <> c2.Id AND c1.Id = @id");

        // Pre-allocate the rolling batch row buffer once.
        _insertBatchRows = new object?[BatchSize][];
        for (int i = 0; i < BatchSize; i++)
            _insertBatchRows[i] = new object?[4];
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _engine?.Dispose();
        if (_tempDir != null)
        {
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }
    }

    [Benchmark]
    public int SelectById()
    {
        _stmtSelectById.Bind("@id", 5000);
        return _stmtSelectById.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectRange()
    {
        _stmtSelectRange.Bind("@min", 1000);
        _stmtSelectRange.Bind("@max", 2000);
        return _stmtSelectRange.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectJoin()
    {
        _stmtSelectJoinOuter.Bind("@min", 1000);
        _stmtSelectJoinOuter.Bind("@max", 2000);
        var orders = _stmtSelectJoinOuter.Execute().Rows;

        int count = 0;
        foreach (var order in orders)
        {
            var custId = order["CustomerId"];
            _stmtSelectJoinInner.Bind("@id", custId);
            count += _stmtSelectJoinInner.Execute().Rows.Count;
        }
        return count;
    }

    [Benchmark]
    public int InsertBatch()
    {
        int start = _insertBatchOffset + 1;
        int end = _insertBatchOffset + BatchSize;
        _insertBatchOffset += BatchSize;

        // Reuse the pre-allocated row buffer; only the mutable values change.
        Span<object?[]> rows = _insertBatchRows;
        for (int i = 0; i < BatchSize; i++)
        {
            int id = start + i;
            var row = rows[i];
            row[0] = id;
            row[1] = "User " + id;
            row[2] = "user" + id + "@test.com";
            row[3] = "R" + (id % 10);
        }

        _engine.InsertBatch("Customers", _insertBatchRows);

        // Cleanup — DELETE the rows just inserted (same Id range).
        _engine.Execute($"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}");
        return BatchSize;
    }

    [Benchmark]
    public int UpdateSingle()
    {
        return _engine.Execute("UPDATE Customers SET Name = 'Updated' WHERE Id = 1").AffectedRows;
    }

    [Benchmark]
    public int DeleteSingle()
    {
        var affected = _engine.Execute("DELETE FROM Customers WHERE Id = 2").AffectedRows;

        if (affected > 0)
            _engine.Execute("INSERT INTO Customers (Id, Name, Email, Region) VALUES (2, 'Restored', 'restored@test.com', 'R0')");

        return affected;
    }

    // ── Index benchmarks ─────────────────────────────────────────────────────

    [Benchmark]
    public int SelectByIndexedEmail()
    {
        _stmtSelectByEmail.Bind("@email", "cust5000@demo.local");
        return _stmtSelectByEmail.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectByIndexedRegion()
    {
        _stmtSelectByRegion.Bind("@region", "R5");
        return _stmtSelectByRegion.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectByNonIndexedName()
    {
        _stmtSelectByName.Bind("@name", "Customer 5000");
        return _stmtSelectByName.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectWithInSubquery()
    {
        return _stmtSelectInSubquery.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectWithExistsSubquery()
    {
        return _stmtSelectExistsSubquery.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectWithJoin()
    {
        _stmtSelectJoin.Bind("@min", 1);
        _stmtSelectJoin.Bind("@max", 1000);
        return _stmtSelectJoin.Execute().Rows.Count;
    }

    [Benchmark]
    public int SelectWithSelfJoin()
    {
        _stmtSelectSelfJoin.Bind("@id", 1);
        return _stmtSelectSelfJoin.Execute().Rows.Count;
    }

    [Benchmark]
    public int InsertWithIndexMaintenance()
    {
        int start = _insertIdxOffset + 1;
        int end = _insertIdxOffset + 100;
        _insertIdxOffset += 100;

        var rows = new List<object?[]>();
        for (int i = start; i <= end; i++)
            rows.Add(new object?[] { i, "User " + i, "user" + i + "@test.com", "R" + (i % 10) });

        _engine.InsertBatch("Customers", rows);

        // Cleanup: remove inserted rows.
        _engine.Execute($"DELETE FROM Customers WHERE Id BETWEEN {start} AND {end}");
        return rows.Count;
    }

    // ── Schema + Seed ──────────────────────────────────────────────────────────

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
        // Secondary indexes for benchmark coverage.
        yield return "CREATE INDEX ix_Customers_Email ON Customers (Email);";
        yield return "CREATE INDEX ix_Customers_Region ON Customers (Region);";
        yield return "CREATE INDEX ix_Orders_CustomerId ON Orders (CustomerId);";
    }

    private static void SeedData(WalhallaEngine engine, int count)
    {
        var rnd = new Random(42);

        var custRows = new List<object?[]>();
        for (int i = 1; i <= count; i++)
        {
            custRows.Add(new object?[] { i, "Customer " + i, "cust" + i + "@demo.local", "R" + (i % 10) });
        }
        engine.InsertBatch("Customers", custRows);

        var orderRows = new List<object?[]>();
        for (int i = 1; i <= count; i++)
        {
            orderRows.Add(new object?[] {
                i,
                (i % count) + 1,
                DateTime.UtcNow.AddDays(-rnd.Next(365)).ToString("O"),
                rnd.NextDouble() * 1000
            });
        }
        engine.InsertBatch("Orders", orderRows);

        var itemRows = new List<object?[]>();
        for (int i = 1; i <= count; i++)
        {
            itemRows.Add(new object?[] {
                i,
                (i % count) + 1,
                "Product " + i,
                rnd.Next(1, 20),
                rnd.NextDouble() * 100
            });
        }
        engine.InsertBatch("OrderItems", itemRows);
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlDiskBenchmark : WalhallaBenchmarkBase { }

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlFastBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaOptions CreateOptions(string rootPath)
    {
        var opts = new WalhallaOptions(rootPath);
        opts.WalSyncMode = WalhallaSql.Core.WalSyncMode.None;
        return opts;
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlInMemoryBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaEngine CreateEngine()
    {
        _tempDir = null; // InMemory manages its own path
        return WalhallaEngine.InMemory();
    }
}

// ── MvccBPlusTree benchmarks ──────────────────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlMvccBPlusTreeMemoryBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaEngine CreateEngine()
    {
        _tempDir = null;
        return new WalhallaEngine(new WalhallaOptions(":memory:")
        {
            StorageMode = WalhallaSql.Core.StorageMode.MvccBPlusTree
        });
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlMvccBPlusTreeDiskBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaOptions CreateOptions(string rootPath)
    {
        var opts = new WalhallaOptions(rootPath)
        {
            StorageMode = WalhallaSql.Core.StorageMode.MvccBPlusTree
        };
        return opts;
    }
}

// ── Legacy BPlusTree benchmarks ──────────────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlBPlusTreeDiskBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaOptions CreateOptions(string rootPath)
    {
        var opts = new WalhallaOptions(rootPath)
        {
            StorageMode = WalhallaSql.Core.StorageMode.BPlusTree
        };
        return opts;
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class WalhallaSqlBPlusTreeFastBenchmark : WalhallaBenchmarkBase
{
    private protected override WalhallaOptions CreateOptions(string rootPath)
    {
        var opts = new WalhallaOptions(rootPath)
        {
            StorageMode = WalhallaSql.Core.StorageMode.BPlusTree,
            WalSyncMode = WalhallaSql.Core.WalSyncMode.None
        };
        return opts;
    }
}

// ── SQLite comparison benchmarks ─────────────────────────────────────────

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class SqliteInMemoryBenchmark : SqliteBenchmarkBase
{
    protected override Microsoft.Data.Sqlite.SqliteConnection CreateConnection()
        => new("Data Source=:memory:;Mode=Memory;Cache=Shared");
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class SqliteDiskBenchmark : SqliteBenchmarkBase
{
    protected override Microsoft.Data.Sqlite.SqliteConnection CreateConnection()
    {
        DbFile = CreateTempDbFile();
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbFile};Mode=ReadWriteCreate");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        { cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; cmd.ExecuteNonQuery(); }
        conn.Close();
        return conn;
    }
}

[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class SqliteFastBenchmark : SqliteBenchmarkBase
{
    protected override Microsoft.Data.Sqlite.SqliteConnection CreateConnection()
    {
        DbFile = CreateTempDbFile();
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbFile};Mode=ReadWriteCreate");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        { cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF;"; cmd.ExecuteNonQuery(); }
        conn.Close();
        return conn;
    }
}
