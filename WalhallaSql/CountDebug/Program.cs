using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;

var storeName = "SharedTestStore";
var safeStoreName = storeName.Replace(" ", "_");
var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "Ef8SpecTests", safeStoreName, "shared");
if (Directory.Exists(dbPath))
    Directory.Delete(dbPath, recursive: true);
Directory.CreateDirectory(dbPath);

var engine = WalhallaEngine.Open(dbPath);
WalhallaSqlConnectionRegistry.Register("ef8spec-shared-" + safeStoreName.ToLowerInvariant(), () => engine);
WalhallaSqlConnectionRegistry.Register(storeName, () => engine);

var conn = new WalhallaSqlDbConnection($"DataSource=ef8spec-shared-{safeStoreName.ToLowerInvariant()};Database=App");
var options = new DbContextOptionsBuilder<UniverseContext>()
    .UseWalhallaSql(conn)
    .Options;

// First run: EnsureCreated applies seed data (2 rows)
using (var ctx1 = new UniverseContext(options))
{
    var created = ctx1.Database.EnsureCreated();
    Console.WriteLine($"First EnsureCreated: {created}");
    var count = System.Linq.Queryable.Count(ctx1.Set<Singularity>().AsNoTracking(), e => e.Id == 77);
    Console.WriteLine($"First Count(Id==77): {count}");
}

// Clean: EnsureDeleted then EnsureCreated (returns false)
using (var ctxClean = new UniverseContext(options))
{
    ctxClean.Database.EnsureDeleted();
    var created = ctxClean.Database.EnsureCreated();
    Console.WriteLine($"Clean EnsureCreated: {created}");
    var count = System.Linq.Queryable.Count(ctxClean.Set<Singularity>().AsNoTracking(), e => e.Id == 77);
    Console.WriteLine($"After Clean Count(Id==77): {count}");
}

// Second run: Add Id=35, SaveChanges, Count inside transaction
using (var ctx2 = new UniverseContext(options))
{
    ctx2.Add(new Singularity { Id = 35, Type = "Red Dwarf" });
    var saved = ctx2.SaveChanges();
    Console.WriteLine($"SaveChanges: {saved}");

    using var tx = ctx2.Database.BeginTransaction();
    var count = System.Linq.Queryable.Count(ctx2.Set<Singularity>().AsNoTracking(), e => e.Id == 35);
    Console.WriteLine($"Count(Id==35): {count}");
}

class UniverseContext : DbContext
{
    public UniverseContext(DbContextOptions<UniverseContext> options) : base(options) { }
    public DbSet<Singularity> Singularities => Set<Singularity>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Singularity>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<Singularity>().HasData(
            new Singularity { Id = 77, Type = "Black Hole" },
            new Singularity { Id = 88, Type = "Big Bang" });
    }
}

class Singularity
{
    public int Id { get; set; }
    public string? Type { get; set; }
}
