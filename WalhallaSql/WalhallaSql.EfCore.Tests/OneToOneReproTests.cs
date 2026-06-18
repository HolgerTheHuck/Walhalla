using Microsoft.EntityFrameworkCore;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Xunit;

namespace Microsoft.EntityFrameworkCore;

public sealed class OneToOneReproTests
{
    public class Parent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public virtual SingleEntity Single { get; set; } = null!;
    }

    public class SingleEntity
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public virtual Parent Parent { get; set; } = null!;
    }

    public class TestContext : WalhallaSqlEfCoreContext
    {
        public TestContext(DbContextOptions<TestContext> options) : base(options) { }
        public DbSet<Parent> Parents => Set<Parent>();
        public DbSet<SingleEntity> Singles => Set<SingleEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Parent>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasOne(e => e.Single)
                    .WithOne(e => e.Parent)
                    .HasForeignKey<SingleEntity>(e => e.ParentId);
            });
            modelBuilder.Entity<SingleEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }

    // Second repro: shared PK like EF Core LoadTestBase model
    public class ParentShared
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public virtual SingleShared Single { get; set; } = null!;
    }

    public class SingleShared
    {
        public int Id { get; set; }
        public virtual ParentShared Parent { get; set; } = null!;
    }

    public class TestContextShared : WalhallaSqlEfCoreContext
    {
        public TestContextShared(DbContextOptions<TestContextShared> options) : base(options) { }
        public DbSet<ParentShared> Parents => Set<ParentShared>();
        public DbSet<SingleShared> Singles => Set<SingleShared>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ParentShared>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasOne(e => e.Single)
                    .WithOne(e => e.Parent)
                    .HasForeignKey<SingleShared>(e => e.Id);
            });
            modelBuilder.Entity<SingleShared>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }

    [Fact]
    public void One_to_one_save_and_load()
    {
        var engine = new WalhallaEngine(new WalhallaOptions(Path.GetTempFileName()) { StorageMode = StorageMode.InMemory });
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;

        using var context = new TestContext(options);
        context.Database.EnsureCreated();

        var parent = new Parent { Name = "P1" };
        var single = new SingleEntity { Parent = parent };
        context.Parents.Add(parent);
        context.Singles.Add(single);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        var loadedParent = context.Parents.First();
        Assert.NotNull(loadedParent);

        // This should lazy-load or require explicit load
        // For non-lazy, this will be null until loaded
        Assert.Equal("P1", loadedParent.Name);

        // Explicit load
        context.Entry(loadedParent).Reference(p => p.Single).Load();
        Assert.NotNull(loadedParent.Single);
        Assert.Equal(loadedParent.Id, loadedParent.Single.ParentId);

        // Direct query
        var directSingle = context.Singles.FirstOrDefault(s => s.ParentId == loadedParent.Id);
        Assert.NotNull(directSingle);

        // Test Include
        context.ChangeTracker.Clear();
        var includedParent = context.Parents.Include(p => p.Single).First();
        Assert.NotNull(includedParent.Single);
        Assert.Equal(includedParent.Id, includedParent.Single.ParentId);
    }

    [Fact]
    public void One_to_one_nullable_fk_spec_style()
    {
        var engine = new WalhallaEngine(new WalhallaOptions(Path.GetTempFileName()) { StorageMode = StorageMode.InMemory });
        var options = new DbContextOptionsBuilder<SpecContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;

        using var context = new SpecContext(options);
        context.Database.EnsureCreated();

        // Seed exactly like LoadFixtureBase
        var parent = new SpecParent
        {
            Id = 707,
            Single = new SpecSingle { Id = 21 }
        };
        context.Add(parent);
        var singleEntry = context.Entry(parent.Single);
        Assert.Equal(707, singleEntry.Property("ParentId").CurrentValue);
        Assert.Equal(EntityState.Added, singleEntry.State);
        var savedCount = context.SaveChanges();
        Assert.Equal(2, savedCount); // Parent + Single

        context.ChangeTracker.Clear();

        // Verify raw data via ADO.NET
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ParentId FROM Singles";
        using var reader = cmd.ExecuteReader();
        bool hasRow = reader.Read();
        Assert.True(hasRow, "No row found in Singles table via ADO.NET");
        if (hasRow)
        {
            var id = reader.GetInt32(0);
            var parentId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            Assert.Equal(21, id);
            Assert.Equal(707, parentId);
        }

        // Verify raw data via EF Core
        var raw = context.Set<SpecSingle>().FirstOrDefault();
        Assert.NotNull(raw);
        Assert.Equal(21, raw.Id);
        // This is the key check: is ParentId correctly stored?
        Assert.Equal(707, raw.ParentId);

        // Verify JOIN directly
        using var joinCmd = conn.CreateCommand();
        joinCmd.CommandText = "SELECT p.Id, s.Id, s.ParentId FROM Parents AS p LEFT JOIN Singles AS s ON p.Id = s.ParentId";
        using var joinReader = joinCmd.ExecuteReader();
        Assert.True(joinReader.Read());
        var pId = joinReader.GetInt32(0);
        var sId = joinReader.IsDBNull(1) ? (int?)null : joinReader.GetInt32(1);
        var sParentId = joinReader.IsDBNull(2) ? (int?)null : joinReader.GetInt32(2);
        Assert.Equal(707, pId);
        Assert.Equal(21, sId);
        Assert.Equal(707, sParentId);

        // Now test Include
        var loadedParent = context.Set<SpecParent>().Include(p => p.Single).Single();
        Assert.NotNull(loadedParent);
        // THIS IS WHERE THE SPEC TEST FAILS:
        Assert.NotNull(loadedParent.Single);

        // Try the exact operation from the failing test
        var entry = context.Entry(loadedParent.Single);
        Assert.NotNull(entry);
    }

    [Fact]
    public void Debug_engine_join_directly()
    {
        var engine = new WalhallaEngine(new WalhallaOptions(Path.GetTempFileName()) { StorageMode = StorageMode.InMemory });
        var options = new DbContextOptionsBuilder<SpecContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;
        using var context = new SpecContext(options);
        context.Database.EnsureCreated();

        var parent = new SpecParent { Id = 707, Single = new SpecSingle { Id = 21 } };
        context.Add(parent);
        context.SaveChanges();

        var result = engine.Execute("SELECT p.Id, s.Id, s.ParentId FROM Parents AS p LEFT JOIN Singles AS s ON p.Id = s.ParentId");
        System.Console.WriteLine($"DEBUG columns: {string.Join(", ", result.ColumnNames)}");
        foreach (var r in result.Rows)
        {
            var vals = new List<string>();
            for (int i = 0; i < result.ColumnNames.Count; i++)
                vals.Add(result.ColumnNames[i] + "=" + (r.GetValue(i)?.ToString() ?? "NULL"));
            System.Console.WriteLine("DEBUG row: " + string.Join(", ", vals));
        }
    }

    public class SpecParent
    {
        [System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None)]
        public int Id { get; set; }
        public virtual SpecSingle Single { get; set; } = null!;
    }

    public class SpecSingle
    {
        [System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.None)]
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public virtual SpecParent Parent { get; set; } = null!;
    }

    public class SpecContext : WalhallaSqlEfCoreContext
    {
        public SpecContext(DbContextOptions<SpecContext> options) : base(options) { }
        public DbSet<SpecParent> Parents => Set<SpecParent>();
        public DbSet<SpecSingle> Singles => Set<SpecSingle>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpecParent>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasOne(e => e.Single)
                    .WithOne(e => e.Parent)
                    .HasForeignKey<SpecSingle>(e => e.ParentId);
            });
            modelBuilder.Entity<SpecSingle>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
}
