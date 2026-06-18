using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests;

public sealed class EfCoreInMemoryConnectionStringTests
{
    private sealed class WidgetContext : DbContext
    {
        public WidgetContext(DbContextOptions<WidgetContext> options) : base(options) { }

        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(b =>
            {
                b.ToTable("Widgets");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id);
                b.Property(x => x.Name).IsRequired();
            });
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static DbContextOptions<WidgetContext> Options(WalhallaSqlDbConnection connection)
        => new DbContextOptionsBuilder<WidgetContext>()
            .UseWalhallaSql(connection)
            .Options;

    [Fact]
    public void Ef_context_with_data_source_memory_roundtrips()
    {
        using var connection = new WalhallaSqlDbConnection("Data Source=:memory:;Database=App");
        connection.Open();

        using var ctx = new WidgetContext(Options(connection));
        ctx.Database.EnsureCreated();

        ctx.Widgets.Add(new Widget { Id = 1, Name = "Alpha" });
        ctx.SaveChanges();

        var loaded = ctx.Widgets.Single(w => w.Id == 1);
        Assert.Equal("Alpha", loaded.Name);
    }

    [Fact]
    public void Ef_context_with_embedded_path_memory_roundtrips()
    {
        using var connection = new WalhallaSqlDbConnection("EmbeddedPath=:memory:;Database=App");
        connection.Open();

        using var ctx = new WidgetContext(Options(connection));
        ctx.Database.EnsureCreated();

        ctx.Widgets.Add(new Widget { Id = 7, Name = "Beta" });
        ctx.SaveChanges();

        var loaded = ctx.Widgets.Single(w => w.Id == 7);
        Assert.Equal("Beta", loaded.Name);
    }

    [Fact]
    public void Ef_in_memory_connections_are_isolated()
    {
        using var firstConnection = new WalhallaSqlDbConnection("Data Source=:memory:;Database=App");
        firstConnection.Open();
        using (var first = new WidgetContext(Options(firstConnection)))
        {
            first.Database.EnsureCreated();
            first.Widgets.Add(new Widget { Id = 1, Name = "Only-in-first" });
            first.SaveChanges();
        }

        using var secondConnection = new WalhallaSqlDbConnection("Data Source=:memory:;Database=App");
        secondConnection.Open();
        using var second = new WidgetContext(Options(secondConnection));
        second.Database.EnsureCreated();
        Assert.Empty(second.Widgets.ToList());
    }

    // A1.4 — Shared-Mode tests

    [Fact]
    public void Shared_mode_connections_with_same_name_share_database()
    {
        const string cs = "Data Source=:memory:;Mode=Shared;Name=A14SharedTest;Database=App";

        using var firstConnection = new WalhallaSqlDbConnection(cs);
        firstConnection.Open();

        using var secondConnection = new WalhallaSqlDbConnection(cs);
        secondConnection.Open();

        using (var first = new WidgetContext(Options(firstConnection)))
        {
            first.Database.EnsureCreated();
            first.Widgets.Add(new Widget { Id = 42, Name = "SharedWidget" });
            first.SaveChanges();
        }

        using var second = new WidgetContext(Options(secondConnection));
        var loaded = second.Widgets.Single(w => w.Id == 42);
        Assert.Equal("SharedWidget", loaded.Name);
    }

    [Fact]
    public void Shared_mode_connections_with_different_names_are_isolated()
    {
        const string csA = "Data Source=:memory:;Mode=Shared;Name=A14IsoA;Database=App";
        const string csB = "Data Source=:memory:;Mode=Shared;Name=A14IsoB;Database=App";

        using var connA = new WalhallaSqlDbConnection(csA);
        connA.Open();
        using var connB = new WalhallaSqlDbConnection(csB);
        connB.Open();

        using (var ctxA = new WidgetContext(Options(connA)))
        {
            ctxA.Database.EnsureCreated();
            ctxA.Widgets.Add(new Widget { Id = 1, Name = "OnlyInA" });
            ctxA.SaveChanges();
        }

        using var ctxB = new WidgetContext(Options(connB));
        ctxB.Database.EnsureCreated();
        Assert.Empty(ctxB.Widgets.ToList());
    }

    [Fact]
    public void Shared_mode_engine_is_released_when_last_connection_closes()
    {
        var name = $"A14LifetimeTest_{Guid.NewGuid():N}";
        var cs = $"Data Source=:memory:;Mode=Shared;Name={name};Database=App";

        // Phase 1: seed data, then close all connections
        using (var conn = new WalhallaSqlDbConnection(cs))
        {
            conn.Open();
            using var ctx = new WidgetContext(Options(conn));
            ctx.Database.EnsureCreated();
            ctx.Widgets.Add(new Widget { Id = 99, Name = "Ephemeral" });
            ctx.SaveChanges();
        } // all leases released ? engine disposed

        // Phase 2: same name ? brand-new engine, no data
        using var conn2 = new WalhallaSqlDbConnection(cs);
        conn2.Open();
        using var ctx2 = new WidgetContext(Options(conn2));
        ctx2.Database.EnsureCreated();
        Assert.Empty(ctx2.Widgets.ToList());
    }

    [Fact]
    public void Shared_mode_without_name_throws()
    {
        using var conn = new WalhallaSqlDbConnection("Data Source=:memory:;Mode=Shared;Database=App");
        Assert.Throws<InvalidOperationException>(() => conn.Open());
    }
}
