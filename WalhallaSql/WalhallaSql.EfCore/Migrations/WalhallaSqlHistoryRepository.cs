using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WalhallaSql.EfCore.Migrations;

public sealed class WalhallaSqlHistoryRepository : IHistoryRepository
{
    private readonly ICurrentDbContext _currentDbContext;

    public WalhallaSqlHistoryRepository(ICurrentDbContext currentDbContext)
    {
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
    }

    public bool Exists()
    {
        return true;
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public IReadOnlyList<HistoryRow> GetAppliedMigrations()
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        return runtime.Migrations.GetHistory()
            .OrderBy(entry => entry.AppliedAtUtc)
            .Select(entry => new HistoryRow(entry.MigrationId, "WalhallaSql"))
            .ToArray();
    }

    public Task<IReadOnlyList<HistoryRow>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetAppliedMigrations());
    }

    public string GetCreateScript()
    {
        return "-- WalhallaSql uses an internal collection '__ef_migrations_history'.";
    }

    public string GetCreateIfNotExistsScript()
    {
        return "-- WalhallaSql history collection is created on demand.";
    }

    public string GetInsertScript(HistoryRow row)
    {
        return $"-- WalhallaSql tracks migration '{row.MigrationId}' via WalhallaSqlMigrationService.";
    }

    public string GetDeleteScript(string migrationId)
    {
        return $"-- Delete migration '{migrationId}' not implemented for WalhallaSql history repository.";
    }

    public string GetBeginIfNotExistsScript(string migrationId)
    {
        return string.Empty;
    }

    public string GetBeginIfExistsScript(string migrationId)
    {
        return string.Empty;
    }

    public string GetEndIfScript()
    {
        return string.Empty;
    }

    public void Create()
    {
        // WalhallaSql verwaltet die Migrations-Historie intern.
    }

    public Task CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Create();
        return Task.CompletedTask;
    }

    public void CreateIfNotExists()
    {
        // WalhallaSql verwaltet die Migrations-Historie intern.
    }

    public Task CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateIfNotExists();
        return Task.CompletedTask;
    }

    public IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        // WalhallaSql ist eine eingebettete Datenbank ohne globale Migrations-Sperre.
        return new NullLock(this);
    }

    public Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IMigrationsDatabaseLock>(new NullLock(this));
    }

    public LockReleaseBehavior LockReleaseBehavior { get; } = LockReleaseBehavior.Transaction;

    private sealed class NullLock(WalhallaSqlHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        IHistoryRepository IMigrationsDatabaseLock.HistoryRepository => historyRepository;

        public IMigrationsDatabaseLock ReacquireIfNeeded(bool connectionReopened, bool? transactionRestarted)
            => this;

        public Task<IMigrationsDatabaseLock> ReacquireIfNeededAsync(
            bool connectionReopened,
            bool? transactionRestarted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IMigrationsDatabaseLock>(this);
        }
    }
}
