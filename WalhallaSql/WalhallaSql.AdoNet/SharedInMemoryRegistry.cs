using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace WalhallaSql.AdoNet;

/// <summary>
/// Process-wide registry for named shared in-memory engines.
/// Engines are ref-counted and disposed when all leases are released.
/// </summary>
internal static class SharedInMemoryRegistry
{
    private static readonly ConcurrentDictionary<string, Lazy<SharedEntry>> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static SharedInMemoryLease Acquire(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared in-memory name is required.", nameof(name));

        var key = name.Trim();

        while (true)
        {
            var lazyEntry = Entries.GetOrAdd(
                key,
                static k => new Lazy<SharedEntry>(
                    () => new SharedEntry(),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            SharedEntry entry;
            try
            {
                entry = lazyEntry.Value;
            }
            catch
            {
                Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEntry>>(key, lazyEntry));
                throw;
            }

            if (entry.TryAddRef())
                return new SharedInMemoryLease(key, entry.Engine);

            Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEntry>>(key, lazyEntry));
        }
    }

    private static void Release(string key)
    {
        while (true)
        {
            if (!Entries.TryGetValue(key, out var lazyEntry))
                return;

            if (!lazyEntry.IsValueCreated)
            {
                Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEntry>>(key, lazyEntry));
                return;
            }

            var entry = lazyEntry.Value;

            if (!entry.Release())
                return;

            if (!Entries.TryRemove(new KeyValuePair<string, Lazy<SharedEntry>>(key, lazyEntry)))
                continue;

            entry.DisposeEngine();
            return;
        }
    }

    internal sealed class SharedInMemoryLease : IDisposable
    {
        private string? _key;

        internal SharedInMemoryLease(string key, WalhallaEngine engine)
        {
            _key = key;
            Engine = engine;
        }

        internal WalhallaEngine Engine { get; }

        public void Dispose()
        {
            var key = _key;
            if (key == null)
                return;

            _key = null;
            Release(key);
        }
    }

    private sealed class SharedEntry
    {
        private readonly object _gate = new();
        private int _refCount;
        private bool _disposePending;

        internal SharedEntry()
        {
            Engine = WalhallaEngine.InMemory();
        }

        internal WalhallaEngine Engine { get; }

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

        internal void DisposeEngine() => Engine.Dispose();
    }
}
