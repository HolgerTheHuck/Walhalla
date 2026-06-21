using System;
using System.Collections.Generic;
using System.Linq;
using WalhallaSql.Collation;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal sealed record CompiledPlan(
    int TableId,
    SqlTableDefinition TableDefinition,
    int[] ProjectionIndices,
    string[] OutputColumnNames,
    Func<object?[], object?[], bool>? WhereDelegate,
    int ParameterCount,
    int? PkLookupColumnIndex = null,
    int? PkLookupParameterIndex = null,
    object? PkLookupConstant = null,
    IndexSelection? SelectedIndex = null,
    PkRangeLookup? PkRange = null,
    JoinPlan? Join = null,
    IReadOnlyList<string>? GroupByColumns = null,
    SqlWhereExpression? Having = null,
    IReadOnlyList<SqlSelectColumn>? SelectColumns = null,
    IReadOnlyList<SqlOrderByColumn>? OrderByColumns = null,
    int? Limit = null,
    int? Offset = null,
    bool IsDistinct = false,
    Func<object?[], object?>?[]? ComputedProjections = null,
    int[]? PredicateColumnIndices = null)
{
    public ColumnCollationContext CollationContext { get; } = ColumnCollationContext.Build(TableDefinition);

    public ColumnSchema OutputSchema { get; } = new ColumnSchema(OutputColumnNames);

    public bool IsFullProjection { get; } = ProjectionIndices.Length == TableDefinition.Columns.Count
        && ProjectionIndices.SequenceEqual(Enumerable.Range(0, ProjectionIndices.Length));

    /// <summary>True when the query can be streamed row-by-row without full materialization.</summary>
    public bool IsStreamable => (OrderByColumns == null || OrderByColumns.Count == 0)
        && !IsDistinct
        && (GroupByColumns == null || GroupByColumns.Count == 0)
        && (SelectColumns == null || !SelectColumns.Any(c => c.Aggregate != null || c.WindowFunction != null))
        && Having == null
        && ComputedProjections == null
        && IsJoinStreamable;

    /// <summary>
    /// Joins sind streambar, solange sie keine RIGHT JOINs enthalten und keine
    /// unbekannt großen CROSS JOINs. RIGHT JOIN erfordert, dass die rechte Seite
    /// der äußere Scan ist; CROSS JOIN ist grundsätzlich streambar, kann aber sehr
    /// viele Zeilen erzeugen.
    /// </summary>
    private bool IsJoinStreamable
    {
        get
        {
            if (Join == null) return true;
            foreach (var step in Join.Steps)
            {
                if (step.Kind == SqlJoinKind.Right)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Maps column index (in table definition order) to output index in the result array.
    /// A value of -1 means the column is not projected and should be skipped during decode.
    /// </summary>
    public int[] DecodeMapping { get; init; } = BuildDecodeMapping(ProjectionIndices, TableDefinition.Columns.Count);

    private static int[] BuildDecodeMapping(int[] projectionIndices, int columnCount)
    {
        var mapping = new int[columnCount];
        for (int i = 0; i < columnCount; i++)
            mapping[i] = -1;
        for (int i = 0; i < projectionIndices.Length; i++)
        {
            var colIdx = projectionIndices[i];
            if (colIdx >= 0)
                mapping[colIdx] = i;
        }
        return mapping;
    }
}

internal sealed record JoinPlan(
    int BaseTableId,
    SqlTableDefinition BaseTableDef,
    IReadOnlyList<JoinStep> Steps,
    int[] ProjectionIndices,
    string[] OutputColumnNames,
    string? BaseAlias = null);

internal sealed record JoinStep(
    SqlJoinKind Kind,
    int TableId,
    SqlTableDefinition TableDef,
    string? Alias,
    int[] LeftColumnIndices,
    int[] RightColumnIndices,
    string[] LeftColumnNames,
    string[] RightColumnNames,
    int[] ProjectionIndices,
    Func<object?[], object?[], bool>? WhereDelegate = null);
