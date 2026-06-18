using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für Bulk-Operationen:
/// ExecuteDelete und ExecuteUpdate (EF Core 7+).
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalBulkOperationsTests
{
    [Fact(Skip = "Known limitation: ExecuteDelete/ExecuteUpdate generate table aliases (e.g. 'Employees AS e') which the engine does not support in DML statements. See EF-CORE-LIMITS.md.")]
    public void ExecuteDelete_removes_matching_rows()
    {
        using var scope = CreateSeededScope();

        var deleted = scope.Context.Employees
            .Where(e => e.Name == "Ada Lovelace")
            .ExecuteDelete();

        Assert.Equal(1, deleted);

        scope.Context.ChangeTracker.Clear();
        var remaining = scope.Context.Employees.Count();
        Assert.Equal(2, remaining);
        Assert.False(scope.Context.Employees.Any(e => e.Name == "Ada Lovelace"));
    }

    [Fact(Skip = "Known limitation: ExecuteDelete/ExecuteUpdate generate table aliases which the engine does not support in DML. See EF-CORE-LIMITS.md.")]
    public void ExecuteUpdate_modifies_matching_rows()
    {
        using var scope = CreateSeededScope();

        var updated = scope.Context.Employees
            .Where(e => e.Age < 40)
            .ExecuteUpdate(s => s.SetProperty(e => e.Age, e => e.Age + 10));

        Assert.Equal(1, updated);

        scope.Context.ChangeTracker.Clear();
        var ada = scope.Context.Employees.Single(e => e.Name == "Ada Lovelace");
        Assert.Equal(40, ada.Age);
    }

    private static MinimalSpecScope<BulkContext> CreateSeededScope()
        => MinimalSpecScope<BulkContext>.Create(
            "20260616_MinimalBulkOperations",
            options => new BulkContext(options),
            seed: ctx =>
            {
                ctx.Employees.AddRange(
                    new Employee { Id = 1, Name = "Ada Lovelace", Age = 30 },
                    new Employee { Id = 2, Name = "Alan Turing", Age = 41 },
                    new Employee { Id = 3, Name = "Grace Hopper", Age = 45 });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class Employee
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public sealed class BulkContext : WalhallaSqlEfCoreContext
    {
        public BulkContext(DbContextOptions options) : base(options) { }

        public DbSet<Employee> Employees => Set<Employee>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Employee>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
                entity.Property(x => x.Age);
            });
        }
    }
}
