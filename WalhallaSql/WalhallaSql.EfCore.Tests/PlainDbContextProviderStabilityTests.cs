using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class PlainDbContextProviderStabilityTests
{
    [Fact]
    [Trait("Category", "EFEmbeddedMigrationGate")]
    public void Plain_DbContext_migrate_applies_schema_and_tracks_history_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        context.Database.Migrate();

        var appliedMigrations = context.Database.GetAppliedMigrations().ToArray();

        Assert.Single(appliedMigrations);
        Assert.StartsWith("Auto_", appliedMigrations[0], StringComparison.OrdinalIgnoreCase);

        var customers = scope.QueryAll("SELECT Id FROM Customers");
        var purchases = scope.QueryAll("SELECT Id FROM Purchases");

        Assert.Empty(customers);
        Assert.Empty(purchases);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_savechanges_persists_entities_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        context.Database.Migrate();

        context.Customers.Add(new PlainCustomer
        {
            Id = 1,
            Email = "ada@example.net",
            DisplayName = "Ada Lovelace"
        });
        context.SaveChanges();

        context.Purchases.AddRange(
            new PlainPurchase { Id = 100, CustomerId = 1, Reference = "PO-100", AmountCents = 129900 },
            new PlainPurchase { Id = 101, CustomerId = 1, Reference = "PO-101", AmountCents = 45900 });

        var written = context.SaveChanges();

        Assert.Equal(2, written);

        var rows = scope.QueryAll(@"
SELECT c.DisplayName, p.Reference, p.AmountCents
FROM Customers c
INNER JOIN Purchases p ON p.CustomerId = c.Id
ORDER BY p.Id");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Ada Lovelace", row["DisplayName"]?.ToString()));
        Assert.Equal("PO-100", rows[0]["Reference"]?.ToString());
        Assert.Equal(129900, Convert.ToInt32(rows[0]["AmountCents"]));
        Assert.Equal("PO-101", rows[1]["Reference"]?.ToString());
    }

    [Fact]
    [Trait("Category", "EFEmbeddedMigrationGate")]
    public void Plain_DbContext_ensurecreated_is_idempotent_and_creates_schema_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var created = context.Database.EnsureCreated();
        var createdAgain = context.Database.EnsureCreated();

        Assert.True(created);
        Assert.False(createdAgain);

        context.Customers.Add(new PlainCustomer
        {
            Id = 2,
            Email = "alan@example.net",
            DisplayName = "Alan Turing"
        });
        context.SaveChanges();

        var rows = scope.QueryAll("SELECT Id, Email FROM Customers WHERE Id = 2");
        Assert.Single(rows);
        Assert.Equal("alan@example.net", rows[0]["Email"]?.ToString());
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_connection_string_mode_roundtrips_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateConnectionStringContext();

        context.Database.Migrate();

        context.Customers.Add(new PlainCustomer
        {
            Id = 3,
            Email = "grace@example.net",
            DisplayName = "Grace Hopper"
        });
        context.SaveChanges();

        var rows = scope.QueryAll("SELECT Id, DisplayName FROM Customers WHERE Id = 3");
        Assert.Single(rows);
        Assert.Equal("Grace Hopper", rows[0]["DisplayName"]?.ToString());
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_existing_connection_transaction_dispose_rolls_back_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var connection = new WalhallaSqlDbConnection($"DataSource={scope.DataSourceName};Database=App");
        connection.Open();

        var options = new DbContextOptionsBuilder<PlainShopContext>()
            .UseWalhallaSql(connection)
            .Options;

        using (var setup = new PlainShopContext(options))
        {
            setup.Database.Migrate();
        }

        using (var context = new PlainShopContext(options))
        {
            using var transaction = context.Database.BeginTransaction();

            context.Customers.Add(new PlainCustomer
            {
                Id = 99,
                Email = "tx@example.net",
                DisplayName = "Transaction Probe"
            });

            var written = context.SaveChanges();
            Assert.Equal(1, written);
        }

        var rows = scope.QueryAll("SELECT Id FROM Customers WHERE Id = 99");
        Assert.Empty(rows);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_existing_connection_transaction_query_sees_tx_local_insert_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();
        using var connection = new WalhallaSqlDbConnection($"DataSource={scope.DataSourceName};Database=App");
        connection.Open();

        var options = new DbContextOptionsBuilder<PlainShopContext>()
            .UseWalhallaSql(connection)
            .Options;

        using (var setup = new PlainShopContext(options))
        {
            setup.Database.Migrate();
        }

        using (var context = new PlainShopContext(options))
        {
            using var transaction = context.Database.BeginTransaction();

            context.Customers.Add(new PlainCustomer
            {
                Id = 100,
                Email = "tx-visible@example.net",
                DisplayName = "Transaction Visible"
            });

            var written = context.SaveChanges();
            var visibleCount = context.Customers.AsNoTracking().Count(customer => customer.Id == 100);

            Assert.Equal(1, written);
            Assert.Equal(1, visibleCount);
        }

        var rows = scope.QueryAll("SELECT Id FROM Customers WHERE Id = 100");
        Assert.Empty(rows);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_begintransaction_defaults_to_non_unspecified_isolation_level()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateConnectionStringContext();

        context.Database.Migrate();

        using var transaction = context.Database.BeginTransaction();

        Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_shared_table_dependent_insert_for_existing_parent_roundtrips_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var setup = scope.CreateIdDatabaseContext<PlainSharedTransportContext>())
        {
            setup.Database.Migrate();
            setup.Transports.Add(new PlainSharedTransportRoot { Id = 20, Name = "bus-20" });
            setup.SaveChanges();
        }

        using var context = scope.CreateIdDatabaseContext<PlainSharedTransportContext>();
        var entity = context.Transports.Single(item => item.Id == 20);
        entity.Operator = new PlainLicensedSharedOperator
        {
            Id = 20,
            DisplayName = "operator-20",
            LicenseCode = "LIC-20"
        };

        var written = context.SaveChanges();

        Assert.Equal(1, written);

        var rows = scope.QueryAll("SELECT Id, Name, Operator_Discriminator, Operator_DisplayName, Operator_LicenseCode FROM SharedTransports WHERE Id = 20");
        var row = Assert.Single(rows);
        Assert.Equal("bus-20", row["Name"]?.ToString());
        Assert.Equal("licensed", row["Operator_Discriminator"]?.ToString());
        Assert.Equal("operator-20", row["Operator_DisplayName"]?.ToString());
        Assert.Equal("LIC-20", row["Operator_LicenseCode"]?.ToString());
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_json_complex_property_query_roundtrips_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext<PlainJsonQueryContext>())
        {
            writeContext.Database.Migrate();
            writeContext.Customers.AddRange(
                new PlainJsonCustomer
                {
                    Id = 1,
                    Profile = new PlainJsonProfile
                    {
                        Name = "Anna",
                        Zip = "10115",
                        City = "Berlin"
                    }
                },
                new PlainJsonCustomer
                {
                    Id = 2,
                    Profile = new PlainJsonProfile
                    {
                        Name = "Bert",
                        Zip = "50667",
                        City = "Koeln"
                    }
                });
            writeContext.SaveChanges();
        }

        using var readContext = scope.CreateIdDatabaseContext<PlainJsonQueryContext>();

        var matching = readContext.Customers
            .Where(customer => customer.Profile.Zip == "50667")
            .OrderBy(customer => customer.Id)
            .Select(customer => new { customer.Id, customer.Profile.Name })
            .ToList();

        var orderedIds = readContext.Customers
            .OrderBy(customer => customer.Profile.Zip)
            .Select(customer => customer.Id)
            .ToList();

        var projectionRows = scope.QueryAll("SELECT Id, json__Profile__Zip FROM JsonCustomers ORDER BY Id");
        var storedRows = scope.QueryAll("SELECT Id, Profile FROM JsonCustomers ORDER BY Id");

        var match = Assert.Single(matching);
        Assert.Equal(2, match.Id);
        Assert.Equal("Bert", match.Name);
        Assert.Equal(new[] { 1, 2 }, orderedIds);
        Assert.Equal("10115", projectionRows[0]["json__Profile__Zip"]?.ToString());
        Assert.Equal("50667", projectionRows[1]["json__Profile__Zip"]?.ToString());

        using var firstStoredDocument = JsonDocument.Parse(storedRows[0]["Profile"]?.ToString() ?? throw new InvalidOperationException("Missing JSON payload."));
        Assert.Equal(JsonValueKind.Object, firstStoredDocument.RootElement.ValueKind);
        Assert.Equal("Anna", firstStoredDocument.RootElement.GetProperty("Name").GetString());
        Assert.Equal("10115", firstStoredDocument.RootElement.GetProperty("Zip").GetString());

        using var secondStoredDocument = JsonDocument.Parse(storedRows[1]["Profile"]?.ToString() ?? throw new InvalidOperationException("Missing JSON payload."));
        Assert.Equal("Bert", secondStoredDocument.RootElement.GetProperty("Name").GetString());
        Assert.Equal("50667", secondStoredDocument.RootElement.GetProperty("Zip").GetString());
    }


    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_query_core_roundtrips_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var pagedCustomers = readContext.Customers
            .Where(customer => customer.Id >= 2)
            .OrderBy(customer => customer.Id)
            .Skip(1)
            .Take(1)
            .Select(customer => new { customer.Id, customer.DisplayName })
            .ToList();

        var anyAda = readContext.Customers.Any(customer => customer.Email == "ada@example.net");
        var firstCustomer = readContext.Customers.OrderBy(customer => customer.Id).First();
        var singleCustomer = readContext.Customers
            .Where(customer => customer.Id == 2)
            .Single();
        var orderedPurchaseReferences = readContext.Purchases
            .OrderByDescending(purchase => purchase.AmountCents)
            .Select(purchase => purchase.Reference)
            .ToList();

        var pagedCustomer = Assert.Single(pagedCustomers);
        Assert.Equal(3, pagedCustomer.Id);
        Assert.Equal("Grace Hopper", pagedCustomer.DisplayName);
        Assert.True(anyAda);
        Assert.Equal("Ada Lovelace", firstCustomer.DisplayName);
        Assert.Equal("Alan Turing", singleCustomer.DisplayName);
        Assert.Equal(new[] { "PO-300", "PO-100", "PO-200" }, orderedPurchaseReferences);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_count_and_single_predicate_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var customerCount = readContext.Customers.Count();
        var highValuePurchaseCount = readContext.Purchases.Count(purchase => purchase.AmountCents >= 100000);
        var matchingCustomerIds = readContext.Customers
            .Where(customer => customer.Email == "alan@example.net")
            .OrderBy(customer => customer.Id)
            .Select(customer => customer.Id)
            .ToList();
        var singleCustomer = readContext.Customers.Single(customer => customer.Email == "alan@example.net");

        Assert.Equal(3, customerCount);
        Assert.Equal(2, highValuePurchaseCount);
        Assert.Equal(new[] { 2 }, matchingCustomerIds);
        Assert.Equal(2, singleCustomer.Id);
        Assert.Equal("Alan Turing", singleCustomer.DisplayName);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_reference_and_collection_include_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var customers = readContext.Customers
            .Include(customer => customer.Purchases)
            .OrderBy(customer => customer.Id);

        var purchases = readContext.Purchases
            .Include(purchase => purchase.Customer)
            .OrderBy(purchase => purchase.Id);

        var customerRows = customers.ToList();
        var purchaseRows = purchases.ToList();

        Assert.Equal(3, customerRows.Count);

        var firstCustomer = customerRows[0];
        Assert.Equal("Ada Lovelace", firstCustomer.DisplayName);
        Assert.Single(firstCustomer.Purchases);
        Assert.Equal("PO-100", firstCustomer.Purchases[0].Reference);

        Assert.Equal(3, purchaseRows.Count);
        Assert.Equal("Ada Lovelace", purchaseRows[0].Customer?.DisplayName);
        Assert.Equal("Alan Turing", purchaseRows[1].Customer?.DisplayName);
        Assert.Equal("Grace Hopper", purchaseRows[2].Customer?.DisplayName);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_collection_theninclude_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var customers = readContext.Customers
            .Include(customer => customer.Purchases)
            .ThenInclude(purchase => purchase.Channel)
            .OrderBy(customer => customer.Id)
            .ToList();

        Assert.Equal(3, customers.Count);

        var ada = customers[0];
        Assert.Equal("Ada Lovelace", ada.DisplayName);
        var adaPurchase = Assert.Single(ada.Purchases);
        Assert.Equal("PO-100", adaPurchase.Reference);
        Assert.Equal("Online", adaPurchase.Channel?.Name);

        var alan = customers[1];
        var alanPurchase = Assert.Single(alan.Purchases);
        Assert.Equal("Retail", alanPurchase.Channel?.Name);

        var grace = customers[2];
        var gracePurchase = Assert.Single(grace.Purchases);
        Assert.Equal("Partner", gracePurchase.Channel?.Name);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_filtered_collection_include_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
            writeContext.Purchases.Add(new PlainPurchase
            {
                Id = 103,
                CustomerId = 1,
                ChannelId = 11,
                Reference = "PO-101",
                AmountCents = 45900
            });
            writeContext.SaveChanges();
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var query = readContext.Customers
            .Include(customer => customer.Purchases
                .Where(purchase => purchase.AmountCents >= 100000)
                .OrderByDescending(purchase => purchase.Id)
                .Take(1))
            .OrderBy(customer => customer.Id);

        var customers = query.ToList();

        var ada = customers[0];
        var adaPurchase = Assert.Single(ada.Purchases);
        Assert.Equal("PO-100", adaPurchase.Reference);

        Assert.Empty(customers[1].Purchases);

        var gracePurchase = Assert.Single(customers[2].Purchases);
        Assert.Equal("PO-300", gracePurchase.Reference);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_include_pagination_allows_skip_take_without_orderby_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var query = readContext.Customers
            .Include(customer => customer.Purchases)
            .Skip(1)
            .Take(1);

        var customers = query.ToList();

        Assert.Single(customers);

        Assert.Single(customers);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_include_pagination_with_orderby_is_deterministic_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var query = readContext.Customers
            .Include(customer => customer.Purchases)
            .OrderBy(customer => customer.Id)
            .Skip(1)
            .Take(1);

        var customers = query.ToList();

        var customer = Assert.Single(customers);
        Assert.Equal(2, customer.Id);
        var purchase = Assert.Single(customer.Purchases);
        Assert.Equal("PO-200", purchase.Reference);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_reference_include_assinglequery_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var purchases = readContext.Purchases
            .Include(purchase => purchase.Customer)
            .AsSingleQuery()
            .OrderBy(purchase => purchase.Id)
            .ToList();

        Assert.Equal(3, purchases.Count);
        Assert.Equal("Ada Lovelace", purchases[0].Customer?.DisplayName);
        Assert.Equal("Alan Turing", purchases[1].Customer?.DisplayName);
        Assert.Equal("Grace Hopper", purchases[2].Customer?.DisplayName);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_collection_include_assinglequery_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var customers = readContext.Customers
            .Include(customer => customer.Purchases)
            .AsSingleQuery()
            .OrderBy(customer => customer.Id)
            .ToList();

        Assert.Equal(3, customers.Count);
        Assert.Single(customers[0].Purchases);
        Assert.Equal("PO-100", customers[0].Purchases[0].Reference);
        Assert.Single(customers[1].Purchases);
        Assert.Equal("PO-200", customers[1].Purchases[0].Reference);
        Assert.Single(customers[2].Purchases);
        Assert.Equal("PO-300", customers[2].Purchases[0].Reference);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_regular_dbset_collection_theninclude_assinglequery_roundtrip_without_base_class()
    {
        using var scope = PlainDbContextScope.Create();

        using (var writeContext = scope.CreateIdDatabaseContext())
        {
            writeContext.Database.Migrate();
            PlainShopTestData.SeedPlainShopData(writeContext);
        }

        using var readContext = scope.CreateIdDatabaseContext();

        var customers = readContext.Customers
            .Include(customer => customer.Purchases)
            .ThenInclude(purchase => purchase.Channel)
            .AsSingleQuery()
            .OrderBy(customer => customer.Id)
            .ToList();

        Assert.Equal(3, customers.Count);

        var adaPurchase = Assert.Single(customers[0].Purchases);
        Assert.Equal("PO-100", adaPurchase.Reference);
        Assert.Equal("Online", adaPurchase.Channel?.Name);

        var alanPurchase = Assert.Single(customers[1].Purchases);
        Assert.Equal("Retail", alanPurchase.Channel?.Name);

        var gracePurchase = Assert.Single(customers[2].Purchases);
        Assert.Equal("Partner", gracePurchase.Channel?.Name);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Plain_DbContext_savechanges_persists_dateonly_and_timeonly_as_canonical_text()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        context.Database.Migrate();

        context.Appointments.Add(new PlainAppointment
        {
            Id = 200,
            Title = "Release Check",
            ServiceDate = new DateOnly(2026, 3, 13),
            StartTime = new TimeOnly(14, 15, 16, 123)
        });

        var written = context.SaveChanges();

        Assert.Equal(1, written);

        var rows = scope.QueryAll("SELECT ServiceDate, StartTime FROM Appointments WHERE Id = 200");
        Assert.Single(rows);
        Assert.Equal("2026-03-13", rows[0]["ServiceDate"]?.ToString());
        Assert.Equal("14:15:16.1230000", rows[0]["StartTime"]?.ToString());
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Provider_type_mapping_source_resolves_decimal_as_decimal_store_type()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappingSource.FindMapping(typeof(decimal));

        Assert.NotNull(mapping);
        Assert.Equal("DECIMAL", mapping!.StoreType);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Provider_type_mapping_source_resolves_datetime_as_datetime_store_type()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappingSource.FindMapping(typeof(DateTime));

        Assert.NotNull(mapping);
        Assert.Equal("DATETIME", mapping!.StoreType);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Provider_type_mapping_source_resolves_datetimeoffset_as_datetime_store_type()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappingSource.FindMapping(typeof(DateTimeOffset));

        Assert.NotNull(mapping);
        Assert.Equal("TEXT", mapping!.StoreType);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Provider_type_mapping_source_resolves_dateonly_as_text_store_type()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappingSource.FindMapping(typeof(DateOnly));

        Assert.NotNull(mapping);
        Assert.Equal("TEXT", mapping!.StoreType);
    }

    [Fact]
    [Trait("Category", "EFEmbeddedGate")]
    public void Provider_type_mapping_source_resolves_timeonly_as_text_store_type()
    {
        using var scope = PlainDbContextScope.Create();
        using var context = scope.CreateIdDatabaseContext();

        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappingSource.FindMapping(typeof(TimeOnly));

        Assert.NotNull(mapping);
        Assert.Equal("TEXT", mapping!.StoreType);
    }

    private sealed class PlainDbContextScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;

        private PlainDbContextScope(string dbPath, WalhallaEngine engine, WalhallaEngine database, string dataSourceName)
        {
            _dbPath = dbPath;
            _engine = engine;
            Database = database;
            DataSourceName = dataSourceName;
        }

        public WalhallaEngine Database { get; }

        public string DataSourceName { get; }

        public static PlainDbContextScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "PlainDbContextProviderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            var dataSourceName = "plainctx-" + Guid.NewGuid().ToString("N");
            WalhallaSqlConnectionRegistry.Register(dataSourceName, () => database);

            return new PlainDbContextScope(dbPath, engine, database, dataSourceName);
        }

        public PlainShopContext CreateIdDatabaseContext()
        {
            var options = new DbContextOptionsBuilder<PlainShopContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(Database))
                .Options;

            return new PlainShopContext(options);
        }

        public PlainShopContext CreateConnectionStringContext()
        {
            var options = new DbContextOptionsBuilder<PlainShopContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions($"DataSource={DataSourceName};Database=App"))
                .Options;

            return new PlainShopContext(options);
        }

        public TContext CreateIdDatabaseContext<TContext>()
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

            var rows = new List<Dictionary<string, object?>>();
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
}

internal static class PlainShopTestData
{
    public static void SeedPlainShopData(PlainShopContext context)
    {
        context.Channels.AddRange(
            new PlainChannel { Id = 10, Name = "Online" },
            new PlainChannel { Id = 11, Name = "Retail" },
            new PlainChannel { Id = 12, Name = "Partner" });
        context.SaveChanges();

        context.Customers.AddRange(
            new PlainCustomer { Id = 1, Email = "ada@example.net", DisplayName = "Ada Lovelace" },
            new PlainCustomer { Id = 2, Email = "alan@example.net", DisplayName = "Alan Turing" },
            new PlainCustomer { Id = 3, Email = "grace@example.net", DisplayName = "Grace Hopper" });
        context.SaveChanges();

        context.Purchases.AddRange(
            new PlainPurchase { Id = 100, CustomerId = 1, ChannelId = 10, Reference = "PO-100", AmountCents = 129900 },
            new PlainPurchase { Id = 101, CustomerId = 2, ChannelId = 11, Reference = "PO-200", AmountCents = 45900 },
            new PlainPurchase { Id = 102, CustomerId = 3, ChannelId = 12, Reference = "PO-300", AmountCents = 189900 });
        context.SaveChanges();
    }
}

public sealed class PlainShopContext : DbContext
{
    public PlainShopContext(DbContextOptions<PlainShopContext> options)
        : base(options)
    {
    }

    public DbSet<PlainCustomer> Customers => Set<PlainCustomer>();

    public DbSet<PlainPurchase> Purchases => Set<PlainPurchase>();

    public DbSet<PlainChannel> Channels => Set<PlainChannel>();

    public DbSet<PlainAppointment> Appointments => Set<PlainAppointment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlainCustomer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Id).ValueGeneratedNever();
            entity.Property(customer => customer.Email).IsRequired();
            entity.Property(customer => customer.DisplayName).IsRequired();
            entity.HasIndex(customer => customer.Email).IsUnique();
        });

        modelBuilder.Entity<PlainPurchase>(entity =>
        {
            entity.ToTable("Purchases");
            entity.HasKey(purchase => purchase.Id);
            entity.Property(purchase => purchase.Id).ValueGeneratedNever();
            entity.Property(purchase => purchase.Reference).IsRequired();
            entity.HasIndex(purchase => purchase.Reference).IsUnique();
            entity.HasOne(purchase => purchase.Customer)
                .WithMany(customer => customer.Purchases)
                .HasForeignKey(purchase => purchase.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(purchase => purchase.Channel)
                .WithMany(channel => channel.Purchases)
                .HasForeignKey(purchase => purchase.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlainChannel>(entity =>
        {
            entity.ToTable("Channels");
            entity.HasKey(channel => channel.Id);
            entity.Property(channel => channel.Id).ValueGeneratedNever();
            entity.Property(channel => channel.Name).IsRequired();
            entity.HasIndex(channel => channel.Name).IsUnique();
        });

        modelBuilder.Entity<PlainAppointment>(entity =>
        {
            entity.ToTable("Appointments");
            entity.HasKey(appointment => appointment.Id);
            entity.Property(appointment => appointment.Id).ValueGeneratedNever();
            entity.Property(appointment => appointment.Title).IsRequired();
            entity.Property(appointment => appointment.ServiceDate).IsRequired();
            entity.Property(appointment => appointment.StartTime).IsRequired();
        });
    }
}

public sealed class PlainSharedTransportContext : DbContext
{
    public PlainSharedTransportContext(DbContextOptions<PlainSharedTransportContext> options)
        : base(options)
    {
    }

    public DbSet<PlainSharedTransportRoot> Transports => Set<PlainSharedTransportRoot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlainSharedTransportRoot>(entity =>
        {
            entity.ToTable("SharedTransports");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Name).IsRequired();
            entity.HasOne(item => item.Operator)
                .WithOne()
                .HasForeignKey<PlainSharedOperatorBase>(item => item.Id)
                .IsRequired(false);
        });

        modelBuilder.Entity<PlainSharedOperatorBase>(entity =>
        {
            entity.ToTable("SharedTransports");
            entity.Property(item => item.DisplayName).HasColumnName("Operator_DisplayName").IsRequired();
            entity.HasDiscriminator<string>("Operator_Discriminator")
                .HasValue<PlainSharedOperatorBase>("base")
                .HasValue<PlainLicensedSharedOperator>("licensed");
        });

        modelBuilder.Entity<PlainLicensedSharedOperator>(entity =>
        {
            entity.Property(item => item.LicenseCode).HasColumnName("Operator_LicenseCode").IsRequired();
        });
    }
}

public sealed class PlainJsonQueryContext : DbContext
{
    public PlainJsonQueryContext(DbContextOptions<PlainJsonQueryContext> options)
        : base(options)
    {
    }

    public DbSet<PlainJsonCustomer> Customers => Set<PlainJsonCustomer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlainJsonCustomer>(entity =>
        {
            entity.ToTable("JsonCustomers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Id).ValueGeneratedNever();
            entity.OwnsOne(
                customer => customer.Profile,
                owned =>
                {
                    owned.ToJson("Profile");
                    owned.Property(profile => profile.Name).IsRequired();
                    owned.Property(profile => profile.Zip).IsRequired();
                    owned.Property(profile => profile.City).IsRequired();
                });
            entity.Navigation(customer => customer.Profile).IsRequired();
        });
    }
}


public sealed class PlainCustomer
{
    public int Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<PlainPurchase> Purchases { get; set; } = new();
}

public sealed class PlainJsonCustomer
{
    public int Id { get; set; }

    public PlainJsonProfile Profile { get; set; } = new();
}

public sealed class PlainJsonProfile
{
    public string Name { get; set; } = string.Empty;

    public string Zip { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;
}

public sealed class PlainPurchase
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public int? ChannelId { get; set; }

    public string Reference { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    public PlainCustomer? Customer { get; set; }

    public PlainChannel? Channel { get; set; }
}

public sealed class PlainChannel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<PlainPurchase> Purchases { get; set; } = new();
}

public sealed class PlainAppointment
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateOnly ServiceDate { get; set; }

    public TimeOnly StartTime { get; set; }
}

public sealed class PlainSharedTransportRoot
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public PlainSharedOperatorBase? Operator { get; set; }
}

public class PlainSharedOperatorBase
{
    public int Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class PlainLicensedSharedOperator : PlainSharedOperatorBase
{
    public string LicenseCode { get; set; } = string.Empty;
}


