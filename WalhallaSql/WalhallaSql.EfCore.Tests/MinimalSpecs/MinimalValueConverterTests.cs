using System;
using System.Globalization;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "CustomConverters":
/// Enum→int, Guid→string, DateTimeOffset→DateTime und Nullable-Werte.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalValueConverterTests
{
    [Fact]
    public void Enum_to_int_converter_roundtrips()
    {
        using var scope = CreateScope();

        scope.Context.Orders.Add(new Order { Id = 1, Name = "First", Status = OrderStatus.Active });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var raw = scope.Context.ExecuteSql("SELECT Status FROM Orders WHERE Id = 1").Rows!.Single();
        Assert.Equal((int)OrderStatus.Active, Convert.ToInt32(raw["Status"], CultureInfo.InvariantCulture));

        var found = scope.Context.Orders.Single();
        Assert.Equal(OrderStatus.Active, found.Status);
    }

    [Fact]
    public void Guid_to_string_converter_roundtrips()
    {
        using var scope = CreateScope();

        var externalId = Guid.NewGuid();
        scope.Context.ExternalItems.Add(new ExternalItem { Id = 1, ExternalId = externalId });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var raw = scope.Context.ExecuteSql("SELECT ExternalId FROM ExternalItems WHERE Id = 1").Rows!.Single();
        var rawString = raw["ExternalId"]?.ToString();
        Assert.True(Guid.TryParse(rawString, out var parsed));
        Assert.Equal(externalId, parsed);

        var found = scope.Context.ExternalItems.Single();
        Assert.Equal(externalId, found.ExternalId);
    }

    [Fact]
    public void DateTimeOffset_to_DateTime_converter_uses_utc_value()
    {
        using var scope = CreateScope();

        var occurredAt = new DateTimeOffset(2024, 7, 11, 13, 45, 30, TimeSpan.FromHours(2));
        scope.Context.Events.Add(new OffsetEvent { Id = 1, OccurredAt = occurredAt });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var raw = scope.Context.ExecuteSql("SELECT OccurredAtUtc FROM OffsetEvents WHERE Id = 1").Rows!.Single();
        var storedValue = Assert.IsType<DateTime>(raw["OccurredAtUtc"]);
        Assert.Equal(occurredAt.UtcDateTime, storedValue);

        var found = scope.Context.Events.Single();
        Assert.Equal(occurredAt.UtcDateTime, found.OccurredAt.UtcDateTime);
    }

    [Fact]
    public void Nullable_enum_converter_handles_null()
    {
        using var scope = CreateScope();

        scope.Context.OptionalOrders.Add(new OptionalOrder { Id = 1, Status = null });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.OptionalOrders.Single();
        Assert.Null(found.Status);

        var raw = scope.Context.ExecuteSql("SELECT Status FROM OptionalOrders WHERE Id = 1").Rows!.Single();
        Assert.Null(raw["Status"]);
    }

    private static MinimalSpecScope<ConverterContext> CreateScope()
        => MinimalSpecScope<ConverterContext>.Create(
            "20260615_MinimalValueConverters",
            options => new ConverterContext(options));

    public enum OrderStatus { Pending = 0, Active = 1, Closed = 2 }

    public enum OptionalOrderStatus { Pending = 0, Active = 1 }

    public sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public OrderStatus Status { get; set; }
    }

    public sealed class ExternalItem
    {
        public int Id { get; set; }
        public Guid ExternalId { get; set; }
    }

    public sealed class OffsetEvent
    {
        public int Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    public sealed class OptionalOrder
    {
        public int Id { get; set; }
        public OptionalOrderStatus? Status { get; set; }
    }

    public sealed class ConverterContext : WalhallaSqlEfCoreContext
    {
        public ConverterContext(DbContextOptions options) : base(options) { }

        public DbSet<Order> Orders => Set<Order>();
        public DbSet<ExternalItem> ExternalItems => Set<ExternalItem>();
        public DbSet<OffsetEvent> Events => Set<OffsetEvent>();
        public DbSet<OptionalOrder> OptionalOrders => Set<OptionalOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("Orders");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
                entity.Property(x => x.Status).HasConversion<int>();
            });

            modelBuilder.Entity<ExternalItem>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.ExternalId).HasConversion<string>();
            });

            modelBuilder.Entity<OffsetEvent>(entity =>
            {
                entity.ToTable("OffsetEvents");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.OccurredAt)
                    .HasColumnName("OccurredAtUtc")
                    .HasConversion(
                        value => value.UtcDateTime,
                        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
            });

            modelBuilder.Entity<OptionalOrder>(entity =>
            {
                entity.ToTable("OptionalOrders");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Status).HasConversion<int?>();
            });
        }
    }
}
