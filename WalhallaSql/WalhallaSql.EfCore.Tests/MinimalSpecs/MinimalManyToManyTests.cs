using System.Collections.Generic;
using System.Linq;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für Many-to-Many-Beziehungen:
/// Include durch Join-Tabelle, Skip-Navigation.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalManyToManyTests
{
    [Fact]
    public void Many_to_many_include_loads_related_entities()
    {
        using var scope = CreateSeededScope();

        var student = scope.Context.Students
            .Include(s => s.Courses)
            .Single(s => s.Id == 1);

        Assert.Equal(2, student.Courses.Count);
        Assert.Contains(student.Courses, c => c.Title == "Math");
        Assert.Contains(student.Courses, c => c.Title == "Physics");
    }

    [Fact]
    public void Many_to_many_reverse_navigation_loads_students()
    {
        using var scope = CreateSeededScope();

        var course = scope.Context.Courses
            .Include(c => c.Students)
            .Single(c => c.Id == 10);

        Assert.Equal(2, course.Students.Count);
        Assert.Contains(course.Students, s => s.Name == "Ada");
        Assert.Contains(course.Students, s => s.Name == "Alan");
    }

    private static MinimalSpecScope<MtmContext> CreateSeededScope()
        => MinimalSpecScope<MtmContext>.Create(
            "20260616_MinimalManyToMany",
            options => new MtmContext(options),
            seed: ctx =>
            {
                var math = new Course { Id = 10, Title = "Math" };
                var physics = new Course { Id = 11, Title = "Physics" };
                var history = new Course { Id = 12, Title = "History" };

                ctx.Students.AddRange(
                    new Student
                    {
                        Id = 1,
                        Name = "Ada",
                        Courses = new List<Course> { math, physics }
                    },
                    new Student
                    {
                        Id = 2,
                        Name = "Alan",
                        Courses = new List<Course> { math, history }
                    });
                ctx.SaveChanges();
                ctx.ChangeTracker.Clear();
            });

    public sealed class Student
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Course> Courses { get; set; } = new();
    }

    public sealed class Course
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public List<Student> Students { get; set; } = new();
    }

    public sealed class MtmContext : WalhallaSqlEfCoreContext
    {
        public MtmContext(DbContextOptions options) : base(options) { }

        public DbSet<Student> Students => Set<Student>();
        public DbSet<Course> Courses => Set<Course>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Student>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
                entity.HasMany(x => x.Courses)
                    .WithMany(x => x.Students);
            });

            modelBuilder.Entity<Course>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Title).IsRequired();
            });
        }
    }
}
