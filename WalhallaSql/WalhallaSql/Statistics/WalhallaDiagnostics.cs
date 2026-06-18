using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WalhallaSql.Statistics;

/// <summary>
/// Centralized .NET observability instrumentation for WalhallaSql.
/// Exposes an <see cref="ActivitySource"/> for distributed tracing and a
/// <see cref="Meter"/> for metrics — both named <c>"WalhallaSql"</c>.
/// Consumers (e.g. OpenTelemetry) subscribe via <c>MeterListener</c> /
/// <c>ActivityListener</c>; the engine is unaffected when no listener is attached.
/// </summary>
internal static class WalhallaDiagnostics
{
    internal const string SourceName = "WalhallaSql";

    /// <summary>ActivitySource for ANALYZE and query-planning activities.</summary>
    internal static readonly ActivitySource Source = new(SourceName);

    /// <summary>Meter for engine-level metrics.</summary>
    internal static readonly Meter Meter = new(SourceName);

    /// <summary>Number of tables processed by ANALYZE commands.</summary>
    internal static readonly Counter<long> AnalyzeTables =
        Meter.CreateCounter<long>(
            "walhallasql.analyze.tables",
            description: "Number of tables processed by ANALYZE commands.");

    /// <summary>Wall-clock time spent per ANALYZE command, in milliseconds.</summary>
    internal static readonly Histogram<long> AnalyzeDurationMs =
        Meter.CreateHistogram<long>(
            "walhallasql.analyze.duration_ms",
            unit: "ms",
            description: "Wall-clock time spent per ANALYZE command, in milliseconds.");

    /// <summary>Number of planner lookups that found real column statistics.</summary>
    internal static readonly Counter<long> EstimatorHits =
        Meter.CreateCounter<long>(
            "walhallasql.estimator.hits",
            description: "Number of times the selectivity estimator found real column statistics.");

    /// <summary>Number of planner lookups that fell back to default selectivity constants.</summary>
    internal static readonly Counter<long> EstimatorFallbacks =
        Meter.CreateCounter<long>(
            "walhallasql.estimator.fallbacks",
            description: "Number of times the selectivity estimator fell back to default constants.");
}
