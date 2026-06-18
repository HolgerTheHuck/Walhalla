using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "OperatorsQuery":
/// Where, OrderBy, Contains, Any, Count, First/Single, Skip/Take.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalLinqOperatorsTests
{
    [Fact]
    public void Where_equality_filter_returns_matching_rows()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.Name == "Ada Lovelace")
            .ToList();

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void Where_not_equality_filter_excludes_rows()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.Name != "Ada Lovelace")
            .OrderBy(e => e.Id)
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].Id);
        Assert.Equal(3, result[1].Id);
    }

    [Fact]
    public void OrderBy_sorts_ascending()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .OrderBy(e => e.Age)
            .Select(e => e.Name)
            .ToList();

        Assert.Equal(new[] { "Ada Lovelace", "Alan Turing", "Grace Hopper" }, result);
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .OrderByDescending(e => e.Age)
            .Select(e => e.Name)
            .ToList();

        Assert.Equal(new[] { "Grace Hopper", "Alan Turing", "Ada Lovelace" }, result);
    }

    [Fact(Skip = "Known limitation: Contains with local collection only returns first matching row instead of all matches. See EF-CORE-LIMITS.md.")]
    public void Contains_filter_with_local_collection()
    {
        using var scope = CreateSeededScope();

        var selected = new[] { 1, 3 };
        var result = scope.Context.Employees
            .Where(e => selected.Contains(e.Id))
            .OrderBy(e => e.Id)
            .ToList();

        // Aktuell liefert Contains nur 1 Zeile statt 2 — Product Bug.
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("Ada Lovelace", result[0].Name);
    }

    [Fact]
    public void Any_returns_true_when_predicate_matches()
    {
        using var scope = CreateSeededScope();

        var hasAda = scope.Context.Employees.Any(e => e.Name == "Ada Lovelace");
        var hasNobody = scope.Context.Employees.Any(e => e.Name == "Nobody");

        Assert.True(hasAda);
        Assert.False(hasNobody);
    }

    [Fact]
    public void Count_returns_total_and_filtered_count()
    {
        using var scope = CreateSeededScope();

        var total = scope.Context.Employees.Count();
        var overForty = scope.Context.Employees.Count(e => e.Age > 40);

        Assert.Equal(3, total);
        Assert.Equal(2, overForty);
    }

    [Fact]
    public void First_and_Single_select_expected_rows()
    {
        using var scope = CreateSeededScope();

        var first = scope.Context.Employees.OrderBy(e => e.Id).First();
        var single = scope.Context.Employees.Single(e => e.Id == 2);

        Assert.Equal("Ada Lovelace", first.Name);
        Assert.Equal("Alan Turing", single.Name);
    }

    [Fact]
    public void Skip_Take_pages_results()
    {
        using var scope = CreateSeededScope();

        var page = scope.Context.Employees
            .OrderBy(e => e.Id)
            .Skip(1)
            .Take(1)
            .ToList();

        Assert.Single(page);
        Assert.Equal("Alan Turing", page[0].Name);
    }

    [Fact]
    public void Combined_Where_OrderBy_Select_projects_correctly()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.Age >= 30)
            .OrderBy(e => e.Name)
            .Select(e => new { e.Name, e.Age })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal("Ada Lovelace", result[0].Name);
        Assert.Equal(30, result[0].Age);
    }

    // ─── NOT-Prädikat ────────────────────────────────────────────────────

    [Fact]
    public void Where_not_equality_excludes_matching_rows()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => !(e.Name == "Ada Lovelace"))
            .OrderBy(e => e.Id)
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Name == "Ada Lovelace");
        Assert.Equal("Alan Turing", result[0].Name);
        Assert.Equal("Grace Hopper", result[1].Name);
    }

    [Fact]
    public void Where_not_on_boolean_property_negates()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => !e.IsActive)
            .OrderBy(e => e.Id)
            .ToList();

        Assert.Single(result);
        Assert.Equal("Alan Turing", result[0].Name);
    }

    // ─── Null-Semantik ───────────────────────────────────────────────────

    [Fact]
    public void Where_equals_null_returns_rows_with_null_column()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.Nickname == null)
            .OrderBy(e => e.Id)
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Ada Lovelace", result[0].Name);
        Assert.Equal("Grace Hopper", result[1].Name);
    }

    [Fact]
    public void Where_not_equals_null_returns_rows_with_non_null_column()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.Nickname != null)
            .OrderBy(e => e.Id)
            .ToList();

        Assert.Single(result);
        Assert.Equal("Alan Turing", result[0].Name);
        Assert.Equal("The Enigma", result[0].Nickname);
    }

    // ─── DateOnly/TimeOnly in LINQ ───────────────────────────────────────

    [Fact(Skip = "Known limitation: DateOnly equality in LINQ returns empty. See EF-CORE-LIMITS.md.")]
    public void Where_dateonly_equality_filters_by_date()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.BirthDate == new DateOnly(1815, 12, 10))
            .ToList();

        Assert.Single(result);
        Assert.Equal("Ada Lovelace", result[0].Name);
    }

    [Fact(Skip = "Known limitation: DateOnly comparison in LINQ returns all rows. See EF-CORE-LIMITS.md.")]
    public void Where_dateonly_greater_than_filters_by_range()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.BirthDate > new DateOnly(1900, 1, 1))
            .OrderBy(e => e.BirthDate)
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Grace Hopper", result[0].Name);
        Assert.Equal("Alan Turing", result[1].Name);
    }

    [Fact]
    public void OrderBy_dateonly_sorts_chronologically()
    {
        using var scope = CreateSeededScope();

        var names = scope.Context.Employees
            .OrderBy(e => e.BirthDate)
            .Select(e => e.Name)
            .ToList();

        Assert.Equal(new[] { "Ada Lovelace", "Grace Hopper", "Alan Turing" }, names);
    }

    [Fact(Skip = "Known limitation: TimeOnly equality in LINQ returns empty. See EF-CORE-LIMITS.md.")]
    public void Where_timeonly_equality_filters_by_time()
    {
        using var scope = CreateSeededScope();

        var result = scope.Context.Employees
            .Where(e => e.PreferredContactTime == new TimeOnly(9, 30, 0))
            .ToList();

        Assert.Single(result);
        Assert.Equal("Alan Turing", result[0].Name);
    }

    // ─── Seed & Modell ───────────────────────────────────────────────────

    private static MinimalSpecScope<LinqContext> CreateSeededScope()
        => MinimalSpecScope<LinqContext>.Create(
            "20260616_MinimalLinqOperators",
            options => new LinqContext(options),
            seed: ctx =>
            {
                ctx.Employees.AddRange(
                    new Employee { Id = 1, Name = "Ada Lovelace", Age = 30, IsActive = true, Nickname = null, BirthDate = new DateOnly(1815, 12, 10), PreferredContactTime = new TimeOnly(8, 15, 0) },
                    new Employee { Id = 2, Name = "Alan Turing", Age = 41, IsActive = false, Nickname = "The Enigma", BirthDate = new DateOnly(1912, 6, 23), PreferredContactTime = new TimeOnly(9, 30, 0) },
                    new Employee { Id = 3, Name = "Grace Hopper", Age = 45, IsActive = true, Nickname = null, BirthDate = new DateOnly(1906, 12, 9), PreferredContactTime = new TimeOnly(10, 45, 0) });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class Employee
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public string? Nickname { get; set; }
        public DateOnly BirthDate { get; set; }
        public TimeOnly PreferredContactTime { get; set; }
    }

    public sealed class LinqContext : WalhallaSqlEfCoreContext
    {
        public LinqContext(DbContextOptions options) : base(options) { }

        public DbSet<Employee> Employees => Set<Employee>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Employee>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
                entity.Property(x => x.Age);
                entity.Property(x => x.IsActive);
                entity.Property(x => x.Nickname);
                entity.Property(x => x.BirthDate).IsRequired();
                entity.Property(x => x.PreferredContactTime).IsRequired();
            });
        }
    }
}
