using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests;

[Trait("Category", "EFEmbeddedGate")]
public sealed class EmbeddedSaveChangesParityTests
{
    private const string SaveGuardrailPrefix = "WalhallaSql EF SaveChanges MVP limitation:";
    private const string SaveGuardrailComplexGraphCode = "LSQ-EF-SAVE-001";
    private const string SaveGuardrailConcurrencyCode = "LSQ-EF-SAVE-007";
    private const string SaveGuardrailExternalEfTransactionCode = "LSQ-EF-SAVE-010";

    [Fact]
    public void SaveChanges_update_non_existing_entity_throws_concurrency_exception_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var ghost = new UserProjection
        {
            Id = 9999,
            Name = "Ghost User",
            Age = 66
        };

        scope.Context.Attach(ghost);
        scope.Context.Entry(ghost).Property(entity => entity.Name).IsModified = true;

        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => scope.Context.SaveChanges());

        Assert.Contains(SaveGuardrailPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SaveGuardrailConcurrencyCode, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveChanges_delete_non_existing_entity_throws_concurrency_exception_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var ghost = new UserProjection
        {
            Id = 8888,
            Name = "Ghost Delete",
            Age = 77
        };

        scope.Context.Remove(ghost);

        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => scope.Context.SaveChanges());

        Assert.Contains(SaveGuardrailPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SaveGuardrailConcurrencyCode, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveChanges_modified_entry_without_scalar_changes_is_a_no_op_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var entity = new UserProjection
        {
            Id = 1,
            Name = "Ada Lovelace",
            Age = 30,
            ExternalCode = Guid.Parse("11111111-1111-1111-1111-111111111111")
        };

        scope.Context.Attach(entity);
        scope.Context.Entry(entity).State = EntityState.Modified;
        scope.Context.Entry(entity).Property(item => item.Name).IsModified = false;
        scope.Context.Entry(entity).Property(item => item.Age).IsModified = false;
        scope.Context.Entry(entity).Property(item => item.ExternalCode).IsModified = false;

        var written = scope.Context.SaveChanges();

        Assert.Equal(0, written);

        var rows = (scope.Context.ExecuteSql("SELECT Name, Age, ExternalCode FROM Users WHERE Id = 1").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .ToArray();

        var row = Assert.Single(rows);
        Assert.Equal("Ada Lovelace", row["Name"]?.ToString());
        Assert.Equal("30", row["Age"]?.ToString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", row["ExternalCode"]?.ToString());
    }

    [Fact]
    public void SaveChanges_concurrency_failure_rolls_back_previous_updates_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var existing = new UserProjection
        {
            Id = 1,
            Name = "Ada Lovelace Updated",
            Age = 30,
            ExternalCode = Guid.Parse("11111111-1111-1111-1111-111111111111")
        };

        var ghost = new UserProjection
        {
            Id = 9999,
            Name = "Ghost User",
            Age = 66
        };

        scope.Context.Attach(existing);
        scope.Context.Entry(existing).Property(entity => entity.Name).IsModified = true;

        scope.Context.Attach(ghost);
        scope.Context.Entry(ghost).Property(entity => entity.Name).IsModified = true;

        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => scope.Context.SaveChanges());

        Assert.Contains(SaveGuardrailPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SaveGuardrailConcurrencyCode, ex.Message, StringComparison.Ordinal);

        var rows = (scope.Context.ExecuteSql("SELECT Name FROM Users WHERE Id = 1").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .ToArray();

        var row = Assert.Single(rows);
        Assert.Equal("Ada Lovelace", row["Name"]?.ToString());
    }

    [Fact]
    public void SaveChanges_cyclic_graph_has_consistent_guardrail_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var options = new DbContextOptionsBuilder<CyclicGraphEfContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(scope.Database))
            .Options;

        using var context = new CyclicGraphEfContext(options);
        context.Migrations.ApplyPlannedChanges("20260325_CyclicGraphSaveChanges");

        var first = new CyclicNode { Id = 1, Name = "first" };
        var second = new CyclicNode { Id = 2, Name = "second" };
        first.Parent = second;
        second.Parent = first;

        context.Add(first);
        context.Add(second);

        var ex = Assert.Throws<NotSupportedException>(() => context.SaveChanges());

        Assert.Contains(SaveGuardrailPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SaveGuardrailComplexGraphCode, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveChanges_with_external_ef_transaction_has_consistent_guardrail_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();
        using var transaction = scope.Context.Database.BeginTransaction();

        scope.Context.Add(new UserProjection
        {
            Id = 85,
            Name = "External Transaction User",
            Age = 40
        });

        var ex = Assert.Throws<NotSupportedException>(() => scope.Context.SaveChanges());

        Assert.Contains(SaveGuardrailPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SaveGuardrailExternalEfTransactionCode, ex.Message, StringComparison.Ordinal);

        var rows = (scope.Context.ExecuteSql("SELECT Id FROM Users WHERE Id = 85").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .ToArray();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SaveChangesAsync_simple_entity_crud_cycle_is_supported_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        var insertEntity = new UserProjection
        {
            Id = 81,
            Name = "Margaret Hamilton",
            Age = 36
        };

        scope.Context.Add(insertEntity);
        var inserted = await scope.Context.SaveChangesAsync();
        Assert.Equal(1, inserted);

        insertEntity.Name = "Dr. Margaret Hamilton";
        var updated = await scope.Context.SaveChangesAsync();
        Assert.Equal(1, updated);

        scope.Context.Remove(insertEntity);
        var deleted = await scope.Context.SaveChangesAsync();
        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task SaveChangesAsync_with_precanceled_token_throws_operation_canceled_in_embedded_gate()
    {
        using var scope = EmbeddedAppScope.Create();

        scope.Context.Add(new UserProjection
        {
            Id = 82,
            Name = "Canceled Before Start",
            Age = 31
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scope.Context.SaveChangesAsync(acceptAllChangesOnSuccess: true, cts.Token));

        var rows = (scope.Context.ExecuteSql("SELECT Id FROM Users WHERE Id = 82").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .ToArray();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SaveChangesAsync_cancellation_during_batch_rolls_back_transaction_in_embedded_gate()
    {
        using var scope = EmbeddedCancellationScope.Create();

        scope.Context.Add(new UserProjection { Id = 83, Name = "Batch Cancel 1", Age = 22 });
        scope.Context.Add(new UserProjection { Id = 84, Name = "Batch Cancel 2", Age = 23 });

        using var cts = new CancellationTokenSource();
        scope.Context.CancelOnAfterExecutionCount = 1;
        scope.Context.CancelSource = cts;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scope.Context.SaveChangesAsync(acceptAllChangesOnSuccess: true, cts.Token));

        var persistedRows = (scope.Context.ExecuteSql("SELECT Id FROM Users WHERE Id IN (83, 84)").Rows
            ?? Array.Empty<IReadOnlyDictionary<string, object?>>())
            .ToArray();

        Assert.Empty(persistedRows);
    }

    private sealed class EmbeddedAppScope : IDisposable
    {
        private readonly string _dbPath;

        private EmbeddedAppScope(string dbPath, WalhallaEngine engine, WalhallaEngine database, AppEfContext context)
        {
            _dbPath = dbPath;
            Engine = engine;
            Database = database;
            Context = context;
        }

        public WalhallaEngine Engine { get; }
        public WalhallaEngine Database { get; }
        public AppEfContext Context { get; }

        public static EmbeddedAppScope Create()
        {
            var dbPath = CreateTempPath();
            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);
            var database = engine;

            var options = new DbContextOptionsBuilder<AppEfContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database)
                {
                    CollectionNameResolver = entity => entity.ClrType?.Name == nameof(UserProjection)
                        ? "Users"
                        : entity.ClrType?.Name ?? entity.Name
                })
                .Options;

            var context = new AppEfContext(options);
            context.Migrations.ApplyPlannedChanges("20260221_IncludeTests");
            SeedUsers(context);

            return new EmbeddedAppScope(dbPath, engine, database, context);
        }

        public void Dispose()
        {
            Context.Dispose();
            Engine.Dispose();

            try
            {
                if (Directory.Exists(_dbPath))
                    Directory.Delete(_dbPath, recursive: true);
            }
            catch
            {
            }
        }

        private static string CreateTempPath()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "EfEmbeddedGateParity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);
            return dbPath;
        }

        private static void SeedUsers(WalhallaSqlEfCoreContext context)
        {
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (1, 'Ada Lovelace', 30, '11111111-1111-1111-1111-111111111111')");
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (2, 'Alan Turing', 41, '22222222-2222-2222-2222-222222222222')");
            context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title, ExternalCode) VALUES (100, 1, 2, 'Hello FK', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')");
            context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title, ExternalCode) VALUES (102, 1, 2, 'Second FK', '33333333-3333-3333-3333-333333333333')");
        }
    }

    private sealed class EmbeddedCancellationScope : IDisposable
    {
        private readonly string _dbPath;

        private EmbeddedCancellationScope(string dbPath, WalhallaEngine engine, WalhallaEngine database, CancellationAwareEfContext context)
        {
            _dbPath = dbPath;
            Engine = engine;
            Database = database;
            Context = context;
        }

        public WalhallaEngine Engine { get; }
        public WalhallaEngine Database { get; }
        public CancellationAwareEfContext Context { get; }

        public static EmbeddedCancellationScope Create()
        {
            var dbPath = CreateTempPath();
            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);
            var database = engine;

            var options = new DbContextOptionsBuilder<CancellationAwareEfContext>()
                .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database)
                {
                    CollectionNameResolver = entity => entity.ClrType?.Name == nameof(UserProjection)
                        ? "Users"
                        : entity.ClrType?.Name ?? entity.Name
                })
                .Options;

            var context = new CancellationAwareEfContext(options);
            context.Migrations.ApplyPlannedChanges("20260224_SaveChangesAsyncCancellationTests");
            SeedCancellationUsers(context);

            return new EmbeddedCancellationScope(dbPath, engine, database, context);
        }

        public void Dispose()
        {
            Context.Dispose();
            Engine.Dispose();

            try
            {
                if (Directory.Exists(_dbPath))
                    Directory.Delete(_dbPath, recursive: true);
            }
            catch
            {
            }
        }

        private static string CreateTempPath()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "EfEmbeddedGateParity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbPath);
            return dbPath;
        }

        private static void SeedUsers(WalhallaSqlEfCoreContext context)
        {
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (1, 'Ada Lovelace', 30, '11111111-1111-1111-1111-111111111111')");
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (2, 'Alan Turing', 41, '22222222-2222-2222-2222-222222222222')");
            context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title, ExternalCode) VALUES (100, 1, 2, 'Hello FK', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')");
            context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title, ExternalCode) VALUES (102, 1, 2, 'Second FK', '33333333-3333-3333-3333-333333333333')");
        }

        private static void SeedCancellationUsers(WalhallaSqlEfCoreContext context)
        {
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (1, 'Ada Lovelace', 30, '11111111-1111-1111-1111-111111111111')");
            context.ExecuteSql("INSERT INTO Users (Id, Name, Age, ExternalCode) VALUES (2, 'Alan Turing', 41, '22222222-2222-2222-2222-222222222222')");
        }
    }
}
