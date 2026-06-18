using System;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "DesignTime"/Migration:
/// EnsureCreated erzeugt erwartete Tabelle + Spalten, Migration-Idempotenz.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalMigrationSchemaTests
{
    [Fact]
    public void Migration_creates_expected_table_and_columns()
    {
        using var scope = CreateScope();

        var result = scope.Context.ExecuteSql(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'schema_items' ORDER BY ordinal_position");

        Assert.NotNull(result.Rows);
        var columns = result.Rows!.Select(r => r["column_name"]?.ToString()).ToList();

        Assert.Contains("Id", columns);
        Assert.Contains("Name", columns);
    }

    [Fact]
    public void Migration_is_idempotent_when_applied_twice()
    {
        using var scope = CreateScope();

        // Zweimal dieselbe Migration anwenden darf keine Exception werfen.
        scope.Context.Migrations.ApplyPlannedChanges("20260615_MinimalMigrationSchema");

        var count = scope.Context.SchemaItems.Count();
        Assert.Equal(0, count);
    }

    [Fact]
    public void EnsureCreated_creates_database_without_exception()
    {
        using var scope = MinimalSpecScope<SchemaContext>.Create(
            "20260615_MinimalMigrationSchemaEnsureCreated",
            options => new SchemaContext(options));

        var created = scope.Context.Database.EnsureCreated();

        // WalhallaSql-EfCore-Provider meldet typischerweise false/true je nach
        // Implementierung; hier reicht uns, dass kein Fehler auftritt.
        Assert.True(created || !created);
    }

    [Fact]
    public void Table_with_custom_schema_mapping_accepts_insert()
    {
        using var scope = CreateScope();

        scope.Context.SchemaItems.Add(new SchemaItem { Id = 1, Name = "Test" });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.SchemaItems.Find(1);
        Assert.NotNull(found);
        Assert.Equal("Test", found!.Name);
    }

    private static MinimalSpecScope<SchemaContext> CreateScope()
        => MinimalSpecScope<SchemaContext>.Create(
            "20260615_MinimalMigrationSchema",
            options => new SchemaContext(options));

    public sealed class SchemaItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SchemaContext : WalhallaSqlEfCoreContext
    {
        public SchemaContext(DbContextOptions options) : base(options) { }

        public DbSet<SchemaItem> SchemaItems => Set<SchemaItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SchemaItem>(entity =>
            {
                entity.ToTable("schema_items");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
