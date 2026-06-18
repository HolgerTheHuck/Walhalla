using System;
using System.IO;
using System.Linq;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für Transaktionsverhalten:
/// Rollback bei Dispose, Commit persistiert, Tx-lokale Sichtbarkeit.
/// Verwendet plain DbContext (nicht WalhallaSqlEfCoreContext), da
/// WalhallaSqlEfCoreContext keine ambient EF Transactions unterstützt (LSQ-EF-SAVE-010).
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalTransactionTests
{
    [Fact]
    public void Transaction_dispose_without_commit_rolls_back_inserts()
    {
        using var scope = TxScope.Create();

        // Phase 1: Insert innerhalb einer Transaktion, dann Dispose ohne Commit
        using (var ctx1 = scope.CreateContext())
        {
            using var tx = ctx1.Database.BeginTransaction();
            ctx1.Items.Add(new TxItem { Id = 1, Name = "ShouldRollback" });
            ctx1.SaveChanges();
            // tx.Dispose() ohne Commit → Rollback
        }

        // Phase 2: Neuer Kontext — die Insert darf nicht sichtbar sein
        using (var ctx2 = scope.CreateContext())
        {
            var found = ctx2.Items.Find(1);
            Assert.Null(found);
        }
    }

    [Fact]
    public void Transaction_commit_persists_inserts()
    {
        using var scope = TxScope.Create();

        // Phase 1: Insert + Commit
        using (var ctx1 = scope.CreateContext())
        {
            using var tx = ctx1.Database.BeginTransaction();
            ctx1.Items.Add(new TxItem { Id = 1, Name = "ShouldPersist" });
            ctx1.SaveChanges();
            tx.Commit();
        }

        // Phase 2: Neuer Kontext — die Insert muss sichtbar sein
        using (var ctx2 = scope.CreateContext())
        {
            var found = ctx2.Items.Find(1);
            Assert.NotNull(found);
            Assert.Equal("ShouldPersist", found.Name);
        }
    }

    [Fact]
    public void Transaction_local_visibility_sees_uncommitted_inserts()
    {
        using var scope = TxScope.Create();

        using var ctx = scope.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        ctx.Items.Add(new TxItem { Id = 1, Name = "TxLocal" });
        ctx.SaveChanges();

        // Innerhalb derselben Transaktion muss die Insert sichtbar sein
        var visible = ctx.Items.AsNoTracking().Count(e => e.Id == 1);
        Assert.Equal(1, visible);

        // Nicht committen → Rollback
    }

    private sealed class TxScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;

        private TxScope(string dbPath, WalhallaEngine engine)
        {
            _dbPath = dbPath;
            _engine = engine;
        }

        public WalhallaEngine Engine => _engine;

        public static TxScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "MinimalSpecs", "TxTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);

            // Migration einmalig über einen Setup-Kontext anwenden
            var setupOptions = new DbContextOptionsBuilder<TxContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
                .Options;
            using (var setupCtx = new TxContext(setupOptions))
            {
                setupCtx.Database.EnsureCreated();
            }

            return new TxScope(dbPath, engine);
        }

        public TxContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<TxContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(_engine))
                .Options;
            return new TxContext(options);
        }

        public void Dispose()
        {
            _engine.Dispose();
            try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
        }
    }

    public sealed class TxItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Plain DbContext (nicht WalhallaSqlEfCoreContext), damit
    /// Database.BeginTransaction() ohne LSQ-EF-SAVE-010-Guardrail funktioniert.
    /// </summary>
    public sealed class TxContext : DbContext
    {
        public TxContext(DbContextOptions<TxContext> options) : base(options) { }

        public DbSet<TxItem> Items => Set<TxItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TxItem>(entity =>
            {
                entity.ToTable("TxItems");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
