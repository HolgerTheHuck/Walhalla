using System;
using Xunit;

namespace WalhallaSql.Tests;

public class CheckConstraintTests
{
    // ── Basic numeric range ──────────────────────────────────────────────────

    [Fact]
    public void Check_NumericRange_RejectsViolatingInsert()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Age >= 0))");

        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 5)");
        var ex = Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Age) VALUES (2, -1)"));
        Assert.Equal("23514", ex.SqlState);

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Check_NumericRange_AllowsBoundaryValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Age >= 0))");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 0)");
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── NULL handling (three-valued logic) ───────────────────────────────────

    [Fact]
    public void Check_NullValue_IsTreatedAsSatisfied()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Age >= 0))");

        // Age IS NULL → predicate is UNKNOWN, not FALSE → row passes.
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, NULL)");
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Check_NullValue_PassesEvenWhenComparisonWouldFail()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Score INT, CHECK (Score > 100))");

        // NULL > 100 is UNKNOWN → allowed.
        engine.Execute("INSERT INTO T (Id, Score) VALUES (1, NULL)");
        // 50 > 100 is FALSE → rejected.
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Score) VALUES (2, 50)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── String / LIKE ────────────────────────────────────────────────────────

    [Fact]
    public void Check_StringLike_EnforcesPattern()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Users (Id INT PRIMARY KEY, Email STRING, CHECK (Email LIKE '%@%'))");

        engine.Execute("INSERT INTO Users (Id, Email) VALUES (1, 'a@b.com')");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO Users (Id, Email) VALUES (2, 'invalid')"));
        Assert.Single(engine.Execute("SELECT * FROM Users").Rows);
    }

    // ── Multi-column predicate ───────────────────────────────────────────────

    [Fact]
    public void Check_MultiColumn_ComparesTwoColumns()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Spans (Id INT PRIMARY KEY, StartV INT, EndV INT, CHECK (EndV >= StartV))");

        engine.Execute("INSERT INTO Spans (Id, StartV, EndV) VALUES (1, 5, 10)");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO Spans (Id, StartV, EndV) VALUES (2, 10, 5)"));
        Assert.Single(engine.Execute("SELECT * FROM Spans").Rows);
    }

    // ── BETWEEN / IN ─────────────────────────────────────────────────────────

    [Fact]
    public void Check_Between_EnforcesRange()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Pct INT, CHECK (Pct BETWEEN 0 AND 100))");

        engine.Execute("INSERT INTO T (Id, Pct) VALUES (1, 50)");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Pct) VALUES (2, 150)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Check_InList_EnforcesMembership()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Status STRING, CHECK (Status IN ('A', 'B', 'C')))");

        engine.Execute("INSERT INTO T (Id, Status) VALUES (1, 'B')");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Status) VALUES (2, 'Z')"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── IS NULL / IS NOT NULL ────────────────────────────────────────────────

    [Fact]
    public void Check_IsNotNull_RejectsNull()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING, CHECK (Name IS NOT NULL))");

        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'x')");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Name) VALUES (2, NULL)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── Function-based predicate ─────────────────────────────────────────────

    [Fact]
    public void Check_LengthFunction_EnforcesMinimum()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Code STRING, CHECK (LENGTH(Code) >= 3))");

        engine.Execute("INSERT INTO T (Id, Code) VALUES (1, 'abcd')");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Code) VALUES (2, 'ab')"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── Inline column-level CHECK ────────────────────────────────────────────

    [Fact]
    public void Check_InlineColumnLevel_IsEnforced()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Qty INT CHECK (Qty > 0))");

        engine.Execute("INSERT INTO T (Id, Qty) VALUES (1, 5)");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Qty) VALUES (2, 0)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── Named CONSTRAINT CHECK ───────────────────────────────────────────────

    [Fact]
    public void Check_NamedConstraint_IsEnforced()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute(
            "CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CONSTRAINT ck_age CHECK (Age >= 18))");

        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 21)");
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Age) VALUES (2, 10)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    // ── UPDATE enforcement ───────────────────────────────────────────────────

    [Fact]
    public void Check_OnUpdate_RejectsViolatingChange()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Age >= 0))");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 5)");

        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("UPDATE T SET Age = -10 WHERE Id = 1"));

        var result = engine.Execute("SELECT Age FROM T WHERE Id = 1");
        Assert.Equal(5, result.Rows[0]["Age"]);
    }

    [Fact]
    public void Check_OnUpdate_AllowsValidChange()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Age >= 0))");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 5)");

        engine.Execute("UPDATE T SET Age = 42 WHERE Id = 1");
        Assert.Equal(42, engine.Execute("SELECT Age FROM T WHERE Id = 1").Rows[0]["Age"]);
    }

    // ── ALTER TABLE ADD / DROP CONSTRAINT ────────────────────────────────────

    [Fact]
    public void Check_AlterAddConstraint_EnforcesAfterwards()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT)");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 5)");

        engine.Execute("ALTER TABLE T ADD CONSTRAINT ck_age CHECK (Age >= 0)");

        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Age) VALUES (2, -3)"));
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Check_AlterAddConstraint_RejectsWhenExistingRowViolates()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT)");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (1, -5)");

        var ex = Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("ALTER TABLE T ADD CONSTRAINT ck_age CHECK (Age >= 0)"));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public void Check_AlterDropConstraint_RemovesEnforcement()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CONSTRAINT ck_age CHECK (Age >= 0))");

        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Age) VALUES (1, -1)"));

        engine.Execute("ALTER TABLE T DROP CONSTRAINT ck_age");
        engine.Execute("INSERT INTO T (Id, Age) VALUES (2, -1)");
        Assert.Single(engine.Execute("SELECT * FROM T").Rows);
    }

    [Fact]
    public void Check_AlterAddCheck_Unnamed_IsEnforced()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Qty INT)");
        engine.Execute("ALTER TABLE T ADD CHECK (Qty > 0)");

        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Qty) VALUES (1, 0)"));
    }

    // ── DDL-time validation ──────────────────────────────────────────────────

    [Fact]
    public void Check_UnknownColumn_FailsAtCreate()
    {
        using var engine = WalhallaEngine.InMemory();
        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CHECK (Missing >= 0))"));
    }

    // ── Persistence round-trip ───────────────────────────────────────────────

    [Fact]
    public void Check_SurvivesReload()
    {
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WalhallaSql.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempPath);
        try
        {
            using (var engine = WalhallaEngine.Open(tempPath))
            {
                engine.Execute(
                    "CREATE TABLE T (Id INT PRIMARY KEY, Age INT, CONSTRAINT ck_age CHECK (Age >= 0))");
                engine.Execute("INSERT INTO T (Id, Age) VALUES (1, 5)");
                engine.Checkpoint();
            }

            using (var engine = WalhallaEngine.Open(tempPath))
            {
                // Constraint must still be enforced after reload.
                var ex = Assert.Throws<WalhallaConstraintException>(() =>
                    engine.Execute("INSERT INTO T (Id, Age) VALUES (2, -7)"));
                Assert.Equal("23514", ex.SqlState);

                engine.Execute("INSERT INTO T (Id, Age) VALUES (3, 9)");
                Assert.Equal(2, engine.Execute("SELECT * FROM T").Rows.Count);
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(tempPath, true); } catch { /* best-effort */ }
        }
    }
}
