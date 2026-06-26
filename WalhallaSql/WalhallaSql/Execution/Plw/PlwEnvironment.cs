using System;
using System.Collections.Generic;

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
    private readonly PlwEnvironment? _parent;
    private bool _found;

    public PlwEnvironment(PlwEnvironment? parent = null)
    {
        _variables = new Dictionary<string, PlwVariable>(StringComparer.OrdinalIgnoreCase);
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
