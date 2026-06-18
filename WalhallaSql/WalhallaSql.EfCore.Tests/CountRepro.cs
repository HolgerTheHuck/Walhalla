using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;

namespace Microsoft.EntityFrameworkCore;

public class CountRepro
{
    private WalhallaEngine CreateEngine()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WalhallaCountRepro");
        if (System.IO.Directory.Exists(path))
            System.IO.Directory.Delete(path, recursive: true);
        System.IO.Directory.CreateDirectory(path);
        return WalhallaEngine.Open(path);
    }

    private TestContext CreateContext(WalhallaEngine engine)
    {
        var conn = new WalhallaSqlDbConnection($"DataSource=countrepro;Database=App");
        WalhallaSqlConnectionRegistry.Register("countrepro", () => engine);

        var options = new DbContextOptionsBuilder<TestContext>()
            .UseWalhallaSql(conn)
            .Options;

        return new TestContext(options);
    }

    private class TestContext : DbContext
    {
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        public DbSet<ReproEntity> Entities => Set<ReproEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ReproEntity>().Property(e => e.Id).ValueGeneratedNever();
        }
    }

    private class ReproEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void Count_scalar_should_return_one_row()
    {
        using var engine = CreateEngine();
        using var context = CreateContext(engine);
        context.Database.EnsureCreated();

        context.Add(new ReproEntity { Id = 35, Name = "A" });
        context.SaveChanges();
        var count = context.Set<ReproEntity>().AsNoTracking().Count(e => e.Id == 35);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_scalar_inside_transaction_should_return_one_row()
    {
        using var engine = CreateEngine();
        using var context = CreateContext(engine);
        context.Database.EnsureCreated();

        context.Add(new ReproEntity { Id = 35, Name = "A" });
        context.SaveChanges();

        using var tx = context.Database.BeginTransaction();
        var count = context.Set<ReproEntity>().AsNoTracking().Count(e => e.Id == 35);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_scalar_with_multiple_rows_should_return_correct_count()
    {
        using var engine = CreateEngine();
        using var context = CreateContext(engine);
        context.Database.EnsureCreated();

        for (int i = 0; i < 5; i++)
            context.Add(new ReproEntity { Id = i, Name = $"A{i}" });
        context.SaveChanges();

        var count = context.Set<ReproEntity>().AsNoTracking().Count(e => e.Id < 3);
        // Print actual for debugging
        // System.Diagnostics.Debug.WriteLine($"Count < 3 = {count}");
        Assert.Equal(3, count);
    }

    [Fact]
    public void Count_equality_with_multiple_rows_should_return_correct_count()
    {
        using var engine = CreateEngine();
        using var context = CreateContext(engine);
        context.Database.EnsureCreated();

        for (int i = 0; i < 5; i++)
            context.Add(new ReproEntity { Id = i, Name = "A" });
        context.SaveChanges();

        var count = context.Set<ReproEntity>().AsNoTracking().Count(e => e.Name == "A");
        Assert.Equal(5, count);
    }
}
