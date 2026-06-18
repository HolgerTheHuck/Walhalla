using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// Reproducer + regression coverage for the PK != RowId bug.
///
/// Walhalla previously assigned an auto-incrementing storage RowId on every
/// insert and ignored the user-supplied PK column value. The query planner,
/// however, treated <c>WHERE pk = literal</c> / <c>WHERE pk BETWEEN a AND b</c>
/// as a RowId lookup/range. This worked by accident when user PK values
/// matched the auto-sequence (1, 2, 3, ...) but produced silent zero-affected
/// results as soon as the values diverged.
///
/// Fix (Option B1): for tables with a single-column INT64/BIGINT primary key,
/// the user-supplied PK value IS the storage RowId (SQLite-style alias).
/// </summary>
public class BigIntPkRowIdAliasTests
{
    [Fact]
    public void DeleteByPkLiteral_FindsRow_WhenUserIdDivergesFromAutoSequence()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id BIGINT PRIMARY KEY, Val INT)");

        // User-supplied Ids deliberately do NOT match the auto-RowId sequence
        // (which would start at 1). Before the fix, the DELETE below targets
        // storage RowId 200 — which is empty — so Affected = 0.
        engine.Execute("INSERT INTO T (Id, Val) VALUES (100, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (200, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (300, 30)");

        var deleteResult = engine.Execute("DELETE FROM T WHERE Id = 200");
        Assert.Equal(1, deleteResult.AffectedRows);

        var select = engine.Execute("SELECT Id FROM T");
        Assert.Equal(2, select.Rows.Count);
        var ids = new long[select.Rows.Count];
        for (int i = 0; i < select.Rows.Count; i++)
            ids[i] = System.Convert.ToInt64(select.Rows[i]["Id"]);
        System.Array.Sort(ids);
        Assert.Equal(new long[] { 100, 300 }, ids);
    }

    [Fact]
    public void DeleteByPkRange_FindsRows_WhenUserIdDivergesFromAutoSequence()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id BIGINT PRIMARY KEY, Val INT)");

        engine.Execute("INSERT INTO T (Id, Val) VALUES (100, 10)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (200, 20)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (300, 30)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (400, 40)");

        var deleteResult = engine.Execute("DELETE FROM T WHERE Id BETWEEN 150 AND 350");
        Assert.Equal(2, deleteResult.AffectedRows);

        var select = engine.Execute("SELECT Id FROM T ORDER BY Id");
        Assert.Equal(2, select.Rows.Count);
        Assert.Equal(100L, System.Convert.ToInt64(select.Rows[0]["Id"]));
        Assert.Equal(400L, System.Convert.ToInt64(select.Rows[1]["Id"]));
    }

    [Fact]
    public void SelectByPkLiteral_FindsRow_WhenUserIdDivergesFromAutoSequence()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id BIGINT PRIMARY KEY, Val INT)");

        engine.Execute("INSERT INTO T (Id, Val) VALUES (1000, 11)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (2000, 22)");

        var result = engine.Execute("SELECT Val FROM T WHERE Id = 2000");
        Assert.Single(result.Rows);
        Assert.Equal(22, System.Convert.ToInt32(result.Rows[0]["Val"]));
    }

    [Fact]
    public void DuplicatePk_ThrowsWalhallaException()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id BIGINT PRIMARY KEY, Val INT)");
        engine.Execute("INSERT INTO T (Id, Val) VALUES (42, 1)");

        Assert.Throws<WalhallaConstraintException>(() =>
            engine.Execute("INSERT INTO T (Id, Val) VALUES (42, 2)"));
    }
}
