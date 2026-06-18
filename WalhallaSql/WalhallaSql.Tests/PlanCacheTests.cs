using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WalhallaSql.Tests;

public class PlanCacheTests
{
    [Fact]
    public void Prepare_SameSql_CacheHit()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var ps1 = engine.Prepare("SELECT * FROM T WHERE Id = @p");
        var ps2 = engine.Prepare("SELECT * FROM T WHERE Id = @p");

        // Same plan instance reused from cache.
        Assert.Same(ps1.GetPlan(), ps2.GetPlan());
        Assert.True(engine.PlanCacheHits >= 1);
    }

    [Fact]
    public void Prepare_DifferentSql_CacheMiss()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Name STRING)");

        engine.Prepare("SELECT * FROM A WHERE Id = @p");
        engine.Prepare("SELECT * FROM B WHERE Id = @p");

        // Different SQL → different plans, both miss.
        Assert.True(engine.PlanCacheMisses >= 2);
    }

    [Fact]
    public void Prepare_DdlInvalidatesPlan()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var sql = "SELECT * FROM T WHERE Id = @p";
        var ps1 = engine.Prepare(sql);

        // DDL bumps schema version → new plan on next Prepare.
        engine.Execute("ALTER TABLE T ADD COLUMN Age INT DEFAULT 0");

        var ps2 = engine.Prepare(sql);

        Assert.NotSame(ps1.GetPlan(), ps2.GetPlan());
    }

    [Fact]
    public void Prepare_ParameterStability_CachedPlanWorks()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice'), (2, 'Bob')");

        var ps1 = engine.Prepare("SELECT Name FROM T WHERE Id = @p");

        // First execution with bound param.
        ps1.Bind("@p", 1);
        var r1 = ps1.Execute();
        Assert.Single(r1.Rows);
        Assert.Equal("Alice", r1.Rows[0].GetValue(0));

        // Re-prepare (cache hit) and execute with different param.
        var ps2 = engine.Prepare("SELECT Name FROM T WHERE Id = @p");
        ps2.Bind("@p", 2);
        var r2 = ps2.Execute();
        Assert.Single(r2.Rows);
        Assert.Equal("Bob", r2.Rows[0].GetValue(0));
    }

    [Fact]
    public void Prepare_ConcurrentAccess_NoExceptions()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");

        var exceptions = 0;
        var ready = new CountdownEvent(4);

        Parallel.Invoke(
            () => { ready.Signal(); ready.Wait(); try { engine.Prepare("SELECT * FROM T WHERE Id = @p"); } catch { Interlocked.Increment(ref exceptions); } },
            () => { ready.Signal(); ready.Wait(); try { engine.Prepare("SELECT * FROM T WHERE Id = @p"); } catch { Interlocked.Increment(ref exceptions); } },
            () => { ready.Signal(); ready.Wait(); try { engine.Prepare("SELECT * FROM T WHERE Id = @p"); } catch { Interlocked.Increment(ref exceptions); } },
            () => { ready.Signal(); ready.Wait(); try { engine.Prepare("SELECT * FROM T WHERE Id = @p"); } catch { Interlocked.Increment(ref exceptions); } }
        );

        Assert.True(engine.PlanCacheHits + engine.PlanCacheMisses >= 4);
        Assert.Equal(0, exceptions);
    }

    [Fact]
    public void Prepare_CacheCapacity_EvictsOldest()
    {
        using var engine = WalhallaEngine.InMemory();

        // Create many tables so each prepare is a different SQL.
        for (int i = 0; i < 100; i++)
        {
            engine.Execute($"CREATE TABLE T{i} (Id INT PRIMARY KEY, Name STRING)");
            engine.Prepare($"SELECT * FROM T{i} WHERE Id = @p");
        }

        // All should have compiled without error (cache evicts oldest entries).
        Assert.True(engine.PlanCacheMisses >= 100);
    }

    [Fact]
    public void Prepare_DropTable_ClearsCache()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Prepare("SELECT * FROM T WHERE Id = @p");

        engine.Execute("DROP TABLE T");
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        var ps = engine.Prepare("SELECT * FROM T WHERE Id = @p");
        Assert.NotNull(ps);

        // Verify the new plan works.
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Test')");
        ps.Bind("@p", 1);
        var result = ps.Execute();
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Prepare_CrossDdl_PlansRecompiledAndWork()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE A (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE B (Id INT PRIMARY KEY, Name STRING)");

        engine.Prepare("SELECT * FROM A WHERE Id = @p");
        engine.Prepare("SELECT * FROM B WHERE Id = @p");

        // CREATE INDEX on A triggers global cache clear — both tables recompiled.
        engine.Execute("CREATE INDEX IX_A_Name ON A (Name)");

        engine.Execute("INSERT INTO A (Id, Name) VALUES (1, 'TestA')");
        engine.Execute("INSERT INTO B (Id, Name) VALUES (1, 'TestB')");

        var psA = engine.Prepare("SELECT * FROM A WHERE Id = @p");
        psA.Bind("@p", 1);
        Assert.Equal("TestA", psA.Execute().Rows[0].GetValue(1));

        var psB = engine.Prepare("SELECT * FROM B WHERE Id = @p");
        psB.Bind("@p", 1);
        Assert.Equal("TestB", psB.Execute().Rows[0].GetValue(1));
    }

    [Fact]
    public void PlanCacheHits_Misses_TelemetryAccurate()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        long hitsBefore = engine.PlanCacheHits;
        long missesBefore = engine.PlanCacheMisses;

        // First Prepare: miss.
        engine.Prepare("SELECT * FROM T");
        Assert.Equal(missesBefore + 1, engine.PlanCacheMisses);

        // Second Prepare with same SQL: hit.
        engine.Prepare("SELECT * FROM T");
        Assert.Equal(hitsBefore + 1, engine.PlanCacheHits);

        // Different SQL: another miss.
        engine.Prepare("SELECT Id FROM T WHERE Id = @p");
        Assert.Equal(missesBefore + 2, engine.PlanCacheMisses);
    }
}
