using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class CompositeKeyLoadRegressionTests
{
    [Fact]
    public void Reference_load_many_to_one_composite_key_tracks_parent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Set<CompositeKeyChild>().Single(entity => entity.Id == 52);
        var reference = context.Entry(child).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(child.Parent);
        Assert.NotNull(child.Parent.ChildrenCompositeKey);
        Assert.Contains(child, child.Parent.ChildrenCompositeKey!);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_query_many_to_one_composite_key_tracks_parent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Set<CompositeKeyChild>().Single(entity => entity.Id == 52);

        var reference = context.Entry(child).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().Single();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(parent);
        Assert.Same(parent, child.Parent);
        Assert.NotNull(parent.ChildrenCompositeKey);
        Assert.Contains(child, parent.ChildrenCompositeKey!);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Collection_load_composite_key_tracks_children_and_fixes_back_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var collection = context.Entry(parent).Collection(entity => entity.ChildrenCompositeKey!);

        Assert.False(collection.IsLoaded);

        collection.Load();

        Assert.True(collection.IsLoaded);
        Assert.NotNull(parent.ChildrenCompositeKey);
        Assert.Equal(2, parent.ChildrenCompositeKey!.Count);
        Assert.All(parent.ChildrenCompositeKey, child => Assert.Same(parent, child.Parent));
        Assert.Equal(3, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Collection_query_composite_key_tracks_children_without_marking_collection_loaded()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var collection = context.Entry(parent).Collection(entity => entity.ChildrenCompositeKey!);

        Assert.False(collection.IsLoaded);

        var children = collection.Query().OrderBy(entity => entity.Id).ToList();

        Assert.False(collection.IsLoaded);
        Assert.Equal(2, children.Count);
        Assert.NotNull(parent.ChildrenCompositeKey);
        Assert.Equal(2, parent.ChildrenCompositeKey!.Count);
        Assert.All(children, child => Assert.Same(parent, child.Parent));
        Assert.All(children, child => Assert.Contains(child, parent.ChildrenCompositeKey));
        Assert.Equal(3, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Collection_load_composite_key_detached_populates_navigation_without_tracking()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var collection = context.Entry(parent).Collection(entity => entity.ChildrenCompositeKey!);
        context.Entry(parent).State = EntityState.Detached;

        Assert.False(collection.IsLoaded);

        collection.Load();

        Assert.True(collection.IsLoaded);
        Assert.NotNull(parent.ChildrenCompositeKey);
        Assert.Equal(2, parent.ChildrenCompositeKey!.Count);
        Assert.All(parent.ChildrenCompositeKey, child => Assert.Same(parent, child.Parent));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Collection_query_composite_key_detached_returns_children_without_fixup_or_tracking()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var collection = context.Entry(parent).Collection(entity => entity.ChildrenCompositeKey!);
        context.Entry(parent).State = EntityState.Detached;

        Assert.False(collection.IsLoaded);

        var children = collection.Query().OrderBy(entity => entity.Id).ToList();

        Assert.False(collection.IsLoaded);
        Assert.Equal(2, children.Count);
        Assert.Empty(parent.ChildrenCompositeKey ?? []);
        Assert.All(children, child => Assert.Null(child.Parent));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Reference_load_many_to_one_composite_key_deleted_state_tracks_parent_without_fixup()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Set<CompositeKeyChild>().Single(entity => entity.Id == 52);
        var reference = context.Entry(child).Reference(entity => entity.Parent);
        context.Entry(child).State = EntityState.Deleted;

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.Null(child.Parent);
        var parent = context.ChangeTracker.Entries<CompositeKeyParent>().Single().Entity;
        Assert.Null(parent.ChildrenCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_query_many_to_one_composite_key_deleted_state_returns_parent_without_marking_loaded()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Set<CompositeKeyChild>().Single(entity => entity.Id == 52);
        var reference = context.Entry(child).Reference(entity => entity.Parent);
        context.Entry(child).State = EntityState.Deleted;

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().Single();

        Assert.False(reference.IsLoaded);
        Assert.NotNull(parent);
        Assert.Null(child.Parent);
        Assert.Null(parent.ChildrenCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_load_one_to_one_composite_key_tracks_parent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Set<CompositeKeySingle>().Single(entity => entity.Id == 42);
        var reference = context.Entry(single).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(single.Parent);
        Assert.Same(single, single.Parent!.SingleCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_query_one_to_one_composite_key_tracks_parent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Set<CompositeKeySingle>().Single(entity => entity.Id == 42);

        var reference = context.Entry(single).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().Single();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(parent);
        Assert.Same(parent, single.Parent);
        Assert.Same(single, parent.SingleCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_load_one_to_one_dependent_composite_key_tracks_dependent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var reference = context.Entry(parent).Reference(entity => entity.SingleCompositeKey);

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(parent.SingleCompositeKey);
        Assert.Same(parent, parent.SingleCompositeKey!.Parent);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_query_one_to_one_dependent_composite_key_tracks_dependent_and_fixes_navigation()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var parent = context.Set<CompositeKeyParent>().Single(entity => entity.Id == 707);
        var reference = context.Entry(parent).Reference(entity => entity.SingleCompositeKey);

        Assert.False(reference.IsLoaded);

        var single = reference.Query().Single();

        Assert.True(reference.IsLoaded);
        Assert.NotNull(single);
        Assert.Same(single, parent.SingleCompositeKey);
        Assert.Same(parent, single.Parent);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_load_one_to_one_composite_key_deleted_state_tracks_parent_without_fixup()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Set<CompositeKeySingle>().Single(entity => entity.Id == 42);
        var reference = context.Entry(single).Reference(entity => entity.Parent);
        context.Entry(single).State = EntityState.Deleted;

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.Null(single.Parent);
        var parent = context.ChangeTracker.Entries<CompositeKeyParent>().Single().Entity;
        Assert.Null(parent.SingleCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_query_one_to_one_composite_key_deleted_state_returns_parent_without_marking_loaded()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Set<CompositeKeySingle>().Single(entity => entity.Id == 42);
        var reference = context.Entry(single).Reference(entity => entity.Parent);
        context.Entry(single).State = EntityState.Deleted;

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().Single();

        Assert.False(reference.IsLoaded);
        Assert.NotNull(parent);
        Assert.Null(single.Parent);
        Assert.Null(parent.SingleCompositeKey);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Reference_load_many_to_one_composite_key_with_partial_null_fk_marks_loaded_without_materializing_parent()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Attach(new CompositeKeyChild { Id = 767, ParentId = 707 }).Entity;
        var reference = context.Entry(child).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.Null(child.Parent);
        Assert.Single(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Reference_query_many_to_one_composite_key_with_partial_null_fk_returns_null_without_marking_loaded()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var child = context.Attach(new CompositeKeyChild { Id = 768, ParentAlternateId = "Root" }).Entity;
        var reference = context.Entry(child).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().SingleOrDefault();

        Assert.False(reference.IsLoaded);
        Assert.Null(parent);
        Assert.Null(child.Parent);
        Assert.Single(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Reference_load_one_to_one_composite_key_with_partial_null_fk_marks_loaded_without_materializing_parent()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Attach(new CompositeKeySingle { Id = 769, ParentId = 707 }).Entity;
        var reference = context.Entry(single).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        reference.Load();

        Assert.True(reference.IsLoaded);
        Assert.Null(single.Parent);
        Assert.Single(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Reference_query_one_to_one_composite_key_with_partial_null_fk_returns_null_without_marking_loaded()
    {
        using var scope = CompositeKeyLoadScope.Create();
        using var context = scope.CreateContext();

        var single = context.Attach(new CompositeKeySingle { Id = 770, ParentAlternateId = "Root" }).Entity;
        var reference = context.Entry(single).Reference(entity => entity.Parent);

        Assert.False(reference.IsLoaded);

        var parent = reference.Query().SingleOrDefault();

        Assert.False(reference.IsLoaded);
        Assert.Null(parent);
        Assert.Null(single.Parent);
        Assert.Single(context.ChangeTracker.Entries());
    }

    private sealed class CompositeKeyLoadScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private CompositeKeyLoadScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static CompositeKeyLoadScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "CompositeKeyLoadRegressionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;

            using var context = CreateContext(database);
            context.Database.EnsureCreated();

            context.Add(new CompositeKeyParent
            {
                Id = 707,
                AlternateId = "Root",
                ChildrenCompositeKey = new List<CompositeKeyChild>
                {
                    new() { Id = 52, ParentId = 707, ParentAlternateId = "Root" },
                    new() { Id = 53, ParentId = 707, ParentAlternateId = "Root" }
                },
                SingleCompositeKey = new CompositeKeySingle
                {
                    Id = 42,
                    ParentId = 707,
                    ParentAlternateId = "Root"
                }
            });
            context.SaveChanges();
            context.ChangeTracker.Clear();

            return new CompositeKeyLoadScope(dbPath, engine, database);
        }

        public CompositeKeyLoadContext CreateContext()
            => CreateContext(_database);

        private static CompositeKeyLoadContext CreateContext(WalhallaEngine database)
        {
            var options = new DbContextOptionsBuilder<CompositeKeyLoadContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
                .Options;

            return new CompositeKeyLoadContext(options);
        }

        public void Dispose()
        {
            _engine.Dispose();

            try
            {
                if (Directory.Exists(_dbPath))
                    Directory.Delete(_dbPath, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class CompositeKeyLoadContext(DbContextOptions<CompositeKeyLoadContext> options)
        : WalhallaSqlEfCoreContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeKeyParent>(entity =>
            {
                entity.ToTable("Parent");
                entity.HasKey(parent => parent.Id);
                entity.Property(parent => parent.AlternateId).IsRequired();
                entity.HasAlternateKey(parent => new { parent.AlternateId, parent.Id });
            });

            modelBuilder.Entity<CompositeKeyChild>(entity =>
            {
                entity.ToTable("ChildCompositeKey");
                entity.HasKey(child => child.Id);
                entity.HasOne(child => child.Parent)
                    .WithMany(parent => parent.ChildrenCompositeKey)
                    .HasForeignKey(child => new { child.ParentAlternateId, child.ParentId })
                    .HasPrincipalKey(parent => new { parent.AlternateId, parent.Id });
            });

            modelBuilder.Entity<CompositeKeySingle>(entity =>
            {
                entity.ToTable("SingleCompositeKey");
                entity.HasKey(single => single.Id);
                entity.HasOne(single => single.Parent)
                    .WithOne(parent => parent.SingleCompositeKey)
                    .HasForeignKey<CompositeKeySingle>(single => new { single.ParentAlternateId, single.ParentId })
                    .HasPrincipalKey<CompositeKeyParent>(parent => new { parent.AlternateId, parent.Id });
            });
        }
    }

    private sealed class CompositeKeyParent
    {
        public int Id { get; set; }
        public string AlternateId { get; set; } = string.Empty;
        public List<CompositeKeyChild>? ChildrenCompositeKey { get; set; }
        public CompositeKeySingle? SingleCompositeKey { get; set; }
    }

    private sealed class CompositeKeyChild
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public string? ParentAlternateId { get; set; }
        public CompositeKeyParent? Parent { get; set; }
    }

    private sealed class CompositeKeySingle
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public string? ParentAlternateId { get; set; }
        public CompositeKeyParent? Parent { get; set; }
    }
}
