using System;
using System.Collections.Generic;

namespace WalhallaSql;

public sealed class ColumnSchema
{
    public readonly string[] Names;
    private readonly Dictionary<string, int> _ordinals;

    public ColumnSchema(string[] names)
    {
        Names = names;
        _ordinals = new Dictionary<string, int>(names.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            if (!_ordinals.ContainsKey(names[i]))
                _ordinals[names[i]] = i;
        }
    }

    internal int Count => Names.Length;

    internal int IndexOf(string columnName) =>
        _ordinals.TryGetValue(columnName, out var idx) ? idx : -1;

    internal bool TryGetOrdinal(string columnName, out int ordinal) =>
        _ordinals.TryGetValue(columnName, out ordinal);
}
