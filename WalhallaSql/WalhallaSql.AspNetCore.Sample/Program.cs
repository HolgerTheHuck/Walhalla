using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Use a stable temp path so the database survives across hot-reloads.
var dbPath = Path.Combine(Path.GetTempPath(), "WalhallaSql", "AspNetCoreSample");
Directory.CreateDirectory(dbPath);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var engine = WalhallaEngine.Open(dbPath);
    var layeredOptions = new WalhallaSqlEfCoreOptions(engine);
    options.UseWalhallaSql(layeredOptions);
});

var app = builder.Build();

// Ensure schema exists.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    if (!db.Todos.Any())
    {
        db.Todos.AddRange(
            new Todo { Id = 1, Title = "Learn WalhallaSql", IsDone = false },
            new Todo { Id = 2, Title = "Build awesome app", IsDone = true });
        db.SaveChanges();
    }
}

app.MapGet("/todos", async (AppDbContext db) =>
{
    var todos = await db.Todos.OrderBy(t => t.Id).ToListAsync();
    return Results.Ok(todos);
});

app.MapGet("/todos/{id}", async (AppDbContext db, int id) =>
{
    var todo = await db.Todos.FindAsync(id);
    return todo is null ? Results.NotFound() : Results.Ok(todo);
});

app.MapPost("/todos", async (AppDbContext db, Todo todo) =>
{
    db.Todos.Add(todo);
    await db.SaveChangesAsync();
    return Results.Created($"/todos/{todo.Id}", todo);
});

app.MapPut("/todos/{id}", async (AppDbContext db, int id, Todo updated) =>
{
    var todo = await db.Todos.FindAsync(id);
    if (todo is null) return Results.NotFound();

    todo.Title = updated.Title;
    todo.IsDone = updated.IsDone;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/todos/{id}", async (AppDbContext db, int id) =>
{
    var todo = await db.Todos.FindAsync(id);
    if (todo is null) return Results.NotFound();

    db.Todos.Remove(todo);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", engine = "WalhallaSql" }));

app.Run();

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Todo> Todos => Set<Todo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Todo>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Title).IsRequired();
        });
    }
}

public class Todo
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
}
