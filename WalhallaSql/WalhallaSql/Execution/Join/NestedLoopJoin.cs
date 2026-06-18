using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Nested-loop physical join operator. Combines the accumulated (left) rows with the right table
/// rows by scanning, for each probe row, the opposite side directly — no hash table is built.
///
/// <para>This avoids the hash-build allocation and is the preferred strategy when the build side is
/// small (see <see cref="JoinStrategySelector"/>). Cost is O(|left| × |right|), so it is only chosen
/// when one side is below a small threshold.</para>
///
/// <para>Output row order and NULL-key handling are identical to <see cref="HashJoinOperator"/>,
/// so the two operators are interchangeable for a given join step.</para>
/// </summary>
internal static class NestedLoopJoin
{
    /// <summary>
    /// Executes one join step using nested-loop iteration. Produces the same rows, in the same order,
    /// as <see cref="HashJoinOperator.ExecuteStep"/> for the same input.
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        object?[]? parameters = null)
    {
        var result = new List<object?[]>();
        var cmp = JoinKeyComparer.Instance;
        var leftKeys = step.LeftColumnIndices;
        var rightKeys = step.RightColumnIndices;
        var where = step.WhereDelegate;
        var paramArray = parameters ?? Array.Empty<object?>();

        if (step.Kind == SqlJoinKind.Cross)
        {
            foreach (var leftRow in accumulated)
                foreach (var rightRow in rightRows)
                {
                    var combined = Combine(leftRow, rightRow);
                    if (where == null || where(combined, paramArray))
                        result.Add(combined);
                }
            return result;
        }

        var leftWidth = accumulated.Count > 0 ? accumulated[0].Length : 0;

        if (step.Kind == SqlJoinKind.Right)
        {
            // RIGHT JOIN: for each right row, scan accumulated (left) rows for matches.
            foreach (var rightRow in rightRows)
            {
                var rightKey = ExtractKey(rightRow, rightKeys);
                bool emitted = false;

                if (!AnyNull(rightKey))
                {
                    foreach (var leftRow in accumulated)
                    {
                        var leftKey = ExtractKey(leftRow, leftKeys);
                        if (AnyNull(leftKey)) continue; // null keys never match (mirrors hash build)
                        if (KeysEqual(leftKey, rightKey, cmp))
                        {
                            var combined = Combine(leftRow, rightRow);
                            if (where == null || where(combined, paramArray))
                            {
                                result.Add(combined);
                                emitted = true;
                            }
                        }
                    }
                }

                if (!emitted)
                {
                    // No match or all filtered out: include right row with null-filled left columns.
                    var combined = new object?[leftWidth + rightRow.Length];
                    Array.Copy(rightRow, 0, combined, leftWidth, rightRow.Length);
                    result.Add(combined);
                }
            }

            return result;
        }

        // INNER / LEFT: for each left row, scan right rows for matches.
        foreach (var leftRow in accumulated)
        {
            var leftKey = ExtractKey(leftRow, leftKeys);
            if (AnyNull(leftKey)) continue; // null keys never match (mirrors hash probe)

            bool emitted = false;
            foreach (var rightRow in rightRows)
            {
                var rightKey = ExtractKey(rightRow, rightKeys);
                if (AnyNull(rightKey)) continue; // null keys never match (mirrors hash build)
                if (KeysEqual(leftKey, rightKey, cmp))
                {
                    var combined = Combine(leftRow, rightRow);
                    if (where == null || where(combined, paramArray))
                    {
                        result.Add(combined);
                        emitted = true;
                    }
                }
            }

            if (!emitted && step.Kind == SqlJoinKind.Left)
            {
                // LEFT JOIN: include left row with null-filled right columns.
                var combined = new object?[leftRow.Length + step.TableDef.Columns.Count];
                Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
                result.Add(combined);
            }
        }

        return result;
    }

    private static object?[] ExtractKey(object?[] row, int[] indices)
    {
        var key = new object?[indices.Length];
        for (int i = 0; i < indices.Length; i++)
            key[i] = row[indices[i]];
        return key;
    }

    private static bool AnyNull(object?[] key)
    {
        foreach (var v in key)
            if (v == null) return true;
        return false;
    }

    private static bool KeysEqual(object?[] a, object?[] b, JoinKeyComparer cmp)
    {
        for (int i = 0; i < a.Length; i++)
            if (!cmp.Equals(a[i], b[i])) return false;
        return true;
    }

    private static object?[] Combine(object?[] left, object?[] right)
    {
        var combined = new object?[left.Length + right.Length];
        Array.Copy(left, 0, combined, 0, left.Length);
        Array.Copy(right, 0, combined, left.Length, right.Length);
        return combined;
    }
}
