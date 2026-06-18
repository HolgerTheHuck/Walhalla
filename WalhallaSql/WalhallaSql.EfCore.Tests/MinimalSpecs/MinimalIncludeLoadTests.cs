using System.Collections.Generic;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für den EF-Core-Spec-Bereich "Load":
/// Include 1:n, Include n:1, ThenInclude und AsNoTracking.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalIncludeLoadTests
{
    [Fact]
    public void Include_1_to_n_loads_related_collection()
    {
        using var scope = CreateSeededScope();

        var blog = scope.Context.Blogs
            .Include(b => b.Posts)
            .Single(b => b.Id == 1);

        Assert.NotNull(blog.Posts);
        Assert.Equal(2, blog.Posts.Count);
        Assert.Contains(blog.Posts, p => p.Title == "First Post");
    }

    [Fact]
    public void Include_n_to_1_loads_related_reference()
    {
        using var scope = CreateSeededScope();

        var post = scope.Context.Posts
            .Include(p => p.Blog)
            .Single(p => p.Id == 1);

        Assert.NotNull(post.Blog);
        Assert.Equal("Ada Lovelace", post.Blog!.Name);
    }

    [Fact]
    public void ThenInclude_loads_second_level_collection()
    {
        using var scope = CreateSeededScope();

        var blog = scope.Context.Blogs
            .Include(b => b.Posts)
            .ThenInclude(p => p!.Tags)
            .Single(b => b.Id == 1);

        var firstPost = blog.Posts.Single(p => p.Id == 1);
        Assert.NotEmpty(firstPost.Tags);
        Assert.Contains(firstPost.Tags, t => t.Name == "ef-core");
    }

    [Fact]
    public void AsNoTracking_does_not_track_entities()
    {
        using var scope = CreateSeededScope();

        var blog = scope.Context.Blogs
            .AsNoTracking()
            .Single(b => b.Id == 1);

        Assert.Equal(EntityState.Detached, scope.Context.Entry(blog).State);
    }

    [Fact]
    public void Filtered_include_returns_only_matching_children()
    {
        using var scope = CreateSeededScope();

        var blog = scope.Context.Blogs
            .Include(b => b.Posts.Where(p => p.AmountCents >= 100000))
            .Single(b => b.Id == 1);

        Assert.Single(blog.Posts);
        Assert.Equal("First Post", blog.Posts[0].Title);
        Assert.Equal(129900, blog.Posts[0].AmountCents);
    }

    private static MinimalSpecScope<IncludeContext> CreateSeededScope()
        => MinimalSpecScope<IncludeContext>.Create(
            "20260616_MinimalIncludeLoad",
            options => new IncludeContext(options),
            seed: ctx =>
            {
                var tag1 = new Tag { Id = 1, Name = "ef-core" };
                var tag2 = new Tag { Id = 2, Name = "database" };

                var blog = new Blog
                {
                    Id = 1,
                    Name = "Ada Lovelace",
                    Posts = new List<Post>
                    {
                        new()
                        {
                            Id = 1,
                            Title = "First Post",
                            AmountCents = 129900,
                            Tags = new List<Tag> { tag1, tag2 }
                        },
                        new()
                        {
                            Id = 2,
                            Title = "Second Post",
                            AmountCents = 45900,
                            Tags = new List<Tag>()
                        }
                    }
                };

                ctx.Blogs.Add(blog);
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class Blog
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Post> Posts { get; set; } = new();
    }

    public sealed class Post
    {
        public int Id { get; set; }
        public int BlogId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int AmountCents { get; set; }
        public Blog? Blog { get; set; }
        public List<Tag> Tags { get; set; } = new();
    }

    public sealed class Tag
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class IncludeContext : WalhallaSqlEfCoreContext
    {
        public IncludeContext(DbContextOptions options) : base(options) { }

        public DbSet<Blog> Blogs => Set<Blog>();
        public DbSet<Post> Posts => Set<Post>();
        public DbSet<Tag> Tags => Set<Tag>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
                entity.HasMany(x => x.Posts)
                    .WithOne(x => x.Blog)
                    .HasForeignKey(x => x.BlogId);
            });

            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Title).IsRequired();
                entity.HasMany(x => x.Tags)
                    .WithMany();
            });

            modelBuilder.Entity<Tag>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
