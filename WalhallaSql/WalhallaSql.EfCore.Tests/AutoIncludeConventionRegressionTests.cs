using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class AutoIncludeConventionRegressionTests
{
    [Fact]
    public void Self_referencing_unique_navigation_is_not_auto_included()
    {
        using var scope = ModelScope.Create();
        using var context = scope.CreateContext<SelfReferenceContext>();

        var entityType = context.Model.FindEntityType(typeof(SelfReferenceEntity))
            ?? throw new InvalidOperationException("Expected self-reference entity type.");
        var navigation = entityType.FindNavigation(nameof(SelfReferenceEntity.Self))
            ?? throw new InvalidOperationException("Expected self-reference navigation.");

        Assert.False(navigation.IsEagerLoaded);
    }

    [Fact]
    public void Shared_table_dependent_navigation_remains_auto_included()
    {
        using var scope = ModelScope.Create();
        using var context = scope.CreateContext<SharedTableReferenceContext>();

        var entityType = context.Model.FindEntityType(typeof(SharedTablePrincipal))
            ?? throw new InvalidOperationException("Expected shared-table principal entity type.");
        var navigation = entityType.FindNavigation(nameof(SharedTablePrincipal.Dependent))
            ?? throw new InvalidOperationException("Expected shared-table dependent navigation.");

        Assert.True(navigation.IsEagerLoaded);
    }

    private sealed class ModelScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private ModelScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static ModelScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "AutoIncludeConventionRegressionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            return new ModelScope(dbPath, engine, database);
        }

        public TContext CreateContext<TContext>() where TContext : DbContext
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(_database))
                .Options;

            return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        }

        public void Dispose()
        {
            _engine.Dispose();

            try
            {
                if (Directory.Exists(_dbPath))
                    Directory.Delete(_dbPath, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class SelfReferenceContext(DbContextOptions<SelfReferenceContext> options)
        : WalhallaSqlEfCoreContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SelfReferenceEntity>(entity =>
            {
                entity.ToTable("SelfRefs");
                entity.HasKey(item => item.Id);
                entity.HasOne(item => item.Self)
                    .WithOne()
                    .HasForeignKey<SelfReferenceEntity>(item => item.SelfId)
                    .IsRequired(false);
            });
        }
    }

    private sealed class SharedTableReferenceContext(DbContextOptions<SharedTableReferenceContext> options)
        : WalhallaSqlEfCoreContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SharedTablePrincipal>(entity =>
            {
                entity.ToTable("SharedItems");
                entity.HasKey(item => item.Id);
                entity.HasOne(item => item.Dependent)
                    .WithOne(item => item.Principal)
                    .HasForeignKey<SharedTableDependent>(item => item.Id)
                    .IsRequired(false);
            });

            modelBuilder.Entity<SharedTableDependent>(entity =>
            {
                entity.ToTable("SharedItems");
                entity.HasKey(item => item.Id);
            });
        }
    }

    private sealed class SelfReferenceEntity
    {
        public int Id { get; set; }
        public int? SelfId { get; set; }
        public SelfReferenceEntity? Self { get; set; }
    }

    private sealed class SharedTablePrincipal
    {
        public int Id { get; set; }
        public SharedTableDependent? Dependent { get; set; }
    }

    private sealed class SharedTableDependent
    {
        public int Id { get; set; }
        public SharedTablePrincipal? Principal { get; set; }
    }
}
