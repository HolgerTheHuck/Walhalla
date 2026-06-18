using System.Threading.Tasks;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "Find":
/// Find über generisches Set, nicht-generischen Context und Async.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalFindTests
{
    [Fact]
    public void Find_by_key_via_Set_returns_entity()
    {
        using var scope = CreateSeededScope();

        var found = scope.Context.Items.Find(1);

        Assert.NotNull(found);
        Assert.Equal("Ada Lovelace", found.Name);
    }

    [Fact]
    public void Find_by_key_via_DbContext_returns_entity()
    {
        using var scope = CreateSeededScope();

        var found = scope.Context.Find(typeof(FindableEntity), 2);

        Assert.NotNull(found);
        Assert.IsType<FindableEntity>(found);
        Assert.Equal("Alan Turing", ((FindableEntity)found).Name);
    }

    [Fact]
    public void Find_missing_key_returns_null()
    {
        using var scope = CreateSeededScope();

        var found = scope.Context.Items.Find(999);

        Assert.Null(found);
    }

    [Fact]
    public void Find_by_key_after_SaveChanges_roundtrips()
    {
        using var scope = MinimalSpecScope<FindContext>.Create(
            "20260615_MinimalFind",
            options => new FindContext(options));

        scope.Context.Items.Add(new FindableEntity { Id = 10, Name = "Grace Hopper" });
        scope.Context.SaveChanges();

        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Items.Find(10);

        Assert.NotNull(found);
        Assert.Equal("Grace Hopper", found.Name);
    }

    [Fact]
    public async Task FindAsync_by_key_returns_entity()
    {
        using var scope = CreateSeededScope();

        var found = await scope.Context.Items.FindAsync(1);

        Assert.NotNull(found);
        Assert.Equal("Ada Lovelace", found.Name);
    }

    private static MinimalSpecScope<FindContext> CreateSeededScope()
        => MinimalSpecScope<FindContext>.Create(
            "20260615_MinimalFind",
            options => new FindContext(options),
            seed: context =>
            {
                context.Items.AddRange(
                    new FindableEntity { Id = 1, Name = "Ada Lovelace" },
                    new FindableEntity { Id = 2, Name = "Alan Turing" });
                context.SaveChanges();
                context.ChangeTracker.Clear();
            });

    public sealed class FindableEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FindContext : WalhallaSqlEfCoreContext
    {
        public FindContext(DbContextOptions options) : base(options) { }

        public DbSet<FindableEntity> Items => Set<FindableEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FindableEntity>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
