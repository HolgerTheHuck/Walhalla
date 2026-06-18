using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class TpcSaveChangesRegressionTests
{
    [Fact]
    public void SaveChanges_persists_shared_table_identifying_one_to_one_in_single_work_item()
    {
        using var scope = TpcSaveChangesScope.Create();
        using var context = scope.CreateContext();

        var street = new StreetCircuitTpc
        {
            Id = 1,
            Name = "Monaco",
            Length = 3337
        };
        var city = new CityTpc
        {
            Id = 1,
            Name = "Monaco",
            Circuit = street
        };
        street.City = city;

        context.AddRange(street, city);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        var persistedStreet = context.Set<StreetCircuitTpc>().Single(entity => entity.Id == 1);
        var persistedCity = context.Set<CityTpc>().Single(entity => entity.Id == 1);

        Assert.Equal("Monaco", persistedStreet.Name);
        Assert.Equal(3337, persistedStreet.Length);
        Assert.Equal("Monaco", persistedCity.Name);
    }

    private sealed class TpcSaveChangesScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private TpcSaveChangesScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static TpcSaveChangesScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "TpcSaveChangesRegressionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;

            using var context = CreateContext(database);
            context.Database.EnsureCreated();

            return new TpcSaveChangesScope(dbPath, engine, database);
        }

        public TpcSaveChangesContext CreateContext()
            => CreateContext(_database);

        private static TpcSaveChangesContext CreateContext(WalhallaEngine database)
        {
            var options = new DbContextOptionsBuilder<TpcSaveChangesContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
                .Options;

            return new TpcSaveChangesContext(options);
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

    private sealed class TpcSaveChangesContext(DbContextOptions<TpcSaveChangesContext> options)
        : WalhallaSqlEfCoreContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CircuitTpcBase>(entity =>
            {
                entity.UseTpcMappingStrategy();
                entity.Property(circuit => circuit.Id).ValueGeneratedNever();
                entity.Property(circuit => circuit.Name).HasColumnName("Name");
            });

            modelBuilder.Entity<StreetCircuitTpc>(entity =>
            {
                entity.ToTable("StreetCircuitsTpc");
                entity.HasOne(street => street.City)
                    .WithOne(city => city.Circuit)
                    .HasForeignKey<CityTpc>(city => city.Id);
            });

            modelBuilder.Entity<OvalCircuitTpc>(entity =>
            {
                entity.ToTable("OvalCircuitsTpc");
            });

            modelBuilder.Entity<CityTpc>(entity =>
            {
                entity.ToTable("StreetCircuitsTpc");
                entity.Property(city => city.Name).HasColumnName("Name");
                entity.Property(city => city.Id).ValueGeneratedNever();
            });
        }
    }

    private abstract class CircuitTpcBase
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class StreetCircuitTpc : CircuitTpcBase
    {
        public int Length { get; set; }
        public CityTpc? City { get; set; }
    }

    private sealed class OvalCircuitTpc : CircuitTpcBase
    {
        public int Banks { get; set; }
    }

    private sealed class CityTpc
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public StreetCircuitTpc? Circuit { get; set; }
    }
}
