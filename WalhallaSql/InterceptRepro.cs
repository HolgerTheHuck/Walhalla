using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WalhallaSql;
using WalhallaSql.EfCore;

var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InterceptTest", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dbPath);
var engine = WalhallaEngine.Open(dbPath);

var interceptor = new SuppressInterceptor();
var options = new DbContextOptionsBuilder<TestCtx>()
    .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
    .AddInterceptors(interceptor)
    .Options;

using var ctx = new TestCtx(options);
ctx.Database.EnsureCreated();
ctx.Add(new Entity { Id = 1, Name = "A" });
var result = ctx.SaveChanges();
Console.WriteLine($"SaveChanges result: {result}");
Console.WriteLine($"Interceptor called: {interceptor.Called}");
Console.WriteLine($"Entities in DB: {ctx.Entities.Count()}");

class SuppressInterceptor : SaveChangesInterceptor
{
    public bool Called { get; set; }
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Called = true;
        return InterceptionResult<int>.SuppressWithResult(0);
    }
}

class TestCtx : DbContext
{
    public TestCtx(DbContextOptions<TestCtx> options) : base(options) { }
    public DbSet<Entity> Entities => Set<Entity>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entity>(e => { e.HasKey(x => x.Id); e.ToTable("Entities"); });
    }
}

class Entity { public int Id { get; set; } public string Name { get; set; } = ""; }
