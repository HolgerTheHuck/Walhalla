using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class ManyToManyFilteredIncludeProjectionRegressionTests
{
    [Fact]
    public void Collection_query_with_filtered_include_and_projection_keeps_scalar_counts_non_null()
    {
        using var scope = ManyToManyScope.Create();
        using var context = scope.CreateContext();

        var root = context.Set<StudentNode>().Single(student => student.Id == 1);

        var results = context.Entry(root)
            .Collection(student => student.Courses)
            .Query()
            .Include(course => course.Tags.Where(tag => tag.Id < 300).OrderBy(tag => tag.Id))
            .Select(course => new
            {
                Course = course,
                StudentCount = course.Students.Count(),
                TagCount = course.Tags.Count()
            })
            .OrderBy(result => result.Course.Id)
            .ToList();

        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal(10, first.Course.Id);
        Assert.Equal(2, first.StudentCount);
        Assert.Equal(2, first.TagCount);
        var firstIncludedTags = first.Course.Tags.OrderBy(tag => tag.Id).ToList();
        Assert.Equal(2, firstIncludedTags.Count);
        Assert.Equal(100, firstIncludedTags[0].Id);
        Assert.Equal(200, firstIncludedTags[1].Id);

        var second = results[1];
        Assert.Equal(20, second.Course.Id);
        Assert.Equal(1, second.StudentCount);
        Assert.Equal(1, second.TagCount);
        Assert.Empty(second.Course.Tags);
    }

    private sealed class ManyToManyScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private ManyToManyScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static ManyToManyScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", nameof(ManyToManyFilteredIncludeProjectionRegressionTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;

            using var context = CreateContext(database);
            context.Database.EnsureCreated();

            var course1 = new CourseNode { Id = 10, Name = "Logic" };
            var course2 = new CourseNode { Id = 20, Name = "Storage" };
            var student1 = new StudentNode { Id = 1, Name = "Ada" };
            var student2 = new StudentNode { Id = 2, Name = "Alan" };
            var tag1 = new TagNode { Id = 100, Name = "Math" };
            var tag2 = new TagNode { Id = 200, Name = "Compilers" };
            var tag3 = new TagNode { Id = 300, Name = "BTree" };

            student1.Courses.Add(course1);
            student1.Courses.Add(course2);
            student2.Courses.Add(course1);

            tag1.Courses.Add(course1);
            tag2.Courses.Add(course1);
            tag3.Courses.Add(course2);

            context.AddRange(student1, student2, tag1, tag2, tag3);
            context.SaveChanges();
            context.ChangeTracker.Clear();

            return new ManyToManyScope(dbPath, engine, database);
        }

        public ManyToManyContext CreateContext()
            => CreateContext(_database);

        private static ManyToManyContext CreateContext(WalhallaEngine database)
        {
            var options = new DbContextOptionsBuilder<ManyToManyContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
                .Options;

            return new ManyToManyContext(options);
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

    private sealed class ManyToManyContext(DbContextOptions<ManyToManyContext> options)
        : WalhallaSqlEfCoreContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StudentNode>(entity =>
            {
                entity.ToTable("MtmStudents");
                entity.HasKey(student => student.Id);
                entity.Property(student => student.Name).IsRequired();
                entity.HasMany(student => student.Courses)
                    .WithMany(course => course.Students);
            });

            modelBuilder.Entity<CourseNode>(entity =>
            {
                entity.ToTable("MtmCourses");
                entity.HasKey(course => course.Id);
                entity.Property(course => course.Name).IsRequired();
                entity.HasMany(course => course.Tags)
                    .WithMany(tag => tag.Courses);
            });

            modelBuilder.Entity<TagNode>(entity =>
            {
                entity.ToTable("MtmTags");
                entity.HasKey(tag => tag.Id);
                entity.Property(tag => tag.Name).IsRequired();
            });
        }
    }

    private sealed class StudentNode
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<CourseNode> Courses { get; set; } = new();
    }

    private sealed class CourseNode
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<StudentNode> Students { get; set; } = new();
        public List<TagNode> Tags { get; set; } = new();
    }

    private sealed class TagNode
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<CourseNode> Courses { get; set; } = new();
    }
}
