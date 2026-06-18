using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Sort-merge physical join operator. Executes a single equi-join step by merging two inputs that
/// are already ordered by their join key — no hash table is built and neither side is re-sorted.
///
/// <para>It is only selected when both inputs are detected as pre-ordered on their join key (see
/// <see cref="IsSortedByKeys"/>). In that case the natural merge output is in join-key order, which is
/// identical to the input order — so the produced rows, their order and the NULL-key handling match
/// <see cref="HashJoinOperator"/> and <see cref="NestedLoopJoin"/> exactly, making the operator a
/// drop-in alternative. CROSS joins are not handled here (they have no join key).</para>
/// </summary>
internal static class SortMergeJoin
{
    /// <summary>
    /// Returns true when <paramref name="rows"/> are in non-decreasing order of the keys at
    /// <paramref name="keyIndices"/> (null keys first), using <see cref="JoinKeyOrderComparer"/>.
    /// </summary>
    public static bool IsSortedByKeys(List<object?[]> rows, int[] keyIndices)
    {
        if (keyIndices.Length == 0) return true;
        var cmp = JoinKeyOrderComparer.Instance;
        for (int k = 1; k < rows.Count; k++)
        {
            var prev = ExtractKey(rows[k - 1], keyIndices);
            var cur = ExtractKey(rows[k], keyIndices);
            if (!CanOrder(prev, cur, cmp)) return false;
            if (CompareKeys(prev, cur, cmp) > 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Executes one equi-join step by merging the pre-sorted accumulated (left) rows with the
    /// pre-sorted right table rows. Produces the same rows, in the same order, as
    /// <see cref="HashJoinOperator.ExecuteStep"/> for inputs sorted by their join key.
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        object?[]? parameters = null)
    {
        var result = new List<object?[]>();
        var order = JoinKeyOrderComparer.Instance;
        var equal = JoinKeyComparer.Instance;

        var leftKeys = step.LeftColumnIndices;
        var rightKeys = step.RightColumnIndices;
        int leftWidth = accumulated.Count > 0 ? accumulated[0].Length : 0;
        int rightWidth = step.TableDef.Columns.Count;
        var where = step.WhereDelegate;
        var paramArray = parameters ?? Array.Empty<object?>();

        int i = 0, j = 0;
        int leftCount = accumulated.Count, rightCount = rightRows.Count;

        while (i < leftCount && j < rightCount)
        {
            var leftKey = ExtractKey(accumulated[i], leftKeys);
            if (AnyNull(leftKey))
            {
                // Null left key never matches and is never null-filled (mirrors hash/nested-loop).
                i++;
                continue;
            }

            var rightKey = ExtractKey(rightRows[j], rightKeys);
            if (AnyNull(rightKey))
            {
                // Null right key: only RIGHT join keeps it (null-filled left); otherwise dropped.
                if (step.Kind == SqlJoinKind.Right)
                    result.Add(RightNullFilled(rightRows[j], leftWidth));
                j++;
                continue;
            }

            int c = CompareKeys(leftKey, rightKey, order);
            if (c < 0)
            {
                if (step.Kind == SqlJoinKind.Left)
                    result.Add(LeftNullFilled(accumulated[i], rightWidth));
                i++;
            }
            else if (c > 0)
            {
                if (step.Kind == SqlJoinKind.Right)
                    result.Add(RightNullFilled(rightRows[j], leftWidth));
                j++;
            }
            else
            {
                // Equal-key block: gather the matching runs on both sides and filter their product.
                int i2 = i + 1;
                while (i2 < leftCount && KeysEqual(ExtractKey(accumulated[i2], leftKeys), leftKey, equal)) i2++;
                int j2 = j + 1;
                while (j2 < rightCount && KeysEqual(ExtractKey(rightRows[j2], rightKeys), rightKey, equal)) j2++;

                var leftMatched = new bool[i2 - i];
                var rightMatched = new bool[j2 - j];
                for (int a = i; a < i2; a++)
                {
                    for (int b = j; b < j2; b++)
                    {
                        var combined = Combine(accumulated[a], rightRows[b]);
                        if (where == null || where(combined, paramArray))
                        {
                            result.Add(combined);
                            leftMatched[a - i] = true;
                            rightMatched[b - j] = true;
                        }
                    }
                }

                for (int a = i; a < i2; a++)
                    if (!leftMatched[a - i] && step.Kind == SqlJoinKind.Left)
                        result.Add(LeftNullFilled(accumulated[a], rightWidth));

                for (int b = j; b < j2; b++)
                    if (!rightMatched[b - j] && step.Kind == SqlJoinKind.Right)
                        result.Add(RightNullFilled(rightRows[b], leftWidth));

                i = i2;
                j = j2;
            }
        }

        // Drain remaining left rows (unmatched).
        while (i < leftCount)
        {
            var leftKey = ExtractKey(accumulated[i], leftKeys);
            if (!AnyNull(leftKey) && step.Kind == SqlJoinKind.Left)
                result.Add(LeftNullFilled(accumulated[i], rightWidth));
            i++;
        }

        // Drain remaining right rows (unmatched). For RIGHT join both null and non-null keys are kept.
        while (j < rightCount)
        {
            if (step.Kind == SqlJoinKind.Right)
                result.Add(RightNullFilled(rightRows[j], leftWidth));
            j++;
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

    private static bool CanOrder(object?[] a, object?[] b, JoinKeyOrderComparer cmp)
    {
        for (int i = 0; i < a.Length; i++)
            if (!cmp.CanOrder(a[i], b[i])) return false;
        return true;
    }

    private static int CompareKeys(object?[] a, object?[] b, JoinKeyOrderComparer cmp)
    {
        for (int i = 0; i < a.Length; i++)
        {
            var c = cmp.Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool KeysEqual(object?[] a, object?[] b, JoinKeyComparer cmp)
    {
        for (int i = 0; i < a.Length; i++)
            if (!cmp.Equals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>
    /// Emits the cartesian product of two equal-key runs. INNER/LEFT iterate left-major (matching the
    /// hash probe order); RIGHT iterates right-major (matching the hash build/probe order for RIGHT).
    /// </summary>
    private static object?[] LeftNullFilled(object?[] leftRow, int rightWidth)
    {
        var combined = new object?[leftRow.Length + rightWidth];
        Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
        return combined;
    }

    private static object?[] RightNullFilled(object?[] rightRow, int leftWidth)
    {
        var combined = new object?[leftWidth + rightRow.Length];
        Array.Copy(rightRow, 0, combined, leftWidth, rightRow.Length);
        return combined;
    }

    private static object?[] Combine(object?[] left, object?[] right)
    {
        var combined = new object?[left.Length + right.Length];
        Array.Copy(left, 0, combined, 0, left.Length);
        Array.Copy(right, 0, combined, left.Length, right.Length);
        return combined;
    }
}
