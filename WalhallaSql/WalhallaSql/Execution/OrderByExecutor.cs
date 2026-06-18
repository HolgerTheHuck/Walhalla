using System;
using System.Collections.Generic;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal static class OrderByExecutor
{
    public static void SortInPlace(
        List<object?[]> rows,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        IReadOnlyList<int> columnIndices,
        SqlTableDefinition tableDef)
    {
        if (rows.Count <= 1) return;

        var comparer = new RowComparer(orderBy, columnIndices, tableDef);
        rows.Sort(comparer);
    }

    /// <summary>
    /// Creates a reusable comparer that orders raw rows by the given ORDER BY columns.
    /// Used by window-function evaluation to sort partitions while keeping the
    /// original row index bound to its row.
    /// </summary>
    public static IComparer<object?[]> CreateRowComparer(
        IReadOnlyList<SqlOrderByColumn> orderBy,
        IReadOnlyList<int> columnIndices,
        SqlTableDefinition tableDef)
        => new RowComparer(orderBy, columnIndices, tableDef);

    private sealed class RowComparer : IComparer<object?[]>
    {
        private readonly IReadOnlyList<SqlOrderByColumn> _orderBy;
        private readonly IReadOnlyList<int> _columnIndices;
        private readonly SqlTableDefinition _tableDef;

        public RowComparer(
            IReadOnlyList<SqlOrderByColumn> orderBy,
            IReadOnlyList<int> columnIndices,
            SqlTableDefinition tableDef)
        {
            _orderBy = orderBy;
            _columnIndices = columnIndices;
            _tableDef = tableDef;
        }

        public int Compare(object?[]? x, object?[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            for (int i = 0; i < _orderBy.Count; i++)
            {
                var col = _orderBy[i];
                var colIdx = FindColumnIndex(_tableDef, col.ColumnName);
                if (colIdx < 0 || colIdx >= x.Length || colIdx >= y.Length) continue;

                var cmp = CompareValues(x[colIdx], y[colIdx]);
                if (cmp != 0)
                    return col.Descending ? -cmp : cmp;
            }
            return 0;
        }

        private static int FindColumnIndex(SqlTableDefinition table, string name)
        {
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static int CompareValues(object? left, object? right)
        {
            if (left == null && right == null) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            if (left is long l && right is long r) return l.CompareTo(r);
            if (left is int il && right is int ir) return il.CompareTo(ir);
            if (left is string ls && right is string rs) return WalhallaSql.Collation.CollationManager.Compare(ls, rs, null);
            if (left is double ld && right is double rd) return ld.CompareTo(rd);

            if (left is IComparable lc && right.GetType() == left.GetType())
                return lc.CompareTo(right);

            var sLeft = Convert.ToString(left) ?? string.Empty;
            var sRight = Convert.ToString(right) ?? string.Empty;
            return WalhallaSql.Collation.CollationManager.Compare(sLeft, sRight, null);
        }
    }
}
