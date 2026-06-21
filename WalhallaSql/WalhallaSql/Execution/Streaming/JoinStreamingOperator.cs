using System;
using System.Collections.Generic;
using WalhallaSql.Execution.Join;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Streaming;

/// <summary>
/// Streaming-Nested-Loop-Join. Die linke Seite wird gestreamt; für jede linke Zeile
/// wird die rechte Tabelle erneut gescannt und passende Zeilen sofort ausgegeben.
///
/// <para>
/// Der Operator materialisiert weder die linke noch die rechte Seite vollständig.
/// Er ist geeignet für INNER JOIN, LEFT JOIN und CROSS JOIN. RIGHT JOIN ist hier
/// nicht implementiert, weil es die rechte Seite als äußeren Scan erfordern würde.
/// </para>
/// </summary>
internal sealed class JoinStreamingOperator : IStreamingOperator
{
    private readonly IStreamingOperator _left;
    private readonly JoinStep _step;
    private readonly int _leftWidth;

    public JoinStreamingOperator(IStreamingOperator left, JoinStep step, int leftWidth)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _leftWidth = leftWidth;
    }

    public IEnumerable<StreamingRow> Execute(StreamingContext context)
    {
        var where = _step.WhereDelegate;
        var parameters = context.Parameters;
        var cmp = JoinKeyComparer.Instance;
        var leftKeys = _step.LeftColumnIndices;
        var rightKeys = _step.RightColumnIndices;
        var rightWidth = _step.TableDef.Columns.Count;
        var paramArray = parameters ?? Array.Empty<object?>();

        foreach (var leftRow in _left.Execute(context))
        {
            var leftValues = leftRow.Values;
            var leftKey = ExtractKey(leftValues, leftKeys);
            bool emitted = false;

            // Für jede linke Zeile die rechte Tabelle erneut scannen.
            foreach (var rightValues in ScanRight(context))
            {
                var rightKey = ExtractKey(rightValues, rightKeys);

                object?[] row;
                if (_step.Kind == SqlJoinKind.Cross)
                {
                    row = Combine(leftValues, rightValues);
                    if (where == null || where(row, paramArray))
                    {
                        emitted = true;
                        yield return new StreamingRow(row);
                    }
                    continue;
                }

                if (AnyNull(leftKey) || AnyNull(rightKey))
                    continue;

                if (!KeysEqual(leftKey, rightKey, cmp))
                    continue;

                row = Combine(leftValues, rightValues);
                if (where == null || where(row, paramArray))
                {
                    emitted = true;
                    yield return new StreamingRow(row);
                }
            }

            if (!emitted && _step.Kind == SqlJoinKind.Left)
            {
                yield return new StreamingRow(LeftNullFilled(leftValues, rightWidth));
            }
        }
    }

    private IEnumerable<object?[]> ScanRight(StreamingContext context)
    {
        return context.Store.ScanWithPredicateLazy(_step.TableId, _step.TableDef, predicate: null);
    }

    private object?[] Combine(object?[] left, object?[] right)
    {
        var combined = new object?[_leftWidth + right.Length];
        Array.Copy(left, 0, combined, 0, left.Length);
        Array.Copy(right, 0, combined, _leftWidth, right.Length);
        return combined;
    }

    private object?[] LeftNullFilled(object?[] left, int rightWidth)
    {
        var combined = new object?[_leftWidth + rightWidth];
        Array.Copy(left, 0, combined, 0, left.Length);
        return combined;
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
}
