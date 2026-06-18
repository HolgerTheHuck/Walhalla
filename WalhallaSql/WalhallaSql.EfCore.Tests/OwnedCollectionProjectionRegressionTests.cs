using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class OwnedCollectionProjectionRegressionTests
{
    [Fact]
    public void Correlated_owned_collection_projection_preserves_expected_values()
    {
        using var scope = RegressionScope.Create();

        using (var setupContext = scope.CreateContext<OwnedCollectionProjectionContext>())
        {
            setupContext.Database.EnsureCreated();
            OwnedCollectionProjectionContext.Seed(setupContext);
        }

        var childRows = scope.QueryAll("SELECT Id, WarehouseCode, CountryCode FROM WarehouseDestinationCountry ORDER BY Id");
        Assert.Equal(2, childRows.Count);

        var joinedRows = scope.QueryAll("""
    SELECT w.WarehouseCode, w.Id, w0.CountryCode, w0.WarehouseCode AS ChildWarehouseCode, w0.Id AS ChildId
    FROM Warehouses AS w
    LEFT JOIN WarehouseDestinationCountry AS w0 ON w.WarehouseCode = w0.WarehouseCode
    ORDER BY w.Id, w0.Id
    """);
        Assert.Equal(2, joinedRows.Count);

        using var context = scope.CreateContext<OwnedCollectionProjectionContext>();
        var result = context.Warehouses
            .Select(x => new OwnedCollectionProjectionContext.WarehouseModel
            {
                WarehouseCode = x.WarehouseCode,
                DestinationCountryCodes = x.DestinationCountries.Select(c => c.CountryCode).ToArray()
            })
            .AsNoTracking()
            .Single();

        Assert.Equal("W001", result.WarehouseCode);
        Assert.Equal(["US", "CA"], result.DestinationCountryCodes.ToArray());
    }

    private sealed class RegressionScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private RegressionScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static RegressionScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "OwnedCollectionProjectionRegressionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            return new RegressionScope(dbPath, engine, database);
        }

        public TContext CreateContext<TContext>() where TContext : DbContext
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(_database))
                .Options;

            return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryAll(string sql)
        {
            using var connection = new WalhallaSqlDbConnection(_database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                rows.Add(row);
            }

            return rows;
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

    private sealed class OwnedCollectionProjectionContext(DbContextOptions<OwnedCollectionProjectionContext> options)
        : DbContext(options)
    {
        public DbSet<Warehouse> Warehouses => Set<Warehouse>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Warehouse>()
                .OwnsMany(x => x.DestinationCountries)
                .WithOwner()
                .HasForeignKey(x => x.WarehouseCode)
                .HasPrincipalKey(x => x.WarehouseCode);

        public static void Seed(OwnedCollectionProjectionContext context)
        {
            context.Add(
                new Warehouse
                {
                    WarehouseCode = "W001",
                    DestinationCountries =
                    {
                        new WarehouseDestinationCountry { Id = "1", CountryCode = "US" },
                        new WarehouseDestinationCountry { Id = "2", CountryCode = "CA" }
                    }
                });

            context.SaveChanges();
        }

        public sealed class Warehouse
        {
            public int Id { get; set; }
            public string WarehouseCode { get; set; } = string.Empty;
            public ICollection<WarehouseDestinationCountry> DestinationCountries { get; set; } = new HashSet<WarehouseDestinationCountry>();
        }

        public sealed class WarehouseDestinationCountry
        {
            public string Id { get; set; } = string.Empty;
            public string WarehouseCode { get; set; } = string.Empty;
            public string CountryCode { get; set; } = string.Empty;
        }

        public sealed class WarehouseModel
        {
            public string WarehouseCode { get; set; } = string.Empty;
            public ICollection<string> DestinationCountryCodes { get; set; } = Array.Empty<string>();
        }
    }
}
