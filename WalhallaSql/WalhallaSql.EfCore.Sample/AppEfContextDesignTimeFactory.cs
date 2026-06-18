using System;
using System.Collections.Concurrent;
using System.IO;
using WalhallaSql;
using WalhallaSql.EfCore;
using WalhallaSql.EfCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Threading;

public sealed class AppEfContextDesignTimeFactory : IDesignTimeDbContextFactory<AppEfContext>
{
    private const string DesignTimeDatabaseName = "App";
    private static readonly ConcurrentDictionary<string, DesignTimeHeldMutex> HeldPreOpenLocks = new(StringComparer.Ordinal);

    public AppEfContext CreateDbContext(string[] args)
    {
        var dbPath = Environment.GetEnvironmentVariable("WALHALLASQL_EF_DESIGNTIME_PATH");
        if (string.IsNullOrWhiteSpace(dbPath))
            dbPath = Path.Combine(Path.GetTempPath(), "WalhallaSql", "EfDesignTime");

        Directory.CreateDirectory(dbPath);

        var migrationLockWaitTimeout = TimeSpan.FromSeconds(30);
        var lockWaitTimeoutText = Environment.GetEnvironmentVariable("WALHALLASQL_EF_DESIGNTIME_MIGRATION_LOCK_WAIT_MS");
        if (int.TryParse(lockWaitTimeoutText, out var lockWaitTimeoutMs) && lockWaitTimeoutMs > 0)
            migrationLockWaitTimeout = TimeSpan.FromMilliseconds(lockWaitTimeoutMs);

        var migrationLockStaleThreshold = TimeSpan.FromMinutes(10);
        var lockStaleThresholdText = Environment.GetEnvironmentVariable("WALHALLASQL_EF_DESIGNTIME_MIGRATION_LOCK_STALE_MS");
        if (int.TryParse(lockStaleThresholdText, out var lockStaleThresholdMs) && lockStaleThresholdMs > 0)
            migrationLockStaleThreshold = TimeSpan.FromMilliseconds(lockStaleThresholdMs);

        HoldDesignTimePreOpenLock(dbPath, migrationLockWaitTimeout);

        var engine = WalhallaEngine.Open(dbPath);

        var layeredOptions = new WalhallaSqlEfCoreOptions(engine)
        {
            MigrationLockWaitTimeout = migrationLockWaitTimeout,
            MigrationLockStaleThreshold = migrationLockStaleThreshold,
            CollectionNameResolver = entity => entity.ClrType?.Name == nameof(UserProjection)
                ? "Users"
                : entity.ClrType?.Name ?? entity.Name
        };

        var options = new DbContextOptionsBuilder<AppEfContext>()
            .UseWalhallaSql(layeredOptions)
            .Options;

        return new AppEfContext(options);
    }

    private static void HoldDesignTimePreOpenLock(string databasePath, TimeSpan waitTimeout)
    {
        var mutexName = WalhallaSqlMigrationService.BuildEmbeddedPathPreOpenMutexName(
            databasePath,
            DesignTimeDatabaseName,
            typeof(WalhallaSql.WalhallaEngine).FullName ?? nameof(WalhallaSql.WalhallaEngine));

        HeldPreOpenLocks.GetOrAdd(mutexName, _ => DesignTimeHeldMutex.Acquire(mutexName, waitTimeout));
    }

    private sealed class DesignTimeHeldMutex
    {
        private readonly Mutex _mutex;

        private DesignTimeHeldMutex(Mutex mutex)
        {
            _mutex = mutex;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
        }

        public static DesignTimeHeldMutex Acquire(string mutexName, TimeSpan waitTimeout)
        {
            var mutex = new Mutex(false, mutexName);
            var hasMutex = false;

            try
            {
                try
                {
                    hasMutex = mutex.WaitOne(waitTimeout);
                }
                catch (AbandonedMutexException)
                {
                    hasMutex = true;
                }

                if (!hasMutex)
                {
                    mutex.Dispose();
                    throw new InvalidOperationException(
                        $"Could not acquire embedded database lock within {waitTimeout.TotalSeconds:0.###} seconds.");
                }

                return new DesignTimeHeldMutex(mutex);
            }
            catch
            {
                if (hasMutex)
                    mutex.ReleaseMutex();

                mutex.Dispose();
                throw;
            }
        }

        private void Release()
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }
}
