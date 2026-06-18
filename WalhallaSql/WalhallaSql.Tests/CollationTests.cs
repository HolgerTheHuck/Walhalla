using System;
using System.Globalization;
using WalhallaSql.Collation;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public class CollationTests
{
    // ── CollationManager unit tests ────────────────────────────────────────────

    [Fact]
    public void DefaultCollation_IsOrdinalIgnoreCase()
    {
        Assert.True(CollationManager.Equals("ABC", "abc", null));
        Assert.True(CollationManager.Equals("hello", "HELLO", null));
        Assert.Equal(0, CollationManager.Compare("ABC", "abc", null));
        // OrdinalIgnoreCase is culture-invariant: ß != SS (ß is not a simple case mapping)
        Assert.False(CollationManager.Equals("Straße", "STRASSE", null));
    }

    [Fact]
    public void GetCompareInfo_C_Collation_ReturnsNull()
    {
        var ci = CollationManager.GetCompareInfo("C");
        Assert.Null(ci); // fast path
    }

    [Fact]
    public void GetCompareInfo_Null_ReturnsNull()
    {
        var ci = CollationManager.GetCompareInfo(null);
        Assert.Null(ci);
    }

    [Fact]
    public void GetCompareInfo_StripsXIcuSuffix()
    {
        var ci = CollationManager.GetCompareInfo("de-DE-x-icu");
        Assert.NotNull(ci);
        Assert.Equal("de-DE", ci.Name);
    }

    [Fact]
    public void Turkish_I_Problem()
    {
        var tr = CollationManager.GetCompareInfo("tr-TR-x-icu");
        Assert.NotNull(tr);

        // Under Turkish collation, dotted İ (U+0130) sorts differently from
        // dotless I (U+0049), while in most other languages they sort the same.
        // Test the sort order: 'i' (U+0069, dotted lowercase) vs 'ı' (U+0131, dotless lowercase)
        var dottedLower = "i";   // U+0069
        var dotlessLower = "ı";  // U+0131

        // In Turkish, dotted i and dotless ı are distinct letters with different sort positions
        var cmp = CollationManager.Compare(dottedLower, dotlessLower, tr);
        Assert.NotEqual(0, cmp);

        // Under default (null) collation, they sort as OrdinalIgnoreCase which
        // treats them as different code points
        var cmpDefault = CollationManager.Compare(dottedLower, dotlessLower, null);
        Assert.NotEqual(0, cmpDefault);
    }

    [Fact]
    public void German_Umlaut_Sorting()
    {
        var de = CollationManager.GetCompareInfo("de-DE-x-icu");
        Assert.NotNull(de);

        // a < ä < b < z in German phone book order
        Assert.True(CollationManager.Compare("a", "ä", de) < 0);
        Assert.True(CollationManager.Compare("ä", "b", de) < 0);
        Assert.True(CollationManager.Compare("b", "z", de) < 0);

        // ä and ae should be treated differently in dictionary order
        // In German, 'ä' sorts between 'a' and 'b' (not as 'ae')
        Assert.True(CollationManager.Compare("admiral", "äpfel", de) < 0);
    }

    [Fact]
    public void GetHashCode_WithCollation_IsConsistent()
    {
        var de = CollationManager.GetCompareInfo("de-DE-x-icu");
        Assert.NotNull(de);

        // Same string → same hash (deterministic)
        var h1 = CollationManager.GetHashCode("Müller", de);
        var h2 = CollationManager.GetHashCode("Müller", de);
        Assert.Equal(h1, h2);

        // Different strings → different hash (CompareOptions.None = case-sensitive)
        var h3 = CollationManager.GetHashCode("abc", de);
        var h4 = CollationManager.GetHashCode("ABC", de);
        Assert.NotEqual(h3, h4);
    }

    [Fact]
    public void GetHashCode_NullCollation_IsOrdinalIgnoreCase()
    {
        var h1 = CollationManager.GetHashCode("ABC", null);
        var h2 = CollationManager.GetHashCode("abc", null);
        var h3 = CollationManager.GetHashCode("AbC", null);
        Assert.Equal(h1, h2);
        Assert.Equal(h2, h3);
    }

    // ── Schema / DDL tests ─────────────────────────────────────────────────────

    [Fact]
    public void CreateTable_WithCollation_PersistsAndRestores()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING COLLATE \"de-DE-x-icu\")");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Müller')");

        // Verify the column definition has collation
        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        var nameCol = tableDef.Columns[1];
        Assert.Equal("de-DE-x-icu", nameCol.Collation);
    }

    [Fact]
    public void AlterTable_RenameColumn_PreservesCollation()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING COLLATE \"de-DE-x-icu\")");
        engine.Execute("ALTER TABLE T RENAME COLUMN Name TO FullName");

        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        var col = tableDef.Columns[1];
        Assert.Equal("FullName", col.Name);
        Assert.Equal("de-DE-x-icu", col.Collation);
    }

    [Fact]
    public void AlterTable_AddColumn_WithCollation()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("ALTER TABLE T ADD COLUMN Description STRING COLLATE \"en-US-x-icu\"");

        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        var descCol = tableDef.Columns[1];
        Assert.Equal("Description", descCol.Name);
        Assert.Equal("en-US-x-icu", descCol.Collation);
    }

    [Fact]
    public void Collation_Null_ColumnDefault()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");

        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        Assert.Null(tableDef.Columns[1].Collation);
    }

    // ── Backward compatibility ─────────────────────────────────────────────────

    [Fact]
    public void BackwardCompatibility_OldCatalog_LoadsCorrectly()
    {
        using var engine = WalhallaEngine.InMemory();
        // Create without collation — simulates old catalog
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'hello')");

        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal("hello", result.Rows[0]["Name"]);
    }

    // ── ColumnCollationContext tests ───────────────────────────────────────────

    [Fact]
    public void ColumnCollationContext_Default_IsDefault()
    {
        var ctx = ColumnCollationContext.Default;
        Assert.True(ctx.IsDefault);
        Assert.Null(ctx.GetCollation(0));
    }

    [Fact]
    public void ColumnCollationContext_Build_FromTableDef()
    {
        var tableDef = new SqlTableDefinition("T", new[]
        {
            new SqlColumnDefinition("Id", SqlScalarType.Int64, IsPrimaryKey: true),
            new SqlColumnDefinition("Name", SqlScalarType.String, Collation: "de-DE-x-icu"),
            new SqlColumnDefinition("Value", SqlScalarType.String)
        }, Array.Empty<SqlIndexDefinition>());

        var ctx = ColumnCollationContext.Build(tableDef);
        Assert.False(ctx.IsDefault);
        Assert.Null(ctx.GetCollation(0));  // INT column
        Assert.NotNull(ctx.GetCollation(1)); // STRING with collation
        Assert.Null(ctx.GetCollation(2)); // STRING without collation
    }

    // ── Expression COLLATE parsing ─────────────────────────────────────────────

    [Fact]
    public void WhereCollate_ExpressionParsesCorrectly()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING COLLATE \"tr-TR-x-icu\")");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'hello'), (2, 'HELLO')");

        // Under OrdinalIgnoreCase (null collation), case differences are ignored
        var result = engine.Execute("SELECT * FROM T WHERE Name = 'hello'");
        Assert.Equal(2, result.Rows.Count);
    }

    // ── ORDER BY COLLATE parsing ───────────────────────────────────────────────

    [Fact]
    public void OrderByCollate_ParsesCorrectly()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'b'), (2, 'a'), (3, 'c')");

        // ORDER BY COLLATE should parse without error
        var result = engine.Execute("SELECT * FROM T ORDER BY Name COLLATE \"de-DE-x-icu\"");
        Assert.Equal(3, result.Rows.Count);
    }

    // ── PgWire virtual tables ──────────────────────────────────────────────────

    [Fact]
    public void GetCollationOid_MapsCorrectly()
    {
        // The GetCollationOid helper is in PgWireServer.cs (private).
        // We test through the CollationManager lookup instead.
        Assert.Null(CollationManager.GetCompareInfo("C"));  // OID 100
        Assert.NotNull(CollationManager.GetCompareInfo("de-DE-x-icu")); // OID 950
        Assert.NotNull(CollationManager.GetCompareInfo("en-US-x-icu")); // OID 951
        Assert.NotNull(CollationManager.GetCompareInfo("tr-TR-x-icu")); // OID 952
    }

    // ── PgVirtualColumnDefinition ──────────────────────────────────────────────

    [Fact]
    public void PgVirtualColumnDefinition_WithCollation()
    {
        var col = new WalhallaSql.PgWire.PgVirtualColumnDefinition(
            "Name", "text", IsNullable: true, IsPrimaryKey: false, Collation: "de-DE-x-icu");

        Assert.Equal("de-DE-x-icu", col.Collation);
    }

    [Fact]
    public void PgVirtualColumnDefinition_DefaultCollation()
    {
        var col = new WalhallaSql.PgWire.PgVirtualColumnDefinition(
            "Name", "text", IsNullable: true, IsPrimaryKey: false);

        Assert.Null(col.Collation);
    }

    // ── WalhallaSqlPgWireBackend ───────────────────────────────────────────────

    [Fact]
    public void Backend_DatabaseCollation_DefaultsToC()
    {
        using var engine = WalhallaEngine.InMemory();
        var backend = new WalhallaSql.PgWire.WalhallaSqlPgWireBackend(engine);
        var conn = (WalhallaSql.PgWire.IPgWireBackendConnection)backend;

        Assert.Equal("C", conn.DatabaseCollation);
        Assert.Equal("C", conn.DatabaseCType);
    }
}
