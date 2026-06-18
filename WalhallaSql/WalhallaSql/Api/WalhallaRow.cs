using System.Collections;
using System.Collections.Generic;

namespace WalhallaSql;

public readonly struct WalhallaRow : IReadOnlyDictionary<string, object?>
{
    private readonly object?[] _values;
    private readonly ColumnSchema _schema;

    internal WalhallaRow(ColumnSchema schema, object?[] values)
    {
        _schema = schema;
        // Materialise PendingBlobValues so callers never see the internal sentinel.
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is PendingBlobValue pb)
                values[i] = pb.ToArray();
        }
        _values = values;
    }

    public object? this[string key] =>
        _schema.TryGetOrdinal(key, out var i) ? _values[i] : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => _schema.Names;
    public IEnumerable<object?> Values => _values;
    public int Count => _schema.Count;

    public bool ContainsKey(string key) => _schema.IndexOf(key) >= 0;

    public object? GetValue(int ordinal) => _values[ordinal];

    public bool IsNull(int ordinal) => _values[ordinal] is null;

    public bool TryGetValue(string key, out object? value)
    {
        if (_schema.TryGetOrdinal(key, out var i))
        {
            value = _values[i];
            return true;
        }
        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < _schema.Names.Length; i++)
            yield return new KeyValuePair<string, object?>(_schema.Names[i], _values[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
