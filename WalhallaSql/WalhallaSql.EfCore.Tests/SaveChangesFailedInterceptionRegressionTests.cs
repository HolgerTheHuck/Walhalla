using WalhallaSql;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WalhallaSql;

namespace WalhallaSql.EfCore.Tests;

public sealed class SaveChangesFailedInterceptionRegressionTests
{
    [Fact]
    public void Duplicate_insert_raises_savechanges_failed_event_and_interceptor()
    {
        using var scope = SaveChangesFailedScope.Create();
        var interceptor = new RecordingSaveChangesInterceptor();
        using var context = scope.CreateContext(interceptor);

        context.Database.EnsureCreated();

        using var transaction = context.Database.BeginTransaction();

        context.Add(new SaveChangesFailedEntity { Id = 35, Name = "first" });
        Assert.Equal(1, context.SaveChanges());
        context.ChangeTracker.Clear();

        Exception? eventException = null;
        context.SaveChangesFailed += (_, args) => eventException = args.Exception;

        context.Add(new SaveChangesFailedEntity { Id = 35, Name = "duplicate" });

        var thrown = Assert.ThrowsAny<Exception>(() => context.SaveChanges());

        Assert.True(interceptor.SavingChangesCalled);
        Assert.True(interceptor.FailedCalled);
        Assert.Same(thrown, interceptor.Exception);
        Assert.Same(thrown, eventException);
    }

    private sealed class RecordingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool SavingChangesCalled { get; private set; }

        public bool FailedCalled { get; private set; }

        public Exception? Exception { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SavingChangesCalled = true;
            return result;
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData)
        {
            FailedCalled = true;
            Exception = eventData.Exception;
        }
    }

    private sealed class SaveChangesFailedScope : IDisposable
    {
        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly WalhallaEngine _database;

        private SaveChangesFailedScope(string dbPath, WalhallaEngine engine, WalhallaEngine database)
        {
            _dbPath = dbPath;
            _engine = engine;
            _database = database;
        }

        public static SaveChangesFailedScope Create()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "SaveChangesFailedInterceptionRegressionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);

            var engine = WalhallaEngine.Open(dbPath);
            var database = engine;
            return new SaveChangesFailedScope(dbPath, engine, database);
        }

        public SaveChangesFailedContext CreateContext(IInterceptor interceptor)
        {
            var options = new DbContextOptionsBuilder<SaveChangesFailedContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(_database))
                .AddInterceptors(interceptor)
                .Options;

            return new SaveChangesFailedContext(options);
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

    private sealed class SaveChangesFailedContext(DbContextOptions<SaveChangesFailedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SaveChangesFailedEntity>(entity =>
            {
                entity.ToTable("SaveChangesFailedEntities");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class SaveChangesFailedEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
