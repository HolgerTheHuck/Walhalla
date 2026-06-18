using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "DataAnnotations":
/// [Required], [MaxLength], [Key], [Timestamp] und [NotMapped].
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalDataAnnotationTests
{
    [Fact]
    public void Required_attribute_creates_non_nullable_column()
    {
        using var scope = CreateScope();

        scope.Context.AnnotatedItems.Add(new AnnotatedItem { Id = 1, RequiredName = "Ada" });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.AnnotatedItems.Single();
        Assert.Equal("Ada", found.RequiredName);
    }

    [Fact]
    public void MaxLength_attribute_does_not_truncate_short_value()
    {
        using var scope = CreateScope();

        scope.Context.AnnotatedItems.Add(new AnnotatedItem
        {
            Id = 1,
            RequiredName = "Ada",
            ShortCode = "AB"
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.AnnotatedItems.Single();
        Assert.Equal("AB", found.ShortCode);
    }

    [Fact]
    public void Key_attribute_identifies_primary_key()
    {
        using var scope = CreateScope();

        scope.Context.KeyedItems.Add(new KeyedItem { Code = "K1", Name = "First" });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.KeyedItems.Find("K1");
        Assert.NotNull(found);
        Assert.Equal("First", found!.Name);
    }

    [Fact]
    public void Timestamp_attribute_marks_concurrency_token()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(AnnotatedItem));
        Assert.NotNull(entityType);

        var timestampProperty = entityType!.FindProperty(nameof(AnnotatedItem.RowVersion));
        Assert.NotNull(timestampProperty);
        Assert.True(timestampProperty!.IsConcurrencyToken);
    }

    [Fact]
    public void NotMapped_attribute_excludes_property_from_model()
    {
        using var scope = CreateScope();

        var entityType = scope.Context.Model.FindEntityType(typeof(AnnotatedItem));
        Assert.NotNull(entityType);
        Assert.Null(entityType!.FindProperty(nameof(AnnotatedItem.Ignored)));
    }

    private static MinimalSpecScope<AnnotationContext> CreateScope()
        => MinimalSpecScope<AnnotationContext>.Create(
            "20260615_MinimalDataAnnotations",
            options => new AnnotationContext(options));

    public sealed class AnnotatedItem
    {
        public int Id { get; set; }

        [Required]
        public string RequiredName { get; set; } = string.Empty;

        [MaxLength(10)]
        public string? ShortCode { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; } = new byte[8];

        [NotMapped]
        public string Ignored { get; set; } = string.Empty;
    }

    public sealed class KeyedItem
    {
        [Key]
        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    public sealed class AnnotationContext : WalhallaSqlEfCoreContext
    {
        public AnnotationContext(DbContextOptions options) : base(options) { }

        public DbSet<AnnotatedItem> AnnotatedItems => Set<AnnotatedItem>();
        public DbSet<KeyedItem> KeyedItems => Set<KeyedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Attribute werden automatisch erkannt; explizite Konfiguration nur
            // für den Tabellennamen, um Kollisionen zu vermeiden.
            modelBuilder.Entity<AnnotatedItem>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.RequiredName).IsRequired();
            });

            modelBuilder.Entity<KeyedItem>(entity =>
            {
                entity.HasKey(x => x.Code);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
