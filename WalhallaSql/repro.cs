using System;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;
using WalhallaSql.EfCore;

public class Parent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public virtual Single Single { get; set; } = null!;
}

public class Single
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public virtual Parent Parent { get; set; } = null!;
}

public class TestContext : WalhallaSqlEfCoreContext
{
    public TestContext(DbContextOptions<TestContext> options) : base(options) { }
    public DbSet<Parent> Parents => Set<Parent>();
    public DbSet<Single> Singles => Set<Single>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Parent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Single)
                .WithOne(e => e.Parent)
                .HasForeignKey<Single>(e => e.ParentId);
        });
        modelBuilder.Entity<Single>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
    }
}

class Program
{
    static void Main()
    {
        var engine = new WalhallaEngine();
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;

        using var context = new TestContext(options);
        context.Database.EnsureCreated();

        var parent = new Parent { Name = "P1" };
        var single = new Single { Parent = parent };
        context.Parents.Add(parent);
        context.Singles.Add(single);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        var loadedParent = context.Parents.First();
        Console.WriteLine($"Parent loaded: {loadedParent.Name}");

        var loadedSingle = loadedParent.Single;
        Console.WriteLine($"Single is null: {loadedSingle == null}");
        if (loadedSingle != null)
        {
            Console.WriteLine($"Single.ParentId: {loadedSingle.ParentId}");
        }

        // Now try explicit Load
        context.Entry(loadedParent).Reference(p => p.Single).Load();
        Console.WriteLine($"After Load, Single is null: {loadedParent.Single == null}");
        if (loadedParent.Single != null)
        {
            Console.WriteLine($"After Load, Single.ParentId: {loadedParent.Single.ParentId}");
        }

        // Try query directly
        var directSingle = context.Singles.FirstOrDefault(s => s.ParentId == loadedParent.Id);
        Console.WriteLine($"Direct query Single is null: {directSingle == null}");
    }
}
