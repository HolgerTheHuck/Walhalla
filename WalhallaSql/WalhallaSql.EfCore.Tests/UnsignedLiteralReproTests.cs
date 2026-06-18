using System;
using System.Globalization;
using System.Linq;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Minimal repro for uint/ulong literal handling and EF SaveChanges round-trip.
/// </summary>
public sealed class UnsignedLiteralReproTests
{
    private sealed class TestContext : WalhallaSqlEfCoreContext
    {
        public TestContext(DbContextOptions options) : base(options) { }

        public DbSet<UnsignedEntity> UnsignedEntities => Set<UnsignedEntity>();
        public DbSet<UlongEntity> UlongEntities => Set<UlongEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UnsignedEntity>(entity =>
            {
                entity.ToTable("UnsignedEntity");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Sequence);
                entity.Property(x => x.Status).HasConversion<uint>();
            });

            modelBuilder.Entity<UlongEntity>(entity =>
            {
                entity.ToTable("UlongEntity");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Sequence);
                entity.Property(x => x.Status).HasConversion<ulong>();
            });
        }
    }

    private sealed class UnsignedEntity
    {
        public int Id { get; set; }
        public uint Sequence { get; set; }
        public UnsignedStatus Status { get; set; }
    }

    private sealed class UlongEntity
    {
        public int Id { get; set; }
        public ulong Sequence { get; set; }
        public UlongStatus Status { get; set; }
    }

    private enum UnsignedStatus : uint
    {
        Pending = 0U,
        Active = 1U,
        Archived = 4000000000U
    }

    private enum UlongStatus : ulong
    {
        Pending = 0UL,
        Active = 1UL,
        Archived = 18446744073709551610UL
    }

    private static TestContext CreateContext()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "UnsignedRepro", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        // MvccBPlusTree ist der aktive MVCC-Storage-Backend; mit dem Legacy-BPlusTree
        // bleiben SaveChanges-Schreiben für neue Sessions unsichtbar (CommitStore ist No-Op).
        var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
        var engine = new WalhallaEngine(engineOptions);
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;

        var context = new TestContext(options);
        context.Migrations.ApplyPlannedChanges("20260615_UnsignedRepro");
        return context;
    }

    [Fact]
    public void Raw_uint_literal_insert_and_update_roundtrips()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Seq BIGINT)");
        engine.Execute("INSERT INTO T (Id, Seq) VALUES (1, 4294967294)");

        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        var seqCol = tableDef.Columns.First(c => c.Name.Equals("Seq", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SqlScalarType.Int64, seqCol.Type);

        var result = engine.Execute("SELECT Seq FROM T WHERE Id = 1");
        var value = result.Rows!.Single()["Seq"];
        Assert.Equal(4294967294L, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EF_uint_sequence_update_roundtrips()
    {
        using var ctx = CreateContext();

        var entity = new UnsignedEntity
        {
            Id = 1,
            Sequence = 4000000000U,
            Status = UnsignedStatus.Archived
        };

        ctx.UnsignedEntities.Add(entity);
        ctx.SaveChanges();

        entity.Sequence = 4294967294U;
        entity.Status = UnsignedStatus.Active;

        var entry = ctx.Entry(entity);
        var seqEntry = entry.Property(nameof(UnsignedEntity.Sequence));
        Assert.True(seqEntry.IsModified, "Sequence should be marked as modified by EF.");

        var affected = ctx.SaveChanges();
        Assert.Equal(1, affected);

        ctx.ChangeTracker.Clear();

        var loaded = ctx.UnsignedEntities.Single(x => x.Id == 1);
        Assert.Equal(4294967294U, loaded.Sequence);
        Assert.Equal(UnsignedStatus.Active, loaded.Status);
    }

    [Fact]
    public void EF_ulong_sequence_update_roundtrips()
    {
        using var ctx = CreateContext();

        var entity = new UlongEntity
        {
            Id = 1,
            Sequence = 18446744073709551610UL,
            Status = UlongStatus.Archived
        };

        ctx.UlongEntities.Add(entity);
        ctx.SaveChanges();

        entity.Sequence = 18446744073709551614UL;
        entity.Status = UlongStatus.Active;
        var affected = ctx.SaveChanges();
        Assert.Equal(1, affected);

        ctx.ChangeTracker.Clear();

        var loaded = ctx.UlongEntities.Single(x => x.Id == 1);
        Assert.Equal(18446744073709551614UL, loaded.Sequence);
        Assert.Equal(UlongStatus.Active, loaded.Status);
    }
}
