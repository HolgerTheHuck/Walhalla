using System;
using System.Linq;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den Connection-String-Mode:
/// DbContext via DataSource=...-String statt direkter Engine-Referenz.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalConnectionStringTests
{
    [Fact]
    public void Connection_string_mode_insert_and_query_roundtrips()
    {
        using var scope = ConnectionStringScope.Create();

        scope.Context.Items.Add(new CsItem { Id = 1, Name = "Test" });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Items.Find(1);
        Assert.NotNull(found);
        Assert.Equal("Test", found!.Name);
    }

    private sealed class ConnectionStringScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly string _dataSourceName;

        private ConnectionStringScope(string dbPath, WalhallaEngine engine, string dataSourceName, CsContext context)
        {
            _dbPath = dbPath;
            _engine = engine;
            _dataSourceName = dataSourceName;
            Context = context;
        }

        public CsContext Context { get; }

        public static ConnectionStringScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "MinimalSpecs", "ConnectionString", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);
            var dataSourceName = "minimal-cs-" + Guid.NewGuid().ToString("N");
            WalhallaSqlConnectionRegistry.Register(dataSourceName, () => engine);

            // Die Connection wird mit dem expliziten Engine-Konstruktor erstellt,
            // damit _hasExplicitEngine = true und Close() die shared Engine nicht disposet.
            // Der Connection-String wird trotzdem für die DataSource-Auflösung genutzt.
            var conn = new WalhallaSqlDbConnection(engine, $"DataSource={dataSourceName};Database=App");
            var options = new DbContextOptionsBuilder<CsContext>()
                .UseWalhallaSql(conn)
                .Options;

            var context = new CsContext(options);
            context.Migrations.ApplyPlannedChanges("20260616_MinimalConnectionString");

            return new ConnectionStringScope(dbPath, engine, dataSourceName, context);
        }

        public void Dispose()
        {
            Context.Dispose();
            _engine.Dispose();
            try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
        }
    }

    public sealed class CsItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CsContext : WalhallaSqlEfCoreContext
    {
        public CsContext(DbContextOptions options) : base(options) { }

        public DbSet<CsItem> Items => Set<CsItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CsItem>(entity =>
            {
                entity.ToTable("CsItems");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
