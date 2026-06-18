using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class AdHocAdvancedMappings32911RegressionTests
{
    [Fact]
    public void SaveChanges_persists_required_complex_values_for_32911_models()
    {
        using var scope = RegressionScope.Create();

        using (var context = scope.CreateDbContext<ComplexPaymentContext>())
        {
            context.Database.EnsureCreated();
            ComplexPaymentContext.Seed(context);
        }

        var variationRows = scope.QueryAll("SELECT * FROM Variation ORDER BY Id");
        var nestedRows = scope.QueryAll("SELECT * FROM NestedEntity ORDER BY Id");

        Assert.Collection(
            variationRows,
            row =>
            {
                Assert.Equal(1, Convert.ToInt32(row["Id"]));
                Assert.Equal(1, Convert.ToInt32(row["OfferId"]));
                Assert.Equal(1, Convert.ToInt32(row["NestedId"]));
                Assert.Equal(1d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(10d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            },
            row =>
            {
                Assert.Equal(2, Convert.ToInt32(row["Id"]));
                Assert.Equal(1, Convert.ToInt32(row["OfferId"]));
                Assert.Equal(2, Convert.ToInt32(row["NestedId"]));
                Assert.Equal(2d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(20d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            },
            row =>
            {
                Assert.Equal(3, Convert.ToInt32(row["Id"]));
                Assert.Equal(1, Convert.ToInt32(row["OfferId"]));
                Assert.Equal(3, Convert.ToInt32(row["NestedId"]));
                Assert.Equal(3d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(30d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            });

        Assert.Collection(
            nestedRows,
            row =>
            {
                Assert.Equal(1, Convert.ToInt32(row["Id"]));
                Assert.Equal(10d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(100d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            },
            row =>
            {
                Assert.Equal(2, Convert.ToInt32(row["Id"]));
                Assert.Equal(20d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(200d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            },
            row =>
            {
                Assert.Equal(3, Convert.ToInt32(row["Id"]));
                Assert.Equal(30d, Convert.ToDouble(GetBySuffix(row, "Netto")));
                Assert.Equal(300d, Convert.ToDouble(GetBySuffix(row, "Brutto")));
            });
    }

    [Fact]
    public void Split_query_with_similar_complex_properties_materializes_nested_payment_values()
    {
        using var scope = RegressionScope.Create();

        using (var context = scope.CreateDbContext<ComplexPaymentContext>())
        {
            context.Database.EnsureCreated();
            ComplexPaymentContext.Seed(context);
        }

        using var queryContext = scope.CreateDbContext<ComplexPaymentContext>();
        var result = queryContext.Offers
            .Include(offer => offer.Variations)
            .ThenInclude(variation => variation.Nested)
            .AsSplitQuery()
            .Single(offer => offer.Id == 1);

        Assert.Collection(
            result.Variations.OrderBy(variation => variation.Id),
            variation =>
            {
                Assert.Equal(10d, variation.Payment.Brutto);
                Assert.Equal(100d, variation.Nested.Payment.Brutto);
            },
            variation =>
            {
                Assert.Equal(20d, variation.Payment.Brutto);
                Assert.Equal(200d, variation.Nested.Payment.Brutto);
            },
            variation =>
            {
                Assert.Equal(30d, variation.Payment.Brutto);
                Assert.Equal(300d, variation.Nested.Payment.Brutto);
            });
    }

    [Fact]
    public void Projection_of_one_of_two_similar_complex_types_reads_inner_created_value()
    {
        using var scope = RegressionScope.Create();

        using (var context = scope.CreateDbContext<ComplexMetadataContext>())
        {
            context.Database.EnsureCreated();
            ComplexMetadataContext.Seed(context);
        }

        var bRows = scope.QueryAll("SELECT * FROM Bs ORDER BY Id");
        var cRows = scope.QueryAll("SELECT * FROM Cs ORDER BY Id");

        var bRow = Assert.Single(bRows);
        Assert.Equal(10, Convert.ToInt32(bRow["Id"]));
        Assert.Equal(1, Convert.ToInt32(bRow["AId"]));
        Assert.Equal(new DateTime(2000, 1, 1), Convert.ToDateTime(GetBySuffix(bRow, "Created")));

        var cRow = Assert.Single(cRows);
        Assert.Equal(100, Convert.ToInt32(cRow["Id"]));
        Assert.Equal(10, Convert.ToInt32(cRow["BId"]));
        Assert.Equal(new DateTime(2020, 10, 10), Convert.ToDateTime(GetBySuffix(cRow, "Created")));

        using var queryContext = scope.CreateDbContext<ComplexMetadataContext>();
        var projection = queryContext.Cs
            .Where(entity => entity.B.AId == 1)
            .OrderBy(entity => entity.Id)
            .Take(10)
            .Select(entity => new
            {
                entity.B.A.Id,
                entity.B.Info.Created
            })
            .Single();

        Assert.Equal(1, projection.Id);
        Assert.Equal(new DateTime(2000, 1, 1), projection.Created);
    }

    private sealed class RegressionScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;

        private RegressionScope(string dbPath, WalhallaEngine engine, WalhallaEngine database, string dataSourceName)
        {
            _dbPath = dbPath;
            _engine = engine;
            Database = database;
            DataSourceName = dataSourceName;
        }

        public WalhallaEngine Database { get; }

        public string DataSourceName { get; }

        public static RegressionScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "AdHocAdvancedMappings32911", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            var dataSourceName = "adhoc32911-" + Guid.NewGuid().ToString("N");
            WalhallaSqlConnectionRegistry.Register(dataSourceName, () => database);

            return new RegressionScope(dbPath, engine, database, dataSourceName);
        }

        public TContext CreateDbContext<TContext>()
            where TContext : DbContext
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(Database))
                .Options;

            return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryAll(string sql)
        {
            using var connection = new WalhallaSqlDbConnection(Database);
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

    private static object? GetBySuffix(IReadOnlyDictionary<string, object?> row, string suffix)
    {
        if (row.TryGetValue(suffix, out var direct))
            return direct;

        var match = row.FirstOrDefault(pair => pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Key))
            return match.Value;

        throw new InvalidOperationException($"No column ending with '{suffix}' found. Available columns: {string.Join(", ", row.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}.");
    }

    private sealed class ComplexPaymentContext : DbContext
    {
        public ComplexPaymentContext(DbContextOptions<ComplexPaymentContext> options)
            : base(options)
        {
        }

        public DbSet<Offer> Offers => Set<Offer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Offer>(entity =>
            {
                entity.ToTable("Offers");
                entity.HasKey(offer => offer.Id);
                entity.Property(offer => offer.Id).ValueGeneratedNever();
                entity.HasMany(offer => offer.Variations)
                    .WithOne(variation => variation.Offer)
                    .HasForeignKey(variation => variation.OfferId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Variation>(entity =>
            {
                entity.ToTable("Variation");
                entity.HasKey(variation => variation.Id);
                entity.Property(variation => variation.Id).ValueGeneratedNever();
                entity.ComplexProperty(
                    variation => variation.Payment,
                    complex =>
                    {
                        complex.IsRequired();
                        complex.Property(payment => payment.Netto).HasColumnName("payment_netto");
                        complex.Property(payment => payment.Brutto).HasColumnName("payment_brutto");
                    });
                entity.HasOne(variation => variation.Nested)
                    .WithOne()
                    .HasForeignKey<Variation>(variation => variation.NestedId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<NestedEntity>(entity =>
            {
                entity.ToTable("NestedEntity");
                entity.HasKey(nested => nested.Id);
                entity.Property(nested => nested.Id).ValueGeneratedNever();
                entity.ComplexProperty(
                    nested => nested.Payment,
                    complex =>
                    {
                        complex.IsRequired();
                        complex.Property(payment => payment.Netto).HasColumnName("payment_netto");
                        complex.Property(payment => payment.Brutto).HasColumnName("payment_brutto");
                    });
            });
        }

        public static void Seed(ComplexPaymentContext context)
        {
            var v1 = new Variation
            {
                Id = 1,
                Payment = new Payment(1, 10),
                Nested = new NestedEntity { Id = 1, Payment = new Payment(10, 100) }
            };

            var v2 = new Variation
            {
                Id = 2,
                Payment = new Payment(2, 20),
                Nested = new NestedEntity { Id = 2, Payment = new Payment(20, 200) }
            };

            var v3 = new Variation
            {
                Id = 3,
                Payment = new Payment(3, 30),
                Nested = new NestedEntity { Id = 3, Payment = new Payment(30, 300) }
            };

            context.Offers.Add(
                new Offer
                {
                    Id = 1,
                    Variations = new List<Variation> { v1, v2, v3 }
                });

            context.SaveChanges();
        }

        public sealed class Offer
        {
            public int Id { get; set; }

            public ICollection<Variation> Variations { get; set; } = new List<Variation>();
        }

        public sealed class Variation
        {
            public int Id { get; set; }

            public int OfferId { get; set; }

            public Offer Offer { get; set; } = null!;

            public int? NestedId { get; set; }

            public Payment Payment { get; set; } = new(0, 0);

            public NestedEntity Nested { get; set; } = null!;
        }

        public sealed class NestedEntity
        {
            public int Id { get; set; }

            public Payment Payment { get; set; } = new(0, 0);
        }

        public sealed record Payment(double Netto, double Brutto);
    }

    private sealed class ComplexMetadataContext : DbContext
    {
        public ComplexMetadataContext(DbContextOptions<ComplexMetadataContext> options)
            : base(options)
        {
        }

        public DbSet<A> As => Set<A>();

        public DbSet<B> Bs => Set<B>();

        public DbSet<C> Cs => Set<C>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<A>(entity =>
            {
                entity.ToTable("As");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<B>(entity =>
            {
                entity.ToTable("Bs");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.ComplexProperty(value => value.Info).IsRequired();
                entity.HasOne(value => value.A)
                    .WithMany()
                    .HasForeignKey(value => value.AId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<C>(entity =>
            {
                entity.ToTable("Cs");
                entity.HasKey(value => value.Id);
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.ComplexProperty(value => value.Info).IsRequired();
                entity.HasOne(value => value.B)
                    .WithMany()
                    .HasForeignKey(value => value.BId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }

        public static void Seed(ComplexMetadataContext context)
        {
            context.Cs.Add(
                new C
                {
                    Id = 100,
                    Info = new Metadata { Created = new DateTime(2020, 10, 10) },
                    B = new B
                    {
                        Id = 10,
                        Info = new Metadata { Created = new DateTime(2000, 1, 1) },
                        A = new A { Id = 1 }
                    }
                });

            context.SaveChanges();
        }

        public sealed class Metadata
        {
            public DateTime Created { get; set; }
        }

        public sealed class A
        {
            public int Id { get; set; }
        }

        public sealed class B
        {
            public int Id { get; set; }

            public Metadata Info { get; set; } = new();

            public int? AId { get; set; }

            public A A { get; set; } = null!;
        }

        public sealed class C
        {
            public int Id { get; set; }

            public Metadata Info { get; set; } = new();

            public int BId { get; set; }

            public B B { get; set; } = null!;
        }
    }
}
