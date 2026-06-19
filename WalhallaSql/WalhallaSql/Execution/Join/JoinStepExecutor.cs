using System.Collections.Generic;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql.Execution.Join;

/// <summary>Physical join algorithm used to execute a single join step.</summary>
internal enum JoinStrategy
{
    /// <summary>Build an in-memory hash table on one side and probe with the other.</summary>
    Hash,

    /// <summary>Scan one side directly for each row of the other; no hash build.</summary>
    NestedLoop,

    /// <summary>Merge two inputs that are already ordered by their join key; no hash build, no re-sort.</summary>
    SortMerge,
}

/// <summary>
/// Chooses the physical join strategy for a single step. This is a deliberately simple,
/// size-based rule for the v1 baseline: nested-loop when the build side is small (cheap inner
/// scan, no hash allocation), hash otherwise.
///
/// <para>A richer cost model driven by index statistics / ANALYZE (and sort-merge for pre-ordered
/// inputs) is introduced in a later B.3 slice.</para>
/// </summary>
internal static class JoinStrategySelector
{
    /// <summary>
    /// Build side at or below this row count is executed with nested-loop iteration instead of
    /// building a hash table.
    /// </summary>
    public const int NestedLoopMaxBuildRows = 100;

    /// <summary>
    /// Selects the join strategy from the materialised row counts of both inputs.
    /// For INNER/LEFT the build/probe-inner side is the right input; for RIGHT it is the left
    /// (accumulated) input. CROSS is always executed as a cartesian product via the hash operator.
    /// </summary>
    public static JoinStrategy Select(SqlJoinKind kind, int leftCount, int rightCount)
    {
        switch (kind)
        {
            case SqlJoinKind.Cross:
                return JoinStrategy.Hash; // cartesian product; both operators are equivalent here

            case SqlJoinKind.Right:
                // Inner scan walks the left (accumulated) side.
                return leftCount <= NestedLoopMaxBuildRows ? JoinStrategy.NestedLoop : JoinStrategy.Hash;

            default: // Inner / Left
                // Inner scan walks the right (join table) side.
                return rightCount <= NestedLoopMaxBuildRows ? JoinStrategy.NestedLoop : JoinStrategy.Hash;
        }
    }
}

/// <summary>
/// Executes a single join step, selecting the physical strategy from the materialised input sizes
/// and dispatching to the matching operator. Single entry point used by all join execution paths.
/// </summary>
internal static class JoinStepExecutor
{
    /// <summary>
    /// Chooses the physical strategy for a step from the materialised inputs: sort-merge when both
    /// sides are already ordered by their join key (cheapest, no allocation), otherwise the size-based
    /// hash/nested-loop rule. This is the single runtime decision shared by execution and telemetry.
    /// </summary>
    public static JoinStrategy ChooseStrategy(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step)
    {
        if (step.Kind != SqlJoinKind.Cross
            && step.LeftColumnIndices.Length > 0
            && SortMergeJoin.IsSortedByKeys(accumulated, step.LeftColumnIndices)
            && SortMergeJoin.IsSortedByKeys(rightRows, step.RightColumnIndices))
        {
            return JoinStrategy.SortMerge;
        }

        if (step.Kind != SqlJoinKind.Cross && step.LeftColumnIndices.Length == 0)
            return JoinStrategy.NestedLoop;

        return JoinStrategySelector.Select(step.Kind, accumulated.Count, rightRows.Count);
    }

    /// <summary>
    /// Executes one join step against the accumulated (left) rows and the right table rows,
    /// choosing the physical strategy. Sort-merge is used when both inputs are already ordered by
    /// their join key (cheapest, no allocation); otherwise the size-based hash/nested-loop rule
    /// applies. Returns the combined rows.
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        object?[]? parameters = null)
    {
        return ExecuteStep(accumulated, rightRows, step, store: null, rightDecoder: null, parameters);
    }

    /// <summary>
    /// Executes one join step mit optionalen Zugriff auf den TableStore. Wenn ein passender
    /// Index auf der rechten Seite existiert, wird ein index-gestützter Nested-Loop-Join
    /// verwendet; ansonsten fällt die Auswahl auf Sort-Merge, Hash oder klassischen Nested-Loop zurück.
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        TableStore? store,
        RowDecoder? rightDecoder,
        object?[]? parameters = null)
    {
        // Prüfe, ob ein Index auf der rechten Seite einen spezialisierten Join-Pfad erlaubt.
        if (store != null
            && rightDecoder != null
            && IndexNestedLoopJoin.TryGetIndex(store, step, out var indexId, out _, out var rightKeyType))
        {
            // InMemory: Punkt-Lookups sind sehr billig, daher pro linker Zeile ein Index-Lookup.
            // Disk: Ein einzelner Index-Range-Scan für das linke Schlüsselintervall ist
            // effizienter als viele Punkt-Lookups.
            if (store.IsInMemory)
            {
                return IndexNestedLoopJoin.ExecuteStep(accumulated, step, store, rightDecoder, indexId, rightKeyType, parameters);
            }

            return IndexRangeHashJoin.ExecuteStep(accumulated, rightRows, step, store, rightDecoder, indexId, rightKeyType, parameters);
        }

        var strategy = ChooseStrategy(accumulated, rightRows, step);
        return strategy switch
        {
            JoinStrategy.SortMerge => SortMergeJoin.ExecuteStep(accumulated, rightRows, step, parameters),
            JoinStrategy.NestedLoop => NestedLoopJoin.ExecuteStep(accumulated, rightRows, step, parameters),
            _ => HashJoinOperator.ExecuteStep(accumulated, rightRows, step, parameters),
        };
    }

    /// <summary>Short telemetry token for a strategy, e.g. <c>hash</c>, <c>nested-loop</c>, <c>sort-merge</c>.</summary>
    public static string StrategyTraceName(JoinStrategy strategy) => strategy switch
    {
        JoinStrategy.Hash => "hash",
        JoinStrategy.NestedLoop => "nested-loop",
        JoinStrategy.SortMerge => "sort-merge",
        _ => "unknown",
    };

    /// <summary>
    /// EXPLAIN operation label for a strategy + join kind, e.g. <c>SORT_MERGE_JOIN (INNER)</c> or
    /// <c>CROSS_JOIN</c>. CROSS has no join key and is always labelled <c>CROSS_JOIN</c>.
    /// </summary>
    public static string StrategyLabel(JoinStrategy strategy, SqlJoinKind kind)
    {
        if (kind == SqlJoinKind.Cross)
            return "CROSS_JOIN";

        var op = strategy switch
        {
            JoinStrategy.Hash => "HASH_JOIN",
            JoinStrategy.NestedLoop => "NESTED_LOOP_JOIN",
            JoinStrategy.SortMerge => "SORT_MERGE_JOIN",
            _ => "JOIN",
        };
        return $"{op} ({kind.ToString().ToUpperInvariant()})";
    }
}

/// <summary>
/// Plan-time strategy estimator used by EXPLAIN. It mirrors the runtime decision in
/// <see cref="JoinStepExecutor.ChooseStrategy"/> using estimated cardinalities (from row counts) and a
/// plan-time proxy for sort-order eligibility (both join columns are primary keys → inputs are scanned
/// in key order). Because it works from estimates, the predicted strategy is the planner's expectation;
/// the runtime may still pick differently if the materialised data is ordered unexpectedly.
/// </summary>
internal static class JoinStrategyEstimator
{
    public static JoinStrategy Estimate(SqlJoinKind kind, int leftCount, int rightCount, bool bothKeysPrimaryKey)
    {
        if (kind != SqlJoinKind.Cross && bothKeysPrimaryKey)
            return JoinStrategy.SortMerge;

        return JoinStrategySelector.Select(kind, leftCount, rightCount);
    }
}
