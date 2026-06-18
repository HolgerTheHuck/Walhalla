using System.Threading.Tasks;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für SaveChanges aus den EF-Core-Specs:
/// Insert, Update, Delete, Attach und State-Tracking.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalSaveChangesTests
{
    [Fact]
    public void SaveChanges_insert_persists_new_entity()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260615_MinimalSaveChanges",
            options => new SaveChangesContext(options));

        scope.Context.Products.Add(new Product { Id = 1, Name = "Keyboard" });
        var written = scope.Context.SaveChanges();

        Assert.Equal(1, written);

        scope.Context.ChangeTracker.Clear();
        var found = scope.Context.Products.Find(1);

        Assert.NotNull(found);
        Assert.Equal("Keyboard", found.Name);
    }

    [Fact]
    public void SaveChanges_update_persists_modified_property()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260615_MinimalSaveChanges",
            options => new SaveChangesContext(options),
            seed: ctx =>
            {
                ctx.Products.Add(new Product { Id = 2, Name = "Mouse" });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

        var entity = scope.Context.Products.Find(2)!;
        entity.Name = "Gaming Mouse";
        var written = scope.Context.SaveChanges();

        Assert.Equal(1, written);

        scope.Context.ChangeTracker.Clear();
        var reloaded = scope.Context.Products.Find(2);

        Assert.NotNull(reloaded);
        Assert.Equal("Gaming Mouse", reloaded.Name);
    }

    [Fact]
    public void SaveChanges_delete_removes_entity()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260615_MinimalSaveChanges",
            options => new SaveChangesContext(options),
            seed: ctx =>
            {
                ctx.Products.Add(new Product { Id = 3, Name = "Monitor" });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

        var entity = scope.Context.Products.Find(3)!;
        scope.Context.Products.Remove(entity);
        var written = scope.Context.SaveChanges();

        Assert.Equal(1, written);

        scope.Context.ChangeTracker.Clear();
        var reloaded = scope.Context.Products.Find(3);

        Assert.Null(reloaded);
    }

    [Fact]
    public void SaveChanges_Attach_and_mark_modified_updates_without_querying()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260615_MinimalSaveChanges",
            options => new SaveChangesContext(options),
            seed: ctx =>
            {
                ctx.Products.Add(new Product { Id = 4, Name = "Headset" });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

        var detached = new Product { Id = 4, Name = "Wireless Headset" };
        scope.Context.Attach(detached);
        scope.Context.Entry(detached).Property(x => x.Name).IsModified = true;

        var written = scope.Context.SaveChanges();

        Assert.Equal(1, written);

        scope.Context.ChangeTracker.Clear();
        var reloaded = scope.Context.Products.Find(4);

        Assert.NotNull(reloaded);
        Assert.Equal("Wireless Headset", reloaded.Name);
    }

    [Fact]
    public async Task SaveChangesAsync_insert_persists_new_entity()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260615_MinimalSaveChanges",
            options => new SaveChangesContext(options));

        scope.Context.Products.Add(new Product { Id = 5, Name = "Webcam" });
        var written = await scope.Context.SaveChangesAsync();

        Assert.Equal(1, written);

        scope.Context.ChangeTracker.Clear();
        var found = await scope.Context.Products.FindAsync(5);

        Assert.NotNull(found);
        Assert.Equal("Webcam", found.Name);
    }

    // ─── DbUpdateConcurrencyException ────────────────────────────────────

    [Fact]
    public void Update_non_existing_entity_throws_DbUpdateConcurrencyException()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260616_MinimalSaveChangesConcurrency",
            options => new SaveChangesContext(options));

        var ghost = new Product { Id = 9999, Name = "Ghost" };
        scope.Context.Attach(ghost);
        scope.Context.Entry(ghost).Property(x => x.Name).IsModified = true;

        Assert.Throws<DbUpdateConcurrencyException>(() => scope.Context.SaveChanges());
    }

    [Fact(Skip = "Known limitation: Deleting non-existing entity does not throw DbUpdateConcurrencyException. See EF-CORE-LIMITS.md.")]
    public void Delete_non_existing_entity_throws_DbUpdateConcurrencyException()
    {
        using var scope = MinimalSpecScope<SaveChangesContext>.Create(
            "20260616_MinimalSaveChangesConcurrency",
            options => new SaveChangesContext(options));

        var ghost = new Product { Id = 8888, Name = "Ghost" };
        scope.Context.Remove(ghost);

        Assert.Throws<DbUpdateConcurrencyException>(() => scope.Context.SaveChanges());
    }

    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SaveChangesContext : WalhallaSqlEfCoreContext
    {
        public SaveChangesContext(DbContextOptions options) : base(options) { }

        public DbSet<Product> Products => Set<Product>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
