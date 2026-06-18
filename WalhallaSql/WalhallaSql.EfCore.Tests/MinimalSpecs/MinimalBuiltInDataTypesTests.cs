using System;
using System.IO;
using System.Linq;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "BuiltInDataTypes":
/// Roundtrip für bool, numerische Typen, Zeichenketten, Datum/Zeit,
/// Guid und Byte-Arrays.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalBuiltInDataTypesTests
{
    [Fact]
    public void Bool_roundtrips_true_and_false()
    {
        using var scope = CreateScope();

        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            Flag = true
        });
        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 2,
            Flag = false
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var rows = scope.Context.Samples.OrderBy(x => x.Id).ToList();

        Assert.True(rows[0].Flag);
        Assert.False(rows[1].Flag);
    }

    [Fact]
    public void Int_and_long_roundtrip()
    {
        using var scope = CreateScope();

        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            IntValue = int.MinValue,
            LongValue = long.MaxValue
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Samples.Single();

        Assert.Equal(int.MinValue, found.IntValue);
        Assert.Equal(long.MaxValue, found.LongValue);
    }

    [Fact]
    public void String_and_nullable_string_roundtrip()
    {
        using var scope = CreateScope();

        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            RequiredText = "required",
            OptionalText = null
        });
        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 2,
            RequiredText = "required2",
            OptionalText = "optional"
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var rows = scope.Context.Samples.OrderBy(x => x.Id).ToList();

        Assert.Equal("required", rows[0].RequiredText);
        Assert.Null(rows[0].OptionalText);
        Assert.Equal("optional", rows[1].OptionalText);
    }

    [Fact]
    public void DateTime_DateOnly_TimeOnly_roundtrip()
    {
        using var scope = CreateScope();

        var sample = new DataTypesSample
        {
            Id = 1,
            Stamp = new DateTime(2024, 7, 11, 13, 45, 30, DateTimeKind.Utc),
            BirthDate = new DateOnly(1815, 12, 10),
            PreferredTime = new TimeOnly(9, 30, 0)
        };

        scope.Context.Samples.Add(sample);
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Samples.Single();

        Assert.Equal(sample.Stamp, found.Stamp);
        Assert.Equal(sample.BirthDate, found.BirthDate);
        Assert.Equal(sample.PreferredTime, found.PreferredTime);
    }

    [Fact]
    public void Decimal_and_double_roundtrip()
    {
        using var scope = CreateScope();

        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            Amount = 123456.789m,
            Ratio = 0.123456789
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Samples.Single();

        Assert.Equal(123456.789m, found.Amount);
        Assert.Equal(0.123456789, found.Ratio, precision: 15);
    }

    [Fact]
    public void Guid_roundtrip()
    {
        using var scope = CreateScope();

        var id = Guid.NewGuid();
        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            Reference = id
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Samples.Single();

        Assert.Equal(id, found.Reference);
    }

    [Fact]
    public void Byte_array_roundtrip()
    {
        using var scope = CreateScope();

        var payload = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        scope.Context.Samples.Add(new DataTypesSample
        {
            Id = 1,
            Payload = payload
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.Samples.Single();

        Assert.NotNull(found.Payload);
        Assert.True(payload.SequenceEqual(found.Payload!));
    }

    // ─── uint/ulong ──────────────────────────────────────────────────────

    [Fact]
    public void Uint_property_insert_and_query_roundtrips()
    {
        using var scope = UnsignedScope.Create();

        scope.Context.UnsignedItems.Add(new UnsignedItem { Id = 1, Sequence = 4000000000U });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.UnsignedItems.Single(e => e.Id == 1);
        Assert.Equal(4000000000U, found.Sequence);
    }

    [Fact]
    public void Ulong_property_insert_and_query_roundtrips()
    {
        using var scope = UnsignedScope.Create();

        scope.Context.UnsignedItems.Add(new UnsignedItem { Id = 2, BigSequence = 18446744073709551610UL });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.UnsignedItems.Single(e => e.Id == 2);
        Assert.Equal(18446744073709551610UL, found.BigSequence);
    }

    private static MinimalSpecScope<DataTypesContext> CreateScope()
        => MinimalSpecScope<DataTypesContext>.Create(
            "20260616_MinimalBuiltInDataTypes",
            options => new DataTypesContext(options));

    public sealed class DataTypesSample
    {
        public int Id { get; set; }
        public bool Flag { get; set; }
        public int IntValue { get; set; }
        public long LongValue { get; set; }
        public string RequiredText { get; set; } = string.Empty;
        public string? OptionalText { get; set; }
        public DateTime Stamp { get; set; }
        public DateOnly BirthDate { get; set; }
        public TimeOnly PreferredTime { get; set; }
        public decimal Amount { get; set; }
        public double Ratio { get; set; }
        public Guid Reference { get; set; }
        public byte[]? Payload { get; set; }
    }

    public sealed class DataTypesContext : WalhallaSqlEfCoreContext
    {
        public DataTypesContext(DbContextOptions options) : base(options) { }

        public DbSet<DataTypesSample> Samples => Set<DataTypesSample>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DataTypesSample>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.RequiredText).IsRequired();
                entity.Property(x => x.OptionalText);
                entity.Property(x => x.Payload);
            });
        }
    }

    // ─── uint/ulong Entity & Context ─────────────────────────────────────

    public sealed class UnsignedItem
    {
        public int Id { get; set; }
        public uint Sequence { get; set; }
        public ulong BigSequence { get; set; }
    }

    public sealed class UnsignedContext : WalhallaSqlEfCoreContext
    {
        public UnsignedContext(DbContextOptions options) : base(options) { }

        public DbSet<UnsignedItem> UnsignedItems => Set<UnsignedItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UnsignedItem>(entity =>
            {
                entity.ToTable("UnsignedItems");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Sequence);
                entity.Property(x => x.BigSequence);
            });
        }
    }

    internal sealed class UnsignedScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;

        private UnsignedScope(string dbPath, WalhallaEngine engine, UnsignedContext context)
        {
            _dbPath = dbPath;
            _engine = engine;
            Context = context;
        }

        public UnsignedContext Context { get; }

        public static UnsignedScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "MinimalSpecs", "UnsignedTypes", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);

            var options = new DbContextOptionsBuilder<UnsignedContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
                .Options;

            var context = new UnsignedContext(options);
            context.Migrations.ApplyPlannedChanges("20260616_MinimalUnsignedTypes");

            return new UnsignedScope(dbPath, engine, context);
        }

        public void Dispose()
        {
            Context.Dispose();
            _engine.Dispose();
            try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
        }
    }
}
