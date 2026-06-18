using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WalhallaSql.EfCore;

string dbPath = @"E:\Develop\WalhallaProject\WalhallaSql\test_intercept_debug.db";
if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath);

var options = new DbContextOptionsBuilder<BlogContext>()
    .UseWalhallaSql(dbPath)
    .Options;

using var ctx = new BlogContext(options);
ctx.Database.EnsureCreated();

// Seed two rows
ctx.Blogs.Add(new Blog { Id = 1, Title = "A" });
ctx.Blogs.Add(new Blog { Id = 2, Title = "B" });
ctx.SaveChanges();

// Add interceptor that suppresses SaveChanges
ctx.GetService<Microsoft.EntityFrameworkCore.Infrastructure.CoreEventDispatcher>()?.AddInterceptor(new SuppressInterceptor());

Console.WriteLine("Before SaveChanges (should be suppressed):");
var result = ctx.SaveChanges();
Console.WriteLine($"SaveChanges returned: {result}");

// Check count inside transaction
using var tx = ctx.Database.BeginTransaction();
var count = ctx.Blogs.Count();
Console.WriteLine($"Count inside transaction: {count}");
tx.Commit();

// Check count after transaction
var countAfter = ctx.Blogs.Count();
Console.WriteLine($"Count after transaction: {countAfter}");

public class BlogContext : DbContext
{
    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }
    public DbSet<Blog> Blogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Title).IsRequired();
        });
    }
}

public class Blog
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public class SuppressInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor
{
    public Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
    {
        Console.WriteLine("Interceptor: Suppressing SaveChanges");
        return result.WithResult(0);
    }

    public System.Threading.Tasks.ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        System.Threading.CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Interceptor: Suppressing SaveChangesAsync");
        return new System.Threading.Tasks.ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>(result.WithResult(0));
    }
}
