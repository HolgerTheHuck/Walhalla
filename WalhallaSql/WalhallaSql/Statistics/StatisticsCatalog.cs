using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace WalhallaSql.Statistics;

/// <summary>
/// Thread-safe in-memory catalog of per-table statistics.
/// Populated by ANALYZE, consumed by the query planner.
/// One instance lives inside <c>WalhallaEngine</c> for the lifetime of the database session.
/// </summary>
internal sealed class StatisticsCatalog
{
    private readonly ConcurrentDictionary<int, TableStatistics> _stats = new();
    private long _hits;
    private long _misses;

    /// <summary>Number of successful statistics lookups (table had ANALYZE results).</summary>
    public long Hits => Volatile.Read(ref _hits);

    /// <summary>Number of failed statistics lookups (table not yet ANALYZEd).</summary>
    public long Misses => Volatile.Read(ref _misses);

    /// <summary>
    /// Try to retrieve statistics for a table.
    /// Returns <c>false</c> when ANALYZE has not yet run for this table.
    /// </summary>
    public bool TryGet(int tableId, [NotNullWhen(true)] out TableStatistics? statistics)
    {
        if (_stats.TryGetValue(tableId, out statistics))
        {
            Interlocked.Increment(ref _hits);
            WalhallaDiagnostics.EstimatorHits.Add(1);
            return true;
        }
        Interlocked.Increment(ref _misses);
        WalhallaDiagnostics.EstimatorFallbacks.Add(1);
        return false;
    }

    /// <summary>
    /// Store or replace statistics for a table (called after ANALYZE completes).
    /// Safe to call from any thread.
    /// </summary>
    public void Set(int tableId, TableStatistics statistics)
        => _stats[tableId] = statistics;

    /// <summary>
    /// Remove statistics for a table.
    /// Called on DROP TABLE or schema changes that invalidate cached stats.
    /// </summary>
    public void Invalidate(int tableId)
        => _stats.TryRemove(tableId, out _);

    /// <summary>Remove all cached statistics (e.g., on engine reset).</summary>
    public void InvalidateAll()
        => _stats.Clear();
}
