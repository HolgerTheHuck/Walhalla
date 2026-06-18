using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "ModelBuilding101":
/// Tabellenname, Spaltenname, Index und Shadow-Property.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalModelBuildingTests
{
    [Fact]
    public void ToTable_sets_table_name()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(ModelledItem));
        Assert.NotNull(entityType);
        Assert.Equal("modelled_items", entityType!.GetTableName());
    }

    [Fact]
    public void HasColumnName_sets_column_name()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(ModelledItem));
        Assert.NotNull(entityType);

        var displayNameProperty = entityType!.FindProperty(nameof(ModelledItem.DisplayName));
        Assert.NotNull(displayNameProperty);
        Assert.Equal("display_name", displayNameProperty!.GetColumnName());
    }

    [Fact]
    public void HasIndex_creates_index_on_column()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(ModelledItem));
        Assert.NotNull(entityType);

        var index = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1
                && i.Properties[0].Name == nameof(ModelledItem.DisplayName));

        Assert.NotNull(index);
    }

    [Fact]
    public void Shadow_property_is_part_of_model()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(ModelledItem));
        Assert.NotNull(entityType);

        var shadowProperty = entityType!.FindProperty("TenantId");
        Assert.NotNull(shadowProperty);
        Assert.Equal(typeof(int), shadowProperty!.ClrType);
    }

    [Fact]
    public void SaveChanges_honors_column_name_mapping()
    {
        using var scope = CreateScope();

        scope.Context.Items.Add(new ModelledItem { Id = 1, DisplayName = "Ada" });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var row = scope.Context.ExecuteSql("SELECT display_name FROM modelled_items WHERE id = 1").Rows!.Single();
        Assert.Equal("Ada", row["display_name"]);
    }

    [Fact]
    public void Where_on_column_mapped_property_uses_db_column_name()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Items
            .Where(e => e.DisplayName == "Ada")
            .ToList();

        Assert.Single(result);
        Assert.Equal("Ada", result[0].DisplayName);
    }

    [Fact]
    public void OrderBy_on_column_mapped_property_uses_db_column_name()
    {
        using var scope = CreateSeededScope();

        var names = scope.Context.Items
            .OrderBy(e => e.DisplayName)
            .Select(e => e.DisplayName)
            .ToList();

        Assert.Equal(new[] { "Ada", "Grace" }, names);
    }

    private static MinimalSpecScope<ModelBuildingContext> CreateScope()
        => MinimalSpecScope<ModelBuildingContext>.Create(
            "20260616_MinimalModelBuilding",
            options => new ModelBuildingContext(options));

    private static MinimalSpecScope<ModelBuildingContext> CreateSeededScope()
        => MinimalSpecScope<ModelBuildingContext>.Create(
            "20260616_MinimalModelBuilding",
            options => new ModelBuildingContext(options),
            seed: ctx =>
            {
                ctx.Items.AddRange(
                    new ModelledItem { Id = 10, DisplayName = "Grace" },
                    new ModelledItem { Id = 11, DisplayName = "Ada" });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class ModelledItem
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class ModelBuildingContext : WalhallaSqlEfCoreContext
    {
        public ModelBuildingContext(DbContextOptions options) : base(options) { }

        public DbSet<ModelledItem> Items => Set<ModelledItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ModelledItem>(entity =>
            {
                entity.ToTable("modelled_items");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasColumnName("id");
                entity.Property(x => x.DisplayName)
                    .IsRequired()
                    .HasColumnName("display_name");
                entity.HasIndex(x => x.DisplayName);
                entity.Property<int>("TenantId");
            });
        }
    }
}
