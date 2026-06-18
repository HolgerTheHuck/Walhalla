using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WalhallaSql.AdoNet;

internal static class EmbeddedEngineRegistry
{
    private const string EmbeddedOpenLockTimeoutEnvVar = "LAYEREDSQL_EMBEDDED_OPEN_LOCK_TIMEOUT_MS";
    private static readonly TimeSpan DefaultEmbeddedOpenLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, Lazy<SharedEngineEntry>> Entries = new(PathComparer);

    public static EmbeddedEngineLease Acquire(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));

        var normalizedPath = NormalizePath(databasePath);

        while (true)
        {
            var lazyEntry = Entries.GetOrAdd(
                normalizedPath,
                static path => new Lazy<SharedEngineEntry>(
                    () => new SharedEngineEntry(path),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            SharedEngineEntry entry;
            try
            {
                entry = lazyEntry.Value;
            }
            catch
            {
                Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEngineEntry>>(normalizedPath, lazyEntry));
                throw;
            }

            if (entry.TryAddRef())
                return new EmbeddedEngineLease(normalizedPath, entry.Engine);

            Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEngineEntry>>(normalizedPath, lazyEntry));
        }
    }

    internal static IDisposable AcquireProcessLock(string databasePath, TimeSpan? timeoutOverride = null)
    {
        var normalizedPath = NormalizePath(databasePath);
        return AcquireProcessLockCore(normalizedPath, timeoutOverride ?? GetOpenLockWaitTimeout());
    }

    internal static string BuildProcessLockName(string databasePath)
    {
        var normalizedPath = NormalizePath(databasePath);
        return BuildProcessMutexName(normalizedPath);
    }

    private static void Release(string normalizedPath)
    {
        while (true)
        {
            if (!Entries.TryGetValue(normalizedPath, out var lazyEntry))
                return;

            if (!lazyEntry.IsValueCreated)
            {
                Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEngineEntry>>(normalizedPath, lazyEntry));
                return;
            }

            var entry = lazyEntry.Value;

            if (!entry.Release())
                return;

            if (!Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEngineEntry>>(normalizedPath, lazyEntry)))
                continue;

            entry.DisposeEngine();
            return;
        }
    }

    private static string NormalizePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath.Trim());
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static ProcessWidePathLock AcquireProcessLockCore(string normalizedPath, TimeSpan waitTimeout)
    {
        var mutex = new Mutex(false, BuildProcessMutexName(normalizedPath));
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
                throw new InvalidOperationException(
                    $"Could not acquire embedded database lock for '{normalizedPath}' within {waitTimeout.TotalSeconds:0.###} seconds.");
            }

            return new ProcessWidePathLock(mutex);
        }
        catch
        {
            if (!hasMutex)
                mutex.Dispose();

            throw;
        }
    }

    private static string BuildProcessMutexName(string normalizedPath)
    {
        var normalizedIdentity = OperatingSystem.IsWindows()
            ? normalizedPath.ToUpperInvariant()
            : normalizedPath;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity));
        var shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 12));
        return $"Local\\WalhallaSqlEmbeddedOpen_{shortHash}";
    }

    private static TimeSpan GetOpenLockWaitTimeout()
    {
        var text = Environment.GetEnvironmentVariable(EmbeddedOpenLockTimeoutEnvVar);
        if (int.TryParse(text, out var timeoutMs) && timeoutMs > 0)
            return TimeSpan.FromMilliseconds(timeoutMs);

        return DefaultEmbeddedOpenLockTimeout;
    }

    internal sealed class EmbeddedEngineLease : IDisposable
    {
        private string? _normalizedPath;

        internal EmbeddedEngineLease(string normalizedPath, WalhallaEngine engine)
        {
            _normalizedPath = normalizedPath;
            Engine = engine;
        }

        internal WalhallaEngine Engine { get; }

        public void Dispose()
        {
            var normalizedPath = _normalizedPath;
            if (normalizedPath == null)
                return;

            _normalizedPath = null;
            Release(normalizedPath);
        }
    }

    private sealed class SharedEngineEntry
    {
        private readonly object _gate = new();
        private int _refCount;
        private bool _disposePending;
        private readonly IDisposable _processLock;

        internal SharedEngineEntry(string normalizedPath)
        {
            _processLock = AcquireProcessLockCore(normalizedPath, GetOpenLockWaitTimeout());

            try
            {
                Engine = WalhallaEngine.Open(normalizedPath);
            }
            catch
            {
                _processLock.Dispose();
                throw;
            }
        }

        internal WalhallaEngine Engine { get; }

        internal void AddRef()
        {
            lock (_gate)
            {
                if (_disposePending)
                    throw new ObjectDisposedException(nameof(SharedEngineEntry));

                _refCount++;
            }
        }

        internal bool TryAddRef()
        {
            lock (_gate)
            {
                if (_disposePending)
                    return false;

                _refCount++;
                return true;
            }
        }

        internal bool Release()
        {
            lock (_gate)
            {
                if (_refCount <= 0)
                    return false;

                _refCount--;
                if (_refCount != 0)
                    return false;

                _disposePending = true;
                return true;
            }
        }

        internal void DisposeEngine()
        {
            try
            {
                Engine.Dispose();
            }
            finally
            {
                _processLock.Dispose();
            }
        }
    }

    private sealed class ProcessWidePathLock : IDisposable
    {
        private Mutex? _mutex;
        private bool _hasMutex;

        internal ProcessWidePathLock(Mutex mutex)
        {
            _mutex = mutex;
            _hasMutex = true;
        }

        public void Dispose()
        {
            var mutex = _mutex;
            if (mutex == null)
                return;

            _mutex = null;

            try
            {
                if (_hasMutex)
                    mutex.ReleaseMutex();
            }
            finally
            {
                _hasMutex = false;
                mutex.Dispose();
            }
        }
    }
}
