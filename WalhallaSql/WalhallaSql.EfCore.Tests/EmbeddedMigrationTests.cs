using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using WalhallaSql.EfCore.Migrations;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Tests for EF Core Migrations (WalhallaSqlMigrationService).
/// Covers: CreateTable, DropTable, RenameTable, AddColumn, DropColumn,
/// RenameColumn, AlterColumn, CreateIndex, DropIndex, AddForeignKey,
/// model-driven diff (PlanModelChanges) and history tracking.
/// </summary>
[Trait("Category", "EFEmbeddedMigrationGate")]
public sealed class EmbeddedMigrationTests
{
    [Fact]
    public void CreateTable_plan_creates_insertable_and_queryable_collection()
    {
        using var scope = MigrationScope.Create();

        var table = new SqlTableDefinition(
            "Products",
            new[] { Col("Id", SqlScalarType.Int32, pk: true), Col("Name", SqlScalarType.String) },
            Array.Empty<SqlIndexDefinition>());

        scope.ApplyExplicit("Mig_CreateProducts",
            new CreateTableOperation(table));

        scope.Exec("INSERT INTO Products (Id, Name) VALUES (1, 'Widget')");
        var row = scope.QueryFirst("SELECT Id, Name FROM Products WHERE Id = 1");
        Assert.Equal("Widget", row["Name"]?.ToString());
    }

    [Fact]
    public void DropTable_plan_makes_collection_inaccessible()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Logs (Id INT PRIMARY KEY, Msg VARCHAR(500) NOT NULL)");
        scope.Exec("INSERT INTO Logs (Id, Msg) VALUES (1, 'hello')");

        scope.ApplyExplicit("Mig_DropLogs",
            new DropTableOperation("Logs"));

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("SELECT Id FROM Logs"));
    }

    [Fact]
    public void RenameTable_plan_makes_old_name_inaccessible_new_name_insertable()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE OldItems (Id INT PRIMARY KEY, Tag VARCHAR(100) NOT NULL)");
        scope.Exec("INSERT INTO OldItems (Id, Tag) VALUES (1, 'alpha')");

        scope.ApplyExplicit("Mig_RenameOldItemsToNewItems",
            new RenameTableOperation("OldItems", "NewItems"));

        scope.Exec("INSERT INTO NewItems (Id, Tag) VALUES (2, 'beta')");
        var rows = scope.QueryAll("SELECT Id, Tag FROM NewItems");
        Assert.Equal(2, rows.Count);

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("SELECT Id FROM OldItems"));
    }

    [Fact]
    public void AddColumn_plan_enables_new_column_in_insert_and_select()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Items (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");
        scope.Exec("INSERT INTO Items (Id, Name) VALUES (1, 'Gadget')");

        scope.ApplyExplicit("Mig_AddPrice",
            new AddColumnOperation("Items",
                Col("Price", SqlScalarType.Double, nullable: true),
                DefaultValueLiteral: "0"));

        scope.Exec("INSERT INTO Items (Id, Name, Price) VALUES (2, 'Widget', 9.99)");
        var row = scope.QueryFirst("SELECT Id, Name, Price FROM Items WHERE Id = 2");
        Assert.Equal(9.99, Convert.ToDouble(row["Price"], CultureInfo.InvariantCulture), precision: 2);
    }

    [Fact]
    public void DropColumn_plan_removes_column_from_schema()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Items (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, TempCol VARCHAR(100))");
        scope.Exec("INSERT INTO Items (Id, Name, TempCol) VALUES (1, 'A', 'x')");

        scope.ApplyExplicit("Mig_DropTempCol",
            new DropColumnOperation("Items", "TempCol"));

        var row = scope.QueryFirst("SELECT Id, Name FROM Items WHERE Id = 1");
        Assert.Equal("A", row["Name"]?.ToString());
        Assert.False(row.ContainsKey("TempCol"), "TempCol should be gone after DropColumn.");
    }

    [Fact]
    public void RenameColumn_plan_old_name_unavailable_new_name_readable()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Items (Id INT PRIMARY KEY, Bezeichnung VARCHAR(200) NOT NULL)");
        scope.Exec("INSERT INTO Items (Id, Bezeichnung) VALUES (1, 'Foo')");

        scope.ApplyExplicit("Mig_RenameBezeichnungToName",
            new RenameColumnOperation("Items", "Bezeichnung", "Name"));

        var row = scope.QueryFirst("SELECT Id, Name FROM Items WHERE Id = 1");
        Assert.Equal("Foo", row["Name"]?.ToString());

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("SELECT Bezeichnung FROM Items"));
    }

    [Fact]
    public void AlterColumn_plan_changes_column_to_nullable()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Items (Id INT PRIMARY KEY, Code VARCHAR(50) NOT NULL)");
        scope.Exec("INSERT INTO Items (Id, Code) VALUES (1, 'X')");

        scope.ApplyExplicit("Mig_AlterCodeNullable",
            new AlterColumnOperation("Items",
                Col("Code", SqlScalarType.String, nullable: true)));

        scope.Exec("INSERT INTO Items (Id, Code) VALUES (2, NULL)");
        var row = scope.QueryFirst("SELECT Id, Code FROM Items WHERE Id = 2");
        Assert.Null(row["Code"]);
    }

    [Fact]
    public void CreateIndex_plan_is_reflected_and_unique_index_rejects_duplicates()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Users (Id INT PRIMARY KEY, Email VARCHAR(200) NOT NULL)");
        scope.Exec("INSERT INTO Users (Id, Email) VALUES (1, 'alice@example.com')");

        scope.ApplyExplicit("Mig_CreateUniqueEmailIndex",
            new CreateIndexOperation("Users",
                new SqlIndexDefinition("IX_Users_Email", "Email", isUnique: true)));

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO Users (Id, Email) VALUES (2, 'alice@example.com')"));

        scope.Exec("INSERT INTO Users (Id, Email) VALUES (3, 'bob@example.com')");
    }

    [Fact]
    public void DropIndex_plan_allows_previously_blocked_duplicates()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Users (Id INT PRIMARY KEY, Email VARCHAR(200) NOT NULL)");
        scope.Exec("CREATE UNIQUE INDEX IX_Users_Email ON Users (Email)");
        scope.Exec("INSERT INTO Users (Id, Email) VALUES (1, 'alice@example.com')");

        scope.ApplyExplicit("Mig_DropEmailIndex",
            new DropIndexOperation("Users", "IX_Users_Email"));

        scope.Exec("INSERT INTO Users (Id, Email) VALUES (2, 'alice@example.com')");
        var count = scope.QueryAll("SELECT Id FROM Users WHERE Email = 'alice@example.com'");
        Assert.Equal(2, count.Count);
    }

    [Fact]
    public void AddForeignKey_plan_enforces_referential_integrity()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Categories (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");
        scope.Exec("INSERT INTO Categories (Id, Name) VALUES (1, 'Electronics')");
        scope.Exec("INSERT INTO Products (Id, Name, CategoryId) VALUES (1, 'Phone', 1)");

        scope.ApplyExplicit("Mig_AddProductCategoryFK",
            new AddForeignKeyOperation("Products",
                new SqlForeignKeyDefinition(
                    "FK_Products_CategoryId",
                    "CategoryId",
                    "Categories",
                    "Id",
                    SqlForeignKeyAction.Restrict,
                    SqlForeignKeyAction.Restrict)));

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO Products (Id, Name, CategoryId) VALUES (2, 'Tablet', 99)"));
    }

    [Fact]
    public void PlanModelChanges_optional_fk_with_client_set_null_is_supported()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<ClientSetNullContext>();

        var plan = context.Migrations.PlanModelChanges();

        Assert.True(plan.HasChanges);
        context.Migrations.ApplyPlannedChanges("Mig_ClientSetNull_V1");

        scope.Exec("INSERT INTO Parents (Id, Name) VALUES (1, 'root')");
        scope.Exec("INSERT INTO Children (Id, Name, ParentId) VALUES (1, 'orphan', NULL)");
        scope.Exec("INSERT INTO Children (Id, Name, ParentId) VALUES (2, 'linked', 1)");

        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO Children (Id, Name, ParentId) VALUES (3, 'invalid', 99)"));
    }

    [Fact]
    public void EnsureCreated_applies_model_seed_data()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<SeededCatalogContext>();

        var created = context.Database.EnsureCreated();

        Assert.True(created);

        var categories = scope.QueryAll("SELECT Id, Name FROM SeedCategories ORDER BY Id");
        var products = scope.QueryAll("SELECT Id, Name, CategoryId FROM SeedProducts ORDER BY Id");

        Assert.Equal(2, categories.Count);
        Assert.Equal(2, products.Count);
        Assert.Equal("Hardware", categories[0]["Name"]?.ToString());
        Assert.Equal(10L, Convert.ToInt64(products[0]["CategoryId"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PlanModelChanges_empty_db_detects_all_tables_as_new()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        var plan = context.Migrations.PlanModelChanges();

        Assert.True(plan.HasChanges);
        Assert.Contains(plan.Operations, op => op is CreateTableOperation ct &&
            string.Equals(ct.Table.CollectionName, "Products", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanModelChanges_after_apply_detects_no_changes()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        context.Migrations.ApplyPlannedChanges("Mig_V1");

        var planAfter = context.Migrations.PlanModelChanges();
        Assert.False(planAfter.HasChanges,
            $"Expected no changes after apply, but got: {string.Join(", ", planAfter.Operations.Select(o => o.GetType().Name))}");
    }

    [Fact]
    public void PlanModelChanges_optional_shared_table_dependent_discriminator_is_nullable()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<OptionalSharedTableDiscriminatorContext>();

        var plan = context.Migrations.PlanModelChanges();
        var createTable = Assert.Single(plan.Operations.OfType<CreateTableOperation>());
        var discriminator = Assert.Single(
            createTable.Table.Columns,
            column => string.Equals(column.Name, "Operator_Discriminator", StringComparison.OrdinalIgnoreCase));

        Assert.True(discriminator.IsNullable);
    }

    [Fact]
    public void PlanModelChanges_detects_added_column_between_model_versions()
    {
        using var scope = MigrationScope.Create();

        using (var v1 = scope.CreateContext<V1ProductContext>())
            v1.Migrations.ApplyPlannedChanges("Mig_V1");

        using var v2 = scope.CreateContext<V2ProductContext>();
        var plan = v2.Migrations.PlanModelChanges();

        Assert.True(plan.HasChanges);
        Assert.Contains(plan.Operations, op =>
            op is AddColumnOperation add &&
            string.Equals(add.Column.Name, "Price", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanModelChanges_detects_dropped_table_when_removed_from_model()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Legacy (Id INT PRIMARY KEY, Data VARCHAR(100))");

        using (var v1 = scope.CreateContext<V1ProductContext>())
            v1.Migrations.ApplyPlannedChanges("Mig_V1");

        using var emptyCtx = scope.CreateContext<EmptyModelContext>();
        var plan = emptyCtx.Migrations.PlanModelChanges();

        Assert.True(plan.HasChanges);
        Assert.Contains(plan.Operations, op => op is DropTableOperation drop &&
            string.Equals(drop.CollectionName, "Products", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Operations, op => op is DropTableOperation drop &&
            string.Equals(drop.CollectionName, "Legacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyPlannedChanges_single_column_alternate_key_enforces_uniqueness()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<AlternateKeyProductContext>();

        context.Migrations.ApplyPlannedChanges("Mig_AltKey_V1");

        scope.Exec("INSERT INTO Products (Id, Name, Sku) VALUES (1, 'Widget', 'SKU-001')");
        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO Products (Id, Name, Sku) VALUES (2, 'Widget 2', 'SKU-001')"));

        scope.Exec("INSERT INTO Products (Id, Name, Sku) VALUES (3, 'Widget 3', 'SKU-003')");
        var rows = scope.QueryAll("SELECT Id, Sku FROM Products ORDER BY Id");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ApplyPlannedChanges_multi_column_alternate_key_enforces_uniqueness()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<CompositeAlternateKeyProductContext>();

        var plan = context.Migrations.PlanModelChanges();
        var createIndex = Assert.Single(plan.Operations.OfType<CreateIndexOperation>());
        Assert.Equal(new[] { "TenantId", "Sku" }, createIndex.Index.ColumnNames);
        Assert.True(createIndex.Index.IsUnique);

        context.Migrations.ApplyPlannedChanges("Mig_CompositeAltKey_V1");

        scope.Exec("INSERT INTO CompositeProducts (Id, TenantId, Name, Sku) VALUES (1, 10, 'Widget', 'SKU-001')");
        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO CompositeProducts (Id, TenantId, Name, Sku) VALUES (2, 10, 'Widget 2', 'SKU-001')"));

        scope.Exec("INSERT INTO CompositeProducts (Id, TenantId, Name, Sku) VALUES (3, 11, 'Widget 3', 'SKU-001')");
        scope.Exec("INSERT INTO CompositeProducts (Id, TenantId, Name, Sku) VALUES (4, 10, 'Widget 4', 'SKU-004')");

        var rows = scope.QueryAll("SELECT Id, TenantId, Sku FROM CompositeProducts ORDER BY Id");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void ApplyPlannedChanges_multi_column_unique_index_enforces_uniqueness()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<MultiColumnIndexContext>();

        var plan = context.Migrations.PlanModelChanges();

        var createIndex = Assert.Single(plan.Operations.OfType<CreateIndexOperation>());
        Assert.Equal(2, createIndex.Index.ColumnNames.Count);
        Assert.Equal(new[] { "Category", "Code" }, createIndex.Index.ColumnNames);

        context.Migrations.ApplyPlannedChanges("Mig_MultiColumnIndex_V1");

        scope.Exec("INSERT INTO IndexedItems (Id, Category, Code) VALUES (1, 'A', '001')");
        Assert.ThrowsAny<Exception>(() =>
            scope.Exec("INSERT INTO IndexedItems (Id, Category, Code) VALUES (2, 'A', '001')"));

        scope.Exec("INSERT INTO IndexedItems (Id, Category, Code) VALUES (3, 'A', '002')");
        scope.Exec("INSERT INTO IndexedItems (Id, Category, Code) VALUES (4, 'B', '001')");

        var rows = scope.QueryAll("SELECT Id, Category, Code FROM IndexedItems ORDER BY Id");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void ApplyPlannedChanges_records_migration_id_in_history()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        const string migrationId = "20260304_V1_Products";
        context.Migrations.ApplyPlannedChanges(migrationId);

        var history = context.Migrations.GetHistory();
        Assert.Contains(history, entry =>
            string.Equals(entry.MigrationId, migrationId, StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyPlannedChanges_second_apply_is_noop_and_history_has_one_entry()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        const string migrationId = "20260304_V1_Idempotent";
        var r1 = context.Migrations.ApplyPlannedChanges(migrationId);
        var r2 = context.Migrations.ApplyPlannedChanges(migrationId + "_2");

        Assert.True(r1.AppliedOperations > 0);
        Assert.Equal(0, r2.AppliedOperations);

        var history = context.Migrations.GetHistory();
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void GetHistory_returns_entries_in_chronological_order()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        context.Migrations.ApplyPlannedChanges("20260304_First");
        context.Migrations.ApplyPlan("20260304_Second",
            new WalhallaSqlMigrationPlan(new[]
            {
                new AddColumnOperation("Products",
                    Col("Description", SqlScalarType.String, nullable: true),
                    DefaultValueLiteral: null)
            }));

        var history = context.Migrations.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.True(history[0].AppliedAtUtc <= history[1].AppliedAtUtc);
        Assert.Equal("20260304_First", history[0].MigrationId);
        Assert.Equal("20260304_Second", history[1].MigrationId);
    }

    [Fact]
    public void ApplyPlan_explicit_executes_specified_operations_only()
    {
        using var scope = MigrationScope.Create();
        using var context = scope.CreateContext<V1ProductContext>();

        context.Migrations.ApplyPlannedChanges("20260304_V1");

        var customPlan = new WalhallaSqlMigrationPlan(new MigrationOperation[]
        {
            new CreateTableOperation(new SqlTableDefinition(
                "Tags",
                new[]
                {
                    Col("Id", SqlScalarType.Int32, pk: true),
                    Col("Label", SqlScalarType.String)
                },
                Array.Empty<SqlIndexDefinition>()))
        });

        var result = context.Migrations.ApplyPlan("20260304_AddTags", customPlan);
        Assert.Equal(1, result.AppliedOperations);

        scope.Exec("INSERT INTO Tags (Id, Label) VALUES (1, 'sale')");
        var row = scope.QueryFirst("SELECT Id, Label FROM Tags WHERE Id = 1");
        Assert.Equal("sale", row["Label"]?.ToString());
    }

    [Fact]
    public void ApplyPlan_explicit_add_existing_column_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_AddExistingColumn_Guardrail",
                new AddColumnOperation("Products",
                    Col("Name", SqlScalarType.String, nullable: true),
                    DefaultValueLiteral: null)));

        Assert.Contains("Column 'Name' already exists in collection 'Products'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_add_column_missing_target_rejects()
    {
        using var scope = MigrationScope.Create();

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_AddColumnMissingTable_Guardrail",
                new AddColumnOperation("GhostProducts",
                    new SqlColumnDefinition("Price", SqlScalarType.Double, IsNullable: true),
                    DefaultValueLiteral: null)));

        Assert.Contains("Table 'GhostProducts' not found.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_rename_table_to_existing_target_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");
        scope.Exec("CREATE TABLE CatalogProducts (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_RenameTableToExistingTarget_Guardrail",
                new RenameTableOperation("Products", "CatalogProducts")));

        Assert.Contains("Table 'CatalogProducts' already exists.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_rename_missing_column_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_RenameMissingColumn_Guardrail",
                new RenameColumnOperation("Products", "DisplayName", "FullName")));

        Assert.Contains("Column 'DisplayName' not found in collection 'Products'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_rename_column_to_existing_target_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, DisplayName VARCHAR(200))");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_RenameToExistingColumn_Guardrail",
                new RenameColumnOperation("Products", "Name", "DisplayName")));

        Assert.Contains("Column 'DisplayName' already exists in collection 'Products'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_add_foreign_key_missing_referenced_table_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_AddForeignKeyMissingTable_Guardrail",
                new AddForeignKeyOperation("Products",
                    new SqlForeignKeyDefinition(
                        "FK_Products_CategoryId",
                        "CategoryId",
                        "Categories",
                        "Id",
                        SqlForeignKeyAction.Restrict,
                        SqlForeignKeyAction.Restrict))));

        Assert.Contains("Foreign key 'FK_Products_CategoryId' references unknown table 'Categories'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_add_foreign_key_rejects_duplicate_constraint()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Categories (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");

        var operation = new AddForeignKeyOperation("Products",
            new SqlForeignKeyDefinition(
                "FK_Products_CategoryId",
                "CategoryId",
                "Categories",
                "Id",
                SqlForeignKeyAction.Restrict,
                SqlForeignKeyAction.Restrict));

        scope.ApplyExplicit("20260324_Embedded_AddForeignKey_Duplicate_Source", operation);

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_AddForeignKey_Duplicate_Guardrail", operation));

        Assert.Contains("Constraint 'FK_Products_CategoryId' already exists in collection 'Products'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_drop_missing_column_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_DropMissingColumn_Guardrail",
                new DropColumnOperation("Products", "DisplayName")));

        Assert.Contains("Column 'DisplayName' not found.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_drop_missing_index_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Users (Id INT PRIMARY KEY, Email VARCHAR(200) NOT NULL)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_DropMissingIndex_Guardrail",
                new DropIndexOperation("Users", "IX_Users_Missing")));

        Assert.Contains("Index 'IX_Users_Missing' does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_drop_missing_foreign_key_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_DropMissingForeignKey_Guardrail",
                new DropForeignKeyOperation("Products", "FK_Products_Missing")));

        Assert.Contains("Constraint 'FK_Products_Missing' not found in collection 'Products'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_drop_table_missing_target_rejects()
    {
        using var scope = MigrationScope.Create();

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_DropMissingTable_Guardrail",
                new DropTableOperation("GhostTable")));

        Assert.Contains("Table 'GhostTable' does not exist.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_drop_table_rejects_referenced_table()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Categories (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");

        scope.ApplyExplicit("20260324_Embedded_AddForeignKey_ForDropGuardrail",
            new AddForeignKeyOperation("Products",
                new SqlForeignKeyDefinition(
                    "FK_Products_CategoryId",
                    "CategoryId",
                    "Categories",
                    "Id",
                    SqlForeignKeyAction.Restrict,
                    SqlForeignKeyAction.Restrict)));

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_DropTable_Guardrail",
                new DropTableOperation("Categories")));

        Assert.Contains("Cannot DROP TABLE 'Categories'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Products.FK_Products_CategoryId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyPlan_explicit_add_foreign_key_missing_referenced_column_rejects()
    {
        using var scope = MigrationScope.Create();

        scope.Exec("CREATE TABLE Categories (Id INT PRIMARY KEY, Name VARCHAR(100) NOT NULL)");
        scope.Exec("CREATE TABLE Products (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL, CategoryId INT)");

        var ex = Assert.ThrowsAny<Exception>(() =>
            scope.ApplyExplicit("20260324_Embedded_AddForeignKeyMissingColumn_Guardrail",
                new AddForeignKeyOperation("Products",
                    new SqlForeignKeyDefinition(
                        "FK_Products_CategoryId",
                        "CategoryId",
                        "Categories",
                        "ExternalId",
                        SqlForeignKeyAction.Restrict,
                        SqlForeignKeyAction.Restrict))));

        Assert.Contains("Foreign key 'FK_Products_CategoryId' references unknown column 'Categories.ExternalId'", ex.Message, StringComparison.Ordinal);
    }

    private static SqlColumnDefinition Col(
        string name,
        SqlScalarType type,
        bool pk = false,
        bool nullable = false)
        => new(name, type, IsNullable: nullable, IsPrimaryKey: pk);

    private sealed class MigrationScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private MigrationScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public WalhallaEngine Database => _database;

        public static MigrationScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "MigrationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);
            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            return new MigrationScope(dbPath, engine, database);
        }

        public TContext CreateContext<TContext>() where TContext : WalhallaSqlEfCoreContext
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(_database))
                .Options;

            return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        }

        public void Exec(string sql)
        {
            using var conn = new WalhallaSqlDbConnection(_database);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public IReadOnlyDictionary<string, object?> QueryFirst(string sql)
        {
            return QueryAll(sql).First();
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryAll(string sql)
        {
            using var conn = new WalhallaSqlDbConnection(_database);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var results = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }

            return results;
        }

        public void ApplyExplicit(string migrationId, params MigrationOperation[] operations)
        {
            using var context = CreateContext<EmptyModelContext>();
            context.Migrations.ApplyPlan(migrationId,
                new WalhallaSqlMigrationPlan(operations));
        }

        public void Dispose()
        {
            _engine.Dispose();
            try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); }
            catch { }
        }
    }
}

public sealed class V1ProductContext : WalhallaSqlEfCoreContext
{
    public V1ProductContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductV1>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
        });
    }
}

public sealed class ProductV1
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class NullableProductNameContext : WalhallaSqlEfCoreContext
{
    public NullableProductNameContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NullableProductNameEntity>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired(false);
        });
    }
}

public sealed class NullableProductNameEntity
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public sealed class OptionalSharedTableDiscriminatorContext : WalhallaSqlEfCoreContext
{
    public OptionalSharedTableDiscriminatorContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SharedTransport>(entity =>
        {
            entity.ToTable("SharedTransports");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.HasOne(x => x.Operator)
                .WithOne()
                .HasForeignKey<SharedOperator>(x => x.Id)
                .IsRequired(false);
        });

        modelBuilder.Entity<SharedOperator>(entity =>
        {
            entity.ToTable("SharedTransports");
            entity.Property(x => x.DisplayName).IsRequired();
            entity.HasDiscriminator<string>("Operator_Discriminator")
                .HasValue<SharedOperator>("base")
                .HasValue<LicensedSharedOperator>("licensed");
        });

        modelBuilder.Entity<LicensedSharedOperator>();
    }
}

public sealed class SharedTransport
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SharedOperator? Operator { get; set; }
}

public class SharedOperator
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class LicensedSharedOperator : SharedOperator
{
    public string LicenseCode { get; set; } = string.Empty;
}

public sealed class RenamedProductTableContext : WalhallaSqlEfCoreContext
{
    public RenamedProductTableContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RenamedProductTableEntity>(entity =>
        {
            entity.ToTable("CatalogProducts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
        });
    }
}

public sealed class RenamedProductTableEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LegacyNamedProductContext : WalhallaSqlEfCoreContext
{
    public LegacyNamedProductContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegacyNamedProduct>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).IsRequired();
        });
    }
}

public sealed class LegacyNamedProduct
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class RenamedProductColumnContext : WalhallaSqlEfCoreContext
{
    public RenamedProductColumnContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RenamedProductColumnEntity>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
        });
    }
}

public sealed class RenamedProductColumnEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class V2ProductContext : WalhallaSqlEfCoreContext
{
    public V2ProductContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductV2>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Price);
        });
    }
}

public sealed class ProductV2
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double? Price { get; set; }
}

public sealed class ClientSetNullContext : WalhallaSqlEfCoreContext
{
    public ClientSetNullContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClientSetNullParent>(entity =>
        {
            entity.ToTable("Parents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
        });

        modelBuilder.Entity<ClientSetNullChild>(entity =>
        {
            entity.ToTable("Children");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.ParentId).IsRequired(false);
            entity.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });
    }
}

public sealed class ClientSetNullParent
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ClientSetNullChild> Children { get; set; } = new();
}

public sealed class ClientSetNullChild
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public ClientSetNullParent? Parent { get; set; }
}

public sealed class SeededCatalogContext : WalhallaSqlEfCoreContext
{
    public SeededCatalogContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeedCategory>(entity =>
        {
            entity.ToTable("SeedCategories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.HasData(
                new SeedCategory { Id = 10, Name = "Hardware" },
                new SeedCategory { Id = 20, Name = "Software" });
        });

        modelBuilder.Entity<SeedProduct>(entity =>
        {
            entity.ToTable("SeedProducts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.HasOne(x => x.Category)
                .WithMany(x => x.Products)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasData(
                new SeedProduct { Id = 100, Name = "Hammer", CategoryId = 10 },
                new SeedProduct { Id = 200, Name = "IDE", CategoryId = 20 });
        });
    }
}

public sealed class SeedCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SeedProduct> Products { get; set; } = new();
}

public sealed class SeedProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public SeedCategory? Category { get; set; }
}

public sealed class EmptyModelContext : WalhallaSqlEfCoreContext
{
    public EmptyModelContext(DbContextOptions options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder) { }
}

public sealed class AlternateKeyProductContext : WalhallaSqlEfCoreContext
{
    public AlternateKeyProductContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlternateKeyProduct>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Sku).IsRequired();
            entity.HasAlternateKey(x => x.Sku);
        });
    }
}

public sealed class AlternateKeyProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
}

public sealed class CompositeAlternateKeyProductContext : WalhallaSqlEfCoreContext
{
    public CompositeAlternateKeyProductContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CompositeAlternateKeyProduct>(entity =>
        {
            entity.ToTable("CompositeProducts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).IsRequired();
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Sku).IsRequired();
            entity.HasAlternateKey(x => new { x.TenantId, x.Sku });
        });
    }
}

public sealed class CompositeAlternateKeyProduct
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
}

public sealed class MultiColumnIndexContext : WalhallaSqlEfCoreContext
{
    public MultiColumnIndexContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MultiColumnIndexedItem>(entity =>
        {
            entity.ToTable("IndexedItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Category).IsRequired();
            entity.Property(x => x.Code).IsRequired();
            entity.HasIndex(x => new { x.Category, x.Code }).IsUnique();
        });
    }
}

public sealed class MultiColumnIndexedItem
{
    public int Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
