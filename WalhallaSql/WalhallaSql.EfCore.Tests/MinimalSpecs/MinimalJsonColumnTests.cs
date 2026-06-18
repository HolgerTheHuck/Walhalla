using System;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "JsonTypes":
/// Owned-Entity als JSON-Spalte (ohne Spatial/NetTopologySuite).
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalJsonColumnTests
{
    [Fact]
    public void Owned_entity_as_json_roundtrips_scalar_values()
    {
        using var scope = CreateScope();

        scope.Context.JsonOwners.Add(new JsonOwner
        {
            Id = 1,
            Profile = new UserProfile { Name = "Ada", Age = 30 }
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.JsonOwners.Single();

        Assert.NotNull(found.Profile);
        Assert.Equal("Ada", found.Profile!.Name);
        Assert.Equal(30, found.Profile.Age);
    }

    [Fact]
    public void Owned_entity_as_json_can_be_updated()
    {
        using var scope = CreateScope();

        scope.Context.JsonOwners.Add(new JsonOwner
        {
            Id = 2,
            Profile = new UserProfile { Name = "Alan", Age = 41 }
        });
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var found = scope.Context.JsonOwners.Single(x => x.Id == 2);
        found.Profile.Age = 42;
        scope.Context.SaveChanges();
        scope.Context.ChangeTracker.Clear();

        var reloaded = scope.Context.JsonOwners.Single(x => x.Id == 2);
        Assert.Equal(42, reloaded.Profile.Age);
    }

    private static MinimalSpecScope<JsonColumnContext> CreateScope()
        => MinimalSpecScope<JsonColumnContext>.Create(
            "20260615_MinimalJsonColumns",
            options => new JsonColumnContext(options));

    public sealed class JsonOwner
    {
        public int Id { get; set; }
        public UserProfile Profile { get; set; } = new();
    }

    public sealed class UserProfile
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public sealed class JsonColumnContext : WalhallaSqlEfCoreContext
    {
        public JsonColumnContext(DbContextOptions options) : base(options) { }

        public DbSet<JsonOwner> JsonOwners => Set<JsonOwner>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<JsonOwner>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.OwnsOne(x => x.Profile, profile =>
                {
                    profile.ToJson();
                    profile.Property(p => p.Name).IsRequired();
                    profile.Property(p => p.Age);
                });
            });
        }
    }
}
