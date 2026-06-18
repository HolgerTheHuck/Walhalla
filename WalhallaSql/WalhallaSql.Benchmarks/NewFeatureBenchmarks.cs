using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace WalhallaSql.Benchmarks;

/// <summary>
/// Benchmarks for Phase 2-5 features.
/// Each benchmark creates/disposes its own engine to avoid state leakage.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class NewFeatureInMemoryBenchmark
{
    // ── Phase 2: Correlated subquery ─────────────────────────────────────────

    [Benchmark]
    public int CorrelatedExists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE C_corr (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE O_corr (Id INT PRIMARY KEY, Cid INT, Amt DOUBLE)");
        for (int i = 0; i < 500; i++)
        {
            engine.Execute($"INSERT INTO C_corr (Id, Name) VALUES ({i}, 'Cust{i}')");
            engine.Execute($"INSERT INTO O_corr (Id, Cid, Amt) VALUES ({i}, {i}, {i * 10.0})");
        }
        return engine.Execute(
            "SELECT Name FROM C_corr WHERE EXISTS (SELECT 1 FROM O_corr WHERE O_corr.Cid = C_corr.Id)").Rows.Count;
    }

    // ── Phase 3: CTE ─────────────────────────────────────────────────────────

    [Benchmark]
    public int Cte_Simple()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T_cte (Id INT PRIMARY KEY, Val INT)");
        for (int i = 0; i < 200; i++)
            engine.Execute($"INSERT INTO T_cte (Id, Val) VALUES ({i}, {i})");
        return engine.Execute(
            "WITH cte AS (SELECT Id, Val FROM T_cte WHERE Val >= 100) SELECT * FROM cte WHERE Val < 150").Rows.Count;
    }

    // ── Phase 3: Window functions ────────────────────────────────────────────

    [Benchmark]
    public int Window_RowNumber()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T_win (Id INT PRIMARY KEY, Cat STRING)");
        for (int i = 0; i < 500; i++)
            engine.Execute($"INSERT INTO T_win (Id, Cat) VALUES ({i}, 'C{(i % 5)}')");
        return engine.Execute(
            "SELECT Id, ROW_NUMBER() OVER (PARTITION BY Cat ORDER BY Id) AS rn FROM T_win").Rows.Count;
    }

    // ── Phase 3: UNION ───────────────────────────────────────────────────────

    [Benchmark]
    public int Union_TwoTables()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A_un (Id INT PRIMARY KEY, Val STRING)");
        engine.Execute("CREATE TABLE B_un (Id INT PRIMARY KEY, Val STRING)");
        for (int i = 0; i < 200; i++)
        {
            engine.Execute($"INSERT INTO A_un (Id, Val) VALUES ({i}, 'A{i}')");
            engine.Execute($"INSERT INTO B_un (Id, Val) VALUES ({i + 200}, 'B{i}')");
        }
        return engine.Execute("SELECT Val FROM A_un UNION SELECT Val FROM B_un").Rows.Count;
    }

    // ── Phase 4: ALTER TABLE ─────────────────────────────────────────────────

    [Benchmark]
    public int AlterTable_AddColumn()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T_alt (Id INT PRIMARY KEY, Name STRING)");
        for (int i = 0; i < 500; i++)
            engine.Execute($"INSERT INTO T_alt (Id, Name) VALUES ({i}, 'N{i}')");
        engine.Execute("ALTER TABLE T_alt ADD COLUMN Extra INT DEFAULT 42");
        return engine.Execute("SELECT * FROM T_alt WHERE Extra = 42").Rows.Count;
    }

    // ── Phase 4: CREATE VIEW ─────────────────────────────────────────────────

    [Benchmark]
    public int View_Select()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T_vw (Id INT PRIMARY KEY, Val INT)");
        for (int i = 0; i < 500; i++)
            engine.Execute($"INSERT INTO T_vw (Id, Val) VALUES ({i}, {i})");
        engine.Execute("CREATE VIEW V_bench AS SELECT Id, Val FROM T_vw WHERE Val >= 250");
        return engine.Execute("SELECT * FROM V_bench").Rows.Count;
    }

    // ── Phase 4: Foreign Keys ────────────────────────────────────────────────

    [Benchmark]
    public int ForeignKey_Enforcement()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE P_fk (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE C_fk (Id INT PRIMARY KEY, Pid INT, Val STRING, " +
                       "FOREIGN KEY (Pid) REFERENCES P_fk(Id))");
        for (int i = 0; i < 200; i++)
        {
            engine.Execute($"INSERT INTO P_fk (Id, Name) VALUES ({i}, 'P{i}')");
            engine.Execute($"INSERT INTO C_fk (Id, Pid, Val) VALUES ({i}, {i}, 'V{i}')");
        }
        return engine.Execute("SELECT c.Val, p.Name FROM C_fk c INNER JOIN P_fk p ON c.Pid = p.Id").Rows.Count;
    }

    // ── Phase 5: Stored Procedures ───────────────────────────────────────────

    [Benchmark]
    public int Procedure_Call()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T_proc (Id INT PRIMARY KEY, Msg STRING)");
        engine.Execute(@"
            CREATE PROCEDURE BenchProc
                @msg STRING
            AS
            BEGIN
                INSERT INTO T_proc (Id, Msg) VALUES (1, @msg);
            END");
        engine.Execute("EXEC BenchProc @msg = 'bench'");
        return engine.Execute("SELECT * FROM T_proc").Rows.Count;
    }

    // ── Phase 5: Triggers ────────────────────────────────────────────────────

    [Benchmark]
    public int Trigger_Fire()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Main_trig (Id INT PRIMARY KEY, Data STRING)");
        engine.Execute("CREATE TABLE Audit_trig (Id INT PRIMARY KEY, Msg STRING)");
        engine.Execute(@"
            CREATE TRIGGER trg_Bench ON Main_trig AFTER INSERT AS
            BEGIN
                INSERT INTO Audit_trig (Id, Msg) VALUES (1, 'inserted');
            END");
        engine.Execute("INSERT INTO Main_trig (Id, Data) VALUES (1, 'test')");
        return engine.Execute("SELECT * FROM Audit_trig").Rows.Count;
    }

    // ── Phase 2: INSERT ... SELECT ───────────────────────────────────────────

    [Benchmark]
    public int InsertSelect()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Src_is (Id INT PRIMARY KEY, Val INT)");
        engine.Execute("CREATE TABLE Dst_is (Id INT PRIMARY KEY, Val INT)");
        for (int i = 0; i < 500; i++)
            engine.Execute($"INSERT INTO Src_is (Id, Val) VALUES ({i}, {i * 2})");
        engine.Execute("INSERT INTO Dst_is (Id, Val) SELECT Id, Val FROM Src_is WHERE Val < 500");
        return engine.Execute("SELECT * FROM Dst_is").Rows.Count;
    }
}

/// <summary>
/// SQLite equivalents for the new-feature benchmarks.
/// Each benchmark creates/disposes its own in-memory SQLite database.
/// </summary>
[ShortRunJob]
[WarmupCount(1)]
[IterationCount(3)]
[MemoryDiagnoser]
public class NewFeatureSqliteBenchmark
{
    private SqliteConnection NewConnection()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    // ── Phase 2: Correlated subquery ─────────────────────────────────────────

    [Benchmark]
    public int CorrelatedExists()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE C_corr (Id INTEGER PRIMARY KEY, Name TEXT)");
        Execute(conn, "CREATE TABLE O_corr (Id INTEGER PRIMARY KEY, Cid INTEGER, Amt REAL)");
        for (int i = 0; i < 500; i++)
        {
            Execute(conn, $"INSERT INTO C_corr (Id, Name) VALUES ({i}, 'Cust{i}')");
            Execute(conn, $"INSERT INTO O_corr (Id, Cid, Amt) VALUES ({i}, {i}, {i * 10.0})");
        }
        return QueryCount(conn,
            "SELECT Name FROM C_corr WHERE EXISTS (SELECT 1 FROM O_corr WHERE O_corr.Cid = C_corr.Id)");
    }

    // ── Phase 3: CTE ─────────────────────────────────────────────────────────

    [Benchmark]
    public int Cte_Simple()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE T_cte (Id INTEGER PRIMARY KEY, Val INTEGER)");
        for (int i = 0; i < 200; i++)
            Execute(conn, $"INSERT INTO T_cte (Id, Val) VALUES ({i}, {i})");
        return QueryCount(conn,
            "WITH cte AS (SELECT Id, Val FROM T_cte WHERE Val >= 100) SELECT * FROM cte WHERE Val < 150");
    }

    // ── Phase 3: Window functions ────────────────────────────────────────────

    [Benchmark]
    public int Window_RowNumber()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE T_win (Id INTEGER PRIMARY KEY, Cat TEXT)");
        for (int i = 0; i < 500; i++)
            Execute(conn, $"INSERT INTO T_win (Id, Cat) VALUES ({i}, 'C{(i % 5)}')");
        return QueryCount(conn,
            "SELECT Id, ROW_NUMBER() OVER (PARTITION BY Cat ORDER BY Id) AS rn FROM T_win");
    }

    // ── Phase 3: UNION ───────────────────────────────────────────────────────

    [Benchmark]
    public int Union_TwoTables()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE A_un (Id INTEGER PRIMARY KEY, Val TEXT)");
        Execute(conn, "CREATE TABLE B_un (Id INTEGER PRIMARY KEY, Val TEXT)");
        for (int i = 0; i < 200; i++)
        {
            Execute(conn, $"INSERT INTO A_un (Id, Val) VALUES ({i}, 'A{i}')");
            Execute(conn, $"INSERT INTO B_un (Id, Val) VALUES ({i + 200}, 'B{i}')");
        }
        return QueryCount(conn, "SELECT Val FROM A_un UNION SELECT Val FROM B_un");
    }

    // ── Phase 4: ALTER TABLE ─────────────────────────────────────────────────

    [Benchmark]
    public int AlterTable_AddColumn()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE T_alt (Id INTEGER PRIMARY KEY, Name TEXT)");
        for (int i = 0; i < 500; i++)
            Execute(conn, $"INSERT INTO T_alt (Id, Name) VALUES ({i}, 'N{i}')");
        Execute(conn, "ALTER TABLE T_alt ADD COLUMN Extra INTEGER DEFAULT 42");
        return QueryCount(conn, "SELECT * FROM T_alt WHERE Extra = 42");
    }

    // ── Phase 4: CREATE VIEW ─────────────────────────────────────────────────

    [Benchmark]
    public int View_Select()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE T_vw (Id INTEGER PRIMARY KEY, Val INTEGER)");
        for (int i = 0; i < 500; i++)
            Execute(conn, $"INSERT INTO T_vw (Id, Val) VALUES ({i}, {i})");
        Execute(conn, "CREATE VIEW V_bench AS SELECT Id, Val FROM T_vw WHERE Val >= 250");
        return QueryCount(conn, "SELECT * FROM V_bench");
    }

    // ── Phase 4: Foreign Keys ────────────────────────────────────────────────

    [Benchmark]
    public int ForeignKey_Enforcement()
    {
        using var conn = NewConnection();
        Execute(conn, "PRAGMA foreign_keys = ON");
        Execute(conn, "CREATE TABLE P_fk (Id INTEGER PRIMARY KEY, Name TEXT)");
        Execute(conn, "CREATE TABLE C_fk (Id INTEGER PRIMARY KEY, Pid INTEGER, Val TEXT, " +
                       "FOREIGN KEY (Pid) REFERENCES P_fk(Id))");
        for (int i = 0; i < 200; i++)
        {
            Execute(conn, $"INSERT INTO P_fk (Id, Name) VALUES ({i}, 'P{i}')");
            Execute(conn, $"INSERT INTO C_fk (Id, Pid, Val) VALUES ({i}, {i}, 'V{i}')");
        }
        return QueryCount(conn, "SELECT c.Val, p.Name FROM C_fk c INNER JOIN P_fk p ON c.Pid = p.Id");
    }

    // ── Phase 5: Triggers ────────────────────────────────────────────────────

    [Benchmark]
    public int Trigger_Fire()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE Main_trig (Id INTEGER PRIMARY KEY, Data TEXT)");
        Execute(conn, "CREATE TABLE Audit_trig (Id INTEGER PRIMARY KEY, Msg TEXT)");
        Execute(conn, @"
            CREATE TRIGGER trg_Bench AFTER INSERT ON Main_trig
            BEGIN
                INSERT INTO Audit_trig (Id, Msg) VALUES (1, 'inserted');
            END");
        Execute(conn, "INSERT INTO Main_trig (Id, Data) VALUES (1, 'test')");
        return QueryCount(conn, "SELECT * FROM Audit_trig");
    }

    // ── Phase 2: INSERT ... SELECT ───────────────────────────────────────────

    [Benchmark]
    public int InsertSelect()
    {
        using var conn = NewConnection();
        Execute(conn, "CREATE TABLE Src_is (Id INTEGER PRIMARY KEY, Val INTEGER)");
        Execute(conn, "CREATE TABLE Dst_is (Id INTEGER PRIMARY KEY, Val INTEGER)");
        for (int i = 0; i < 500; i++)
            Execute(conn, $"INSERT INTO Src_is (Id, Val) VALUES ({i}, {i * 2})");
        Execute(conn, "INSERT INTO Dst_is (Id, Val) SELECT Id, Val FROM Src_is WHERE Val < 500");
        return QueryCount(conn, "SELECT * FROM Dst_is");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int QueryCount(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }
}
