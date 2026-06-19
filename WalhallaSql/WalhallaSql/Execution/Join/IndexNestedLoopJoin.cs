using System;
using System.Collections.Generic;
using WalhallaSql.Sql;
using WalhallaSql.Storage;

namespace WalhallaSql.Execution.Join;

/// <summary>
/// Index-gestützter Nested-Loop-Join für INNER/LEFT-Äquijoins.
///
/// <para>Für jede linke Zeile wird der passende Schlüsselwert ausgelesen und ein
/// vorhandener B+Tree-Index auf der rechten Seite nach übereinstimmenden Zeilen
/// befragt. Dadurch entfällt der vollständige Scan der rechten Tabelle sowie der
/// Aufbau einer Hash-Tabelle – das spart sowohl CPU als auch Heap-Allokationen.</para>
///
/// <para>Der Operator wird nur gewählt, wenn der Join aus einer einzelnen
/// Gleichheitsbedingung besteht und die rechte Seite einen passenden Index besitzt.</para>
/// </summary>
internal static class IndexNestedLoopJoin
{
    /// <summary>
    /// Versucht, für den gegebenen Join-Schritt einen passenden Index auf der rechten
    /// Seite zu finden. Gibt die Index-ID, den rechten Schlüsselspalten-Index und
    /// den Spaltentyp zurück, wenn ein Index verwendet werden kann.
    /// </summary>
    public static bool TryGetIndex(
        TableStore store,
        JoinStep step,
        out int indexId,
        out int rightKeyColumnIndex,
        out SqlScalarType rightKeyType)
    {
        indexId = -1;
        rightKeyColumnIndex = -1;
        rightKeyType = SqlScalarType.Unknown;

        if (step.Kind == SqlJoinKind.Cross || step.Kind == SqlJoinKind.Right)
            return false;

        if (step.RightColumnIndices.Length != 1)
            return false;

        rightKeyColumnIndex = step.RightColumnIndices[0];
        if (rightKeyColumnIndex < 0 || rightKeyColumnIndex >= step.TableDef.Columns.Count)
            return false;

        var keyColumnName = step.TableDef.Columns[rightKeyColumnIndex].Name;
        var indexIds = store.GetTableIndexIds(step.TableDef.CollectionName);
        if (indexIds == null || indexIds.Count == 0)
            return false;

        foreach (var indexDef in step.TableDef.Indexes)
        {
            if (!indexIds.TryGetValue(indexDef.IndexName, out var candidateId))
                continue;

            // Präfix-Indexe (erste Spalte ist der Join-Schlüssel) sind verwendbar,
            // weil der exakte Schlüsselwert gesucht wird.
            if (indexDef.ColumnNames.Count > 0
                && string.Equals(indexDef.ColumnNames[0], keyColumnName, StringComparison.OrdinalIgnoreCase))
            {
                indexId = candidateId;
                rightKeyType = step.TableDef.Columns[rightKeyColumnIndex].Type;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Führt einen einzelnen Join-Schritt über einen Index-Lookup auf der rechten Seite aus.
    /// </summary>
    public static List<object?[]> ExecuteStep(
        List<object?[]> accumulated,
        JoinStep step,
        TableStore store,
        RowDecoder rightDecoder,
        int indexId,
        SqlScalarType rightKeyType,
        object?[]? parameters = null)
    {
        var result = new List<object?[]>();
        var leftKeys = step.LeftColumnIndices;
        var where = step.WhereDelegate;
        var paramArray = parameters ?? Array.Empty<object?>();
        int rightWidth = step.TableDef.Columns.Count;
        int leftWidth = accumulated.Count > 0 ? accumulated[0].Length : 0;

        foreach (var leftRow in accumulated)
        {
            var leftKeyValue = leftRow[leftKeys[0]];
            bool emitted = false;

            if (leftKeyValue != null)
            {
                var sortKey = IndexKeyCodec.EncodeSortable(leftKeyValue, rightKeyType);
                var matches = store.ScanIndex(indexId, sortKey, sortKey, true, true);

                foreach (var (tableId, rowId) in matches)
                {
                    var encoded = store.GetRow(tableId, rowId);
                    if (encoded == null) continue;

                    var rightRow = rightDecoder(encoded);
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
                var combined = new object?[leftWidth + rightWidth];
                Array.Copy(leftRow, 0, combined, 0, leftRow.Length);
                result.Add(combined);
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
}
