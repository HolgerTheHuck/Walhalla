using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WalhallaSql.EfCore.Tests;

public sealed class SaveChangesInterceptionReproTests
{
    [Fact]
    public void Passive_interceptor_should_not_cause_duplicate_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "InterceptionRepro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);
        var engine = WalhallaEngine.Open(dbPath);

        try
        {
            var interceptor = new PassiveInterceptor();
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
                .AddInterceptors(interceptor)
                .Options;

            using var context = new TestContext(options);
            context.Database.EnsureCreated();

            context.Add(new Singularity { Id = 35, Type = "Red Dwarf" });
            var result = context.SaveChanges();

            var count = context.Set<Singularity>().AsNoTracking().Count(e => e.Id == 35);
            Assert.Equal(1, result);  // Should return 1
            Assert.Equal(1, count);   // Should have 1 row
        }
        finally
        {
            engine.Dispose();
            try { Directory.Delete(dbPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Suppressing_interceptor_should_prevent_save()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "InterceptionRepro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);
        var engine = WalhallaEngine.Open(dbPath);

        try
        {
            var interceptor = new SuppressingInterceptor();
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
                .AddInterceptors(interceptor)
                .Options;

            using var context = new TestContext(options);
            context.Database.EnsureCreated();

            context.Add(new Singularity { Id = 35, Type = "Red Dwarf" });
            var result = context.SaveChanges();

            var count = context.Set<Singularity>().AsNoTracking().Count(e => e.Id == 35);
            Assert.Equal(-1, result);  // Should return -1 (suppressed)
            Assert.Equal(0, count);    // Should have 0 rows
        }
        finally
        {
            engine.Dispose();
            try { Directory.Delete(dbPath, recursive: true); } catch { }
        }
    }

    private class TestContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Singularity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Type).IsRequired();
                entity.HasData(
                    new Singularity { Id = 77, Type = "Black Hole" },
                    new Singularity { Id = 88, Type = "Big Bang" });
            });
        }
    }

    private class Singularity
    {
        public int Id { get; set; }
        public string Type { get; set; } = string.Empty;
    }

    private class PassiveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            return result;
        }
    }

    private class SuppressingInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            return InterceptionResult<int>.SuppressWithResult(-1);
        }
    }
}
