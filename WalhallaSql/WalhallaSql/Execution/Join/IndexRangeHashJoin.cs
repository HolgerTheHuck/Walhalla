using System;
using System.Collections.Generic;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Index-Range-Hash-Join für einspaltige Äquijoins.
///
/// <para>Statt die gesamte rechte Tabelle zu scannen, wird der passende Index nach dem
/// Minimum/Maximum der linken Schlüsselwerte befragt. Nur die Kandidatenzeilen in diesem
/// Bereich werden geladen und in eine kleine Hash-Tabelle eingetragen; anschließend wird
/// die linke Seite wie beim normalen Hash-Join probed. Das spart insbesondere auf Disk
/// erheblich I/O und Allokationen, wenn der linke Schlüsselbereich nur einen Bruchteil der
/// rechten Tabelle abdeckt.</para>
/// </summary>
internal static class IndexRangeHashJoin
{
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        List<object?[]> rightRows,
        JoinStep step,
        TableStore store,
        RowDecoder rightDecoder,
        int indexId,
        SqlScalarType rightKeyType,
        object?[]? parameters = null)
    {
        var result = new List<object?[]>();
        var leftKeys = step.LeftColumnIndices;
        var rightKeys = step.RightColumnIndices;
        var where = step.WhereDelegate;
        var paramArray = parameters ?? Array.Empty<object?>();
        int rightWidth = step.TableDef.Columns.Count;

        // Ermittle Minimum und Maximum der nicht-null linken Schlüsselwerte.
        var orderComparer = JoinKeyOrderComparer.Instance;
        object? minKey = null;
        object? maxKey = null;
        foreach (var leftRow in accumulated)
        {
            var k = leftRow[leftKeys[0]];
            if (k == null) continue;
            if (minKey == null || orderComparer.Compare(k, minKey) < 0) minKey = k;
            if (maxKey == null || orderComparer.Compare(k, maxKey) > 0) maxKey = k;
        }

        if (minKey == null)
        {
            // Alle linken Schlüssel sind NULL -> keine Equi-Join-Matches (LEFT-Füllung später).
            if (step.Kind == SqlJoinKind.Left)
            {
                foreach (var leftRow in accumulated)
                    result.Add(LeftNullFilled(leftRow, rightWidth));
            }
            return result;
        }

        var minSortKey = IndexKeyCodec.EncodeSortable(minKey, rightKeyType);
        var maxSortKey = IndexKeyCodec.EncodeSortable(maxKey, rightKeyType);
        var candidates = store.ScanIndex(indexId, minSortKey, maxSortKey, true, true);

        // Hash-Tabelle über den Kandidaten aufbauen.
        var hash = new Dictionary<object?, List<object?[]>>(new SingleKeyComparer());
        foreach (var (tableId, rowId) in candidates)
        {
            var encoded = store.GetRow(tableId, rowId);
            if (encoded == null) continue;

            var rightRow = rightDecoder(encoded);
            var key = rightRow[rightKeys[0]];
            if (key == null) continue;

            if (!hash.TryGetValue(key, out var list))
            {
                list = new List<object?[]>();
                hash[key] = list;
            }
            list.Add(rightRow);
        }

        // Proben mit der linken Seite.
        foreach (var leftRow in accumulated)
        {
            var key = leftRow[leftKeys[0]];
            if (key == null) continue;

            bool emitted = false;
            if (hash.TryGetValue(key, out var matches))
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
                result.Add(LeftNullFilled(leftRow, rightWidth));
            }
        }

        return result;
    }

    private static object?[] Combine(object?[] left, object?[] right)
    {
        var combined = new object?[left.Length + right.Length];
        Array.Copy(left, 0, combined, 0, left.Length);
        Array.Copy(right, 0, combined, left.Length, right.Length);
        return combined;
    }

    private static object?[] LeftNullFilled(object?[] leftRow, int rightWidth)
    {
        var combined = new object?[leftRow.Length + rightWidth];
        Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
        return combined;
    }

    private sealed class SingleKeyComparer : IEqualityComparer<object?>
    {
        private static readonly JoinKeyComparer _cmp = JoinKeyComparer.Instance;
        public bool Equals(object? x, object? y) => _cmp.Equals(x, y);
        public int GetHashCode(object? obj) => obj == null ? 0 : _cmp.GetHashCode(obj);
    }
}
