using System.Globalization;

namespace WalhallaSql.Collation;

internal readonly struct ColumnCollationContext
{
    private readonly CompareInfo?[]? _columnCollations;

    public static readonly ColumnCollationContext Default = new(null);

    internal ColumnCollationContext(CompareInfo?[]? columnCollations)
    {
        _columnCollations = columnCollations;
    }

    public CompareInfo? GetCollation(int columnIndex)
    {
        if (_columnCollations == null || (uint)columnIndex >= (uint)_columnCollations.Length)
            return null;
        return _columnCollations[columnIndex];
    }

    public bool IsDefault => _columnCollations == null;

    public static ColumnCollationContext Build(Sql.SqlTableDefinition tableDef)
    {
        var cols = tableDef.Columns;
        CompareInfo?[]? arr = null;
        for (int i = 0; i < cols.Count; i++)
        {
            var ci = CollationManager.GetCompareInfo(cols[i].Collation);
            if (ci != null)
            {
                arr ??= new CompareInfo?[cols.Count];
                arr[i] = ci;
            }
        }
        return new ColumnCollationContext(arr);
    }
}
