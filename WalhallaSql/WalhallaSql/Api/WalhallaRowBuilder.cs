using System;
using System.Collections.Generic;

namespace WalhallaSql;

/// <summary>
/// Baut eine einzelne Zeile für ein <see cref="WalhallaResultSetBuilder"/>.
/// Unterstützt sowohl positions- als auch namensbasiertes Befüllen.
/// </summary>
public sealed class WalhallaRowBuilder
{
    private readonly object?[] _values;
    private readonly ColumnSchema _schema;

    internal WalhallaRowBuilder(ColumnSchema schema)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _values = new object?[schema.Count];
    }

    public object? this[int ordinal]
    {
        set
        {
            if (ordinal < 0 || ordinal >= _values.Length)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            _values[ordinal] = value;
        }
    }

    public object? this[string columnName]
    {
        set
        {
            if (!_schema.TryGetOrdinal(columnName, out var ordinal))
                throw new ArgumentException($"Column '{columnName}' not found.", nameof(columnName));
            _values[ordinal] = value;
        }
    }

    internal object?[] Values => _values;
}
