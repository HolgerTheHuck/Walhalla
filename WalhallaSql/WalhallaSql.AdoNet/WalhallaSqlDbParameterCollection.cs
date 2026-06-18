using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace WalhallaSql.AdoNet;

public sealed class WalhallaSqlDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = new();

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot!;

    public override int Add(object value)
    {
        if (value is not DbParameter parameter)
            throw new ArgumentException("Value must be a DbParameter.", nameof(value));

        _items.Add(parameter);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        if (values == null)
            throw new ArgumentNullException(nameof(values));

        foreach (var value in values)
            Add(value!);
    }

    public override void Clear()
    {
        _items.Clear();
    }

    public override bool Contains(object value)
    {
        return value is DbParameter parameter && _items.Contains(parameter);
    }

    public override bool Contains(string value)
    {
        return _items.Any(parameter => string.Equals(parameter.ParameterName, value, StringComparison.OrdinalIgnoreCase));
    }

    public override void CopyTo(Array array, int index)
    {
        ((ICollection)_items).CopyTo(array, index);
    }

    public override IEnumerator GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    protected override DbParameter GetParameter(int index)
    {
        return _items[index];
    }

    protected override DbParameter GetParameter(string parameterName)
    {
        return _items.First(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public override int IndexOf(object value)
    {
        return value is DbParameter parameter ? _items.IndexOf(parameter) : -1;
    }

    public override int IndexOf(string parameterName)
    {
        return _items.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public override void Insert(int index, object value)
    {
        if (value is not DbParameter parameter)
            throw new ArgumentException("Value must be a DbParameter.", nameof(value));

        _items.Insert(index, parameter);
    }

    public override void Remove(object value)
    {
        if (value is DbParameter parameter)
            _items.Remove(parameter);
    }

    public override void RemoveAt(int index)
    {
        _items.RemoveAt(index);
    }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _items.RemoveAt(index);
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        _items[index] = value;
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            _items.Add(value);
        else
            _items[index] = value;
    }
}
