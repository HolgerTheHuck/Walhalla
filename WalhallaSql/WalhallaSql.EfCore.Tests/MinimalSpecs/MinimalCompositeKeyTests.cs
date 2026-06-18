using System.Collections.Generic;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für Composite Keys:
/// Find, Update und Include mit mehrspaltigen Primärschlüsseln.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalCompositeKeyTests
{
    [Fact]
    public void Composite_key_entity_can_be_found_and_updated()
    {
        using var scope = CreateSeededScope();

        var child = scope.Context.Children.Find(1, 200);
        Assert.NotNull(child);
        Assert.Equal("Original", child!.Name);

        child.Name = "Updated";
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var reloaded = scope.Context.Children.Find(1, 200);
        Assert.NotNull(reloaded);
        Assert.Equal("Updated", reloaded.Name);
    }

    [Fact]
    public void Composite_key_include_loads_navigation()
    {
        using var scope = CreateSeededScope();

        var child = scope.Context.Children
            .Include(c => c.Parent)
            .Single(c => c.TenantId == 1 && c.Id == 200);

        Assert.NotNull(child.Parent);
        Assert.Equal(100, child.Parent!.Id);
        Assert.Equal("Parent", child.Parent.Name);
    }

    private static MinimalSpecScope<CompositeKeyContext> CreateSeededScope()
        => MinimalSpecScope<CompositeKeyContext>.Create(
            "20260616_MinimalCompositeKey",
            options => new CompositeKeyContext(options),
            seed: ctx =>
            {
                ctx.Parents.Add(new CompositeParent { TenantId = 1, Id = 100, Name = "Parent" });
                ctx.Children.Add(new CompositeChild { TenantId = 1, Id = 200, ParentId = 100, Name = "Original" });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class CompositeParent
    {
        public int TenantId { get; set; }
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<CompositeChild> Children { get; set; } = new();
    }

    public sealed class CompositeChild
    {
        public int TenantId { get; set; }
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public CompositeParent? Parent { get; set; }
    }

    public sealed class CompositeKeyContext : WalhallaSqlEfCoreContext
    {
        public CompositeKeyContext(DbContextOptions options) : base(options) { }

        public DbSet<CompositeParent> Parents => Set<CompositeParent>();
        public DbSet<CompositeChild> Children => Set<CompositeChild>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeParent>(entity =>
            {
                entity.ToTable("CompositeParents");
                entity.HasKey(x => new { x.TenantId, x.Id });
                entity.Property(x => x.Name).IsRequired();
            });

            modelBuilder.Entity<CompositeChild>(entity =>
            {
                entity.ToTable("CompositeChildren");
                entity.HasKey(x => new { x.TenantId, x.Id });
                entity.Property(x => x.Name).IsRequired();
                entity.HasOne(x => x.Parent)
                    .WithMany(x => x.Children)
                    .HasForeignKey(x => new { x.TenantId, x.ParentId })
                    .HasPrincipalKey(x => new { x.TenantId, x.Id });
            });
        }
    }
}
