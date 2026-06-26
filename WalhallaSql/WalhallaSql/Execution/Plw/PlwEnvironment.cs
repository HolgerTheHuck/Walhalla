using System;
using System.Collections.Generic;
using WalhallaSql.Parsing.Plw;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Variablen-Scope fuer einen PLW-Aufruf. Variablennamen werden case-insensitiv
/// behandelt und koennen wahlweise mit oder ohne fuehrendes '@' angesprochen werden.
/// Enthaelt zusaetzlich die implizite Systemvariable FOUND.
/// </summary>
internal sealed class PlwEnvironment
{
    public const string FoundVariableName = "found";

    private readonly Dictionary<string, PlwVariable> _variables;
    private readonly Dictionary<string, PlwCursor> _cursors;
    private readonly PlwEnvironment? _parent;
    private bool _found;

    public PlwEnvironment(PlwEnvironment? parent = null)
    {
        _variables = new Dictionary<string, PlwVariable>(StringComparer.OrdinalIgnoreCase);
        _cursors = new Dictionary<string, PlwCursor>(StringComparer.OrdinalIgnoreCase);
        _parent = parent;
    }

    public void Declare(string name, string typeName, object? value, bool allowOverwrite = false)
    {
        var key = Normalize(name);
        if (IsSystemVariable(key))
            throw new WalhallaException($"PLW-Variable '{name}' ist eine reservierte Systemvariable und darf nicht deklariert werden.");

        if (!allowOverwrite && _variables.ContainsKey(key))
            throw new WalhallaException($"PLW-Variable '{name}' ist bereits deklariert.");

        _variables[key] = new PlwVariable(key, typeName, value);
    }

    public void DeclareCursor(string name, PlwCursor cursor)
    {
        var key = Normalize(name);
        if (_variables.ContainsKey(key))
            throw new WalhallaException($"PLW-Name '{name}' ist bereits als Variable deklariert.");
        _cursors[key] = cursor;
    }

    public PlwCursor GetCursor(string name)
    {
        var key = Normalize(name);
        if (_cursors.TryGetValue(key, out var cursor))
            return cursor;
        if (_parent != null)
            return _parent.GetCursor(name);
        throw new WalhallaException($"PLW-Cursor '{name}' ist nicht deklariert.");
    }

    public bool HasCursor(string name)
    {
        var key = Normalize(name);
        return _cursors.ContainsKey(key) || (_parent?.HasCursor(name) ?? false);
    }

    public void Set(string name, object? value)
    {
        var key = Normalize(name);
        if (IsFoundVariable(key))
        {
            SetFound(PlwExpressionEvaluator.ToBoolean(value));
            return;
        }

        if (_variables.TryGetValue(key, out var variable))
        {
            variable.Value = value;
            return;
        }

        if (_parent != null)
        {
            _parent.Set(name, value);
            return;
        }

        throw new WalhallaException($"PLW-Variable '{name}' ist nicht deklariert.");
    }

    public object? Get(string name)
    {
        var key = Normalize(name);
        if (IsFoundVariable(key))
            return _found;

        if (_variables.TryGetValue(key, out var variable))
            return variable.Value;

        if (_parent != null)
            return _parent.Get(name);

        throw new WalhallaException($"PLW-Variable '{name}' ist nicht deklariert.");
    }

    public bool TryGet(string name, out object? value)
    {
        var key = Normalize(name);
        if (IsFoundVariable(key))
        {
            value = _found;
            return true;
        }

        if (_variables.TryGetValue(key, out var variable))
        {
            value = variable.Value;
            return true;
        }

        if (_parent != null)
            return _parent.TryGet(name, out value);

        value = null;
        return false;
    }

    public bool Contains(string name)
    {
        var key = Normalize(name);
        return IsFoundVariable(key) || _variables.ContainsKey(key) || (_parent?.Contains(name) ?? false);
    }

    public string? TryGetTypeName(string name)
    {
        var key = Normalize(name);
        if (IsFoundVariable(key))
            return "BOOLEAN";

        if (_variables.TryGetValue(key, out var variable))
            return variable.TypeName;

        if (_parent != null)
            return _parent.TryGetTypeName(name);

        return null;
    }

    /// <summary>
    /// Setzt die Systemvariable FOUND. Wird nach jeder SQL-Operation aufgerufen.
    /// </summary>
    public void SetFound(bool value)
    {
        _found = value;
        if (_parent != null)
            _parent.SetFound(value);
    }

    private static bool IsFoundVariable(string key)
        => string.Equals(key, FoundVariableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemVariable(string key)
        => IsFoundVariable(key);

    private static string Normalize(string name)
        => name.TrimStart('@').Trim();
}

/// <summary>
/// Laufzeitwert einer PLW-Variablen inklusive Typname fuer die SQL-Formatierung.
/// </summary>
internal sealed class PlwVariable
{
    public string Name { get; }
    public string TypeName { get; }
    public object? Value { get; set; }

    public PlwVariable(string name, string typeName, object? value)
    {
        Name = name;
        TypeName = typeName;
        Value = value;
    }
}

/// <summary>
/// Laufzeitzustand eines PLW-Cursors.
/// </summary>
internal sealed class PlwCursor
{
    public string Name { get; }
    public PlwSqlFragment Query { get; }

    private WalhallaResultSet? _resultSet;
    private IEnumerator<WalhallaRow>? _enumerator;

    public PlwCursor(string name, PlwSqlFragment query)
    {
        Name = name;
        Query = query;
    }

    public bool IsOpen => _enumerator != null;

    public void Open(PlwSqlExecutor executor, PlwEnvironment env)
    {
        if (IsOpen)
            throw new WalhallaException($"PLW-Cursor '{Name}' ist bereits geoeffnet.");

        _resultSet = executor.Execute(Query, env);
        _enumerator = _resultSet.Rows.GetEnumerator();
    }

    public bool FetchNext()
    {
        if (_enumerator == null)
            throw new WalhallaException($"PLW-Cursor '{Name}' ist nicht geoeffnet.");

        return _enumerator.MoveNext();
    }

    public WalhallaRow CurrentRow
    {
        get
        {
            if (_enumerator == null)
                throw new WalhallaException($"PLW-Cursor '{Name}' ist nicht geoeffnet.");
            return _enumerator.Current;
        }
    }

    public IReadOnlyList<string> ColumnNames
        => _resultSet?.ColumnNames ?? Array.Empty<string>();

    public void Close()
    {
        _enumerator?.Dispose();
        _enumerator = null;
        _resultSet = null;
    }
}
