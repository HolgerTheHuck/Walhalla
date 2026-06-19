using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Hash-join physical operator. Executes a single join step by building an in-memory hash table
/// on one side's join key and probing it with the other. This is the current default strategy
/// for equi-joins (INNER/LEFT/RIGHT) and the cartesian fallback for CROSS joins.
///
/// <para>Memory footprint: the build side is fully materialised into a
/// <see cref="Dictionary{TKey,TValue}"/u003e (O(n) rows). Spill-to-disk for very large build sides is a
/// v1.x backlog item. Sort-merge and nested-loop alternatives are introduced in later B.3 slices.</para>
/// </summary>
internal static class HashJoinOperator
{
    /// <summary>
    /// Executes one join step, combining the accumulated (left) rows with the right table rows
    /// according to <paramref name="step"/>'s <see cref="SqlJoinKind"/>. Returns the new
    /// accumulated rows (left columns followed by right columns).
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        object?[]? parameters = null)
    {
        var result = new List<object?[]>();
        var leftKeys = step.LeftColumnIndices;
        var rightKeys = step.RightColumnIndices;
        var where = step.WhereDelegate;
        var paramArray = parameters ?? Array.Empty<object?>();

        if (step.Kind == SqlJoinKind.Cross)
        {
            // CROSS JOIN: cartesian product with optional post-filter.
            foreach (var leftRow in accumulated)
                foreach (var rightRow in rightRows)
                {
                    var combined = Combine(leftRow, rightRow);
                    if (where == null || where(combined, paramArray))
                        result.Add(combined);
                }
            return result;
        }

        // Width of accumulated rows, needed for RIGHT JOIN null-fill of the left side.
        var leftWidth = accumulated.Count > 0 ? accumulated[0].Length : 0;

        if (step.Kind == SqlJoinKind.Right)
        {
            // RIGHT JOIN: build hash from left (accumulated), probe with right table rows.
            var hash = BuildHash(accumulated, leftKeys);

            foreach (var rightRow in rightRows)
            {
                var key = ExtractKey(rightRow, rightKeys);
                bool emitted = false;
                if (!AnyNull(key) && hash.TryGetValue(new CompositeKey(key), out var matches))
                {
                    foreach (var leftRow in matches)
                    {
                        var combined = Combine(leftRow, rightRow);
                        if (where == null || where(combined, paramArray))
                        {
                            result.Add(combined);
                            emitted = true;
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

        // INNER / LEFT: build hash from right (join) table, probe with accumulated rows.
        var rightHash = BuildHash(rightRows, rightKeys);

        foreach (var leftRow in accumulated)
        {
            var key = ExtractKey(leftRow, leftKeys);
            if (AnyNull(key)) continue;

            bool emitted = false;
            if (rightHash.TryGetValue(new CompositeKey(key), out var matches))
            {
                foreach (var rightRow in matches)
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

    /// <summary>Builds a hash table keyed by the given column indices; rows with a null key are skipped.</summary>
    private static Dictionary<CompositeKey, List<object?[]>> BuildHash(List<object?[]> rows, int[] keyIndices)
    {
        var hash = new Dictionary<CompositeKey, List<object?[]>>(new CompositeKeyComparer());
        foreach (var row in rows)
        {
            var key = ExtractKey(row, keyIndices);
            if (AnyNull(key)) continue;
            var ck = new CompositeKey(key);
            if (!hash.TryGetValue(ck, out var list))
            {
                list = new List<object?[]>();
                hash[ck] = list;
            }
            list.Add(row);
        }
        return hash;
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

    private static object?[] Combine(object?[] left, object?[] right)
    {
        var combined = new object?[left.Length + right.Length];
        Array.Copy(left, 0, combined, 0, left.Length);
        Array.Copy(right, 0, combined, left.Length, right.Length);
        return combined;
    }

    private readonly record struct CompositeKey(object?[] Values);

    private sealed class CompositeKeyComparer : IEqualityComparer<CompositeKey>
    {
        private static readonly JoinKeyComparer _elementComparer = JoinKeyComparer.Instance;

        public bool Equals(CompositeKey x, CompositeKey y)
        {
            var a = x.Values;
            var b = y.Values;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!_elementComparer.Equals(a[i], b[i])) return false;
            return true;
        }

        public int GetHashCode(CompositeKey obj)
        {
            var hash = new HashCode();
            foreach (var v in obj.Values) hash.Add(v, _elementComparer);
            return hash.ToHashCode();
        }
    }
}
