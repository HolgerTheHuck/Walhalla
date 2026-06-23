using System;
using System.Collections.Generic;

namespace WalhallaSql.AdoNet;

public sealed record SqlCandidateFilterTelemetry(
    string Alias,
    int CandidateCount,
    int ConsideredCount,
    int RejectedCount,
    int ScannedCount);

public sealed record SqlOptimizationInfo(
    string LogicalOperator,
    string PhysicalStrategy,
    double EstimatedCost,
    IReadOnlyList<SqlCandidateFilterTelemetry>? CandidateFilters = null);

public sealed record SqlExecutionResult(
    int AffectedRows,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null,
    SqlOptimizationInfo? Optimization = null)
{
    internal ScalarResultData? ScalarData { get; init; }

    internal string? CommandText { get; init; }

    /// <summary>
    /// Output-Parameter, die von einer Stored-Procedure-Ausführung zurückgegeben wurden
    /// (z. B. via C#-SP <c>ctx.SetOutput(name, value)</c>).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? OutputParameters { get; init; }

    public static SqlExecutionResult FromRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => new(rows.Count, rows);
}

internal sealed record ScalarResultData(
    string ColumnName,
    Type ColumnType,
    List<object?> Values);
