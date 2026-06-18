using System;
using System.Collections.Concurrent;

namespace WalhallaSql.AdoNet;

public static class WalhallaSqlConnectionRegistry
{
    private static readonly ConcurrentDictionary<string, Func<WalhallaEngine>> Providers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string dataSourceName, Func<WalhallaEngine> engineFactory)
    {
        if (string.IsNullOrWhiteSpace(dataSourceName))
            throw new ArgumentException("Data source name must not be empty.", nameof(dataSourceName));

        Providers[dataSourceName] = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    public static bool TryResolve(string dataSourceName, out WalhallaEngine? engine)
    {
        engine = null;
        if (string.IsNullOrWhiteSpace(dataSourceName))
            return false;

        if (!Providers.TryGetValue(dataSourceName, out var factory))
            return false;

        engine = factory();
        return true;
    }
}
