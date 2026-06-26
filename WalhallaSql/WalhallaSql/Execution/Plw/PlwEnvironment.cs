using System;
using System.Collections.Generic;
using System.Globalization;
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
    public const string SqlStateVariableName = "sqlstate";
    public const string SqlErrmVariableName = "sqlerrm";
    public const string NewVariableName = "new";
    public const string OldVariableName = "old";
    public const string TgOpVariableName = "tg_op";
    public const string TgTableNameVariableName = "tg_table_name";
    public const string TgWhenVariableName = "tg_when";
    public const string TgNameVariableName = "tg_name";

    private readonly Dictionary<string, PlwVariable> _variables;
    private readonly Dictionary<string, PlwCursor> _cursors;
    private readonly PlwEnvironment? _parent;
    private bool _found;
    private string _sqlState = "00000";
    private string _sqlErrm = string.Empty;
    private IDictionary<string, object?>? _triggerNewRow;
    private IDictionary<string, object?>? _triggerOldRow;
    private string _triggerOperation = string.Empty;
    private string _triggerTableName = string.Empty;
    private string _triggerWhen = string.Empty;
    private string _triggerName = string.Empty;

    public PlwEnvironment(PlwEnvironment? parent = null)
    {
        _variables = new Dictionary<string, PlwVariable>(StringComparer.OrdinalIgnoreCase);
        _cursors = new Dictionary<string, PlwCursor>(StringComparer.OrdinalIgnoreCase);
        _parent = parent;
    }

    /// <summary>
    /// Setzt die Trigger-Kontextvariablen (NEW, OLD, TG_OP, TG_TABLE_NAME, TG_WHEN, TG_NAME).
    /// Wird von PlwInterpreter.ExecuteTrigger vor dem Body aufgerufen.
    /// </summary>
    public void SetTriggerContext(
        IDictionary<string, object?>? newRow,
        IDictionary<string, object?>? oldRow,
        string operation,
        string tableName,
        string when,
        string triggerName)
    {
        _triggerNewRow = newRow;
        _triggerOldRow = oldRow;
        _triggerOperation = operation;
        _triggerTableName = tableName;
        _triggerWhen = when;
        _triggerName = triggerName;

        if (_parent != null)
            _parent.SetTriggerContext(newRow, oldRow, operation, tableName, when, triggerName);
    }

    public IDictionary<string, object?>? GetTriggerNewRow() => _triggerNewRow;
    public IDictionary<string, object?>? GetTriggerOldRow() => _triggerOldRow;

    public void Declare(string name, string typeName, object? value, bool allowOverwrite = false)
    {
        var key = Normalize(name);
        if (IsSystemVariable(key) || IsTriggerContextVariable(key))
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

        if (IsSqlStateVariable(key))
        {
            SetSqlState(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "00000");
            return;
        }

        if (IsSqlErrmVariable(key))
        {
            SetSqlErrm(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            return;
        }

        if (IsTriggerContextVariable(key))
        {
            // Trigger-Kontextvariablen sind read-only; Zuweisungen werden ignoriert.
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

        if (IsSqlStateVariable(key))
            return _sqlState;

        if (IsSqlErrmVariable(key))
            return _sqlErrm;

        if (IsTriggerContextVariable(key))
            return GetTriggerContextVariable(key);

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

        if (IsSqlStateVariable(key))
        {
            value = _sqlState;
            return true;
        }

        if (IsSqlErrmVariable(key))
        {
            value = _sqlErrm;
            return true;
        }

        if (IsTriggerContextVariable(key))
        {
            value = GetTriggerContextVariable(key);
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
        return IsSystemVariable(key) || IsTriggerContextVariable(key) || _variables.ContainsKey(key) || (_parent?.Contains(name) ?? false);
    }

    public string? TryGetTypeName(string name)
    {
        var key = Normalize(name);
        if (IsFoundVariable(key))
            return "BOOLEAN";

        if (IsSqlStateVariable(key) || IsSqlErrmVariable(key))
            return "STRING";

        if (IsTriggerContextVariable(key))
            return "RECORD";

        if (_variables.TryGetValue(key, out var variable))
            return variable.TypeName;

        if (_parent != null)
            return _parent.TryGetTypeName(name);

        return null;
    }

    private object? GetTriggerContextVariable(string key)
    {
        if (string.Equals(key, NewVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerNewRow;

        if (string.Equals(key, OldVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerOldRow;

        if (string.Equals(key, TgOpVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerOperation;

        if (string.Equals(key, TgTableNameVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerTableName;

        if (string.Equals(key, TgWhenVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerWhen;

        if (string.Equals(key, TgNameVariableName, StringComparison.OrdinalIgnoreCase))
            return _triggerName;

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

    /// <summary>
    /// Setzt SQLSTATE und SQLERRM. Wird beim Auftreten oder Abfangen einer Exception aufgerufen.
    /// </summary>
    public void SetErrorState(string sqlState, string sqlErrm)
    {
        _sqlState = sqlState;
        _sqlErrm = sqlErrm;
        if (_parent != null)
            _parent.SetErrorState(sqlState, sqlErrm);
    }

    public void SetSqlState(string sqlState)
    {
        _sqlState = sqlState;
        if (_parent != null)
            _parent.SetSqlState(sqlState);
    }

    public void SetSqlErrm(string sqlErrm)
    {
        _sqlErrm = sqlErrm;
        if (_parent != null)
            _parent.SetSqlErrm(sqlErrm);
    }

    private static bool IsFoundVariable(string key)
        => string.Equals(key, FoundVariableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlStateVariable(string key)
        => string.Equals(key, SqlStateVariableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlErrmVariable(string key)
        => string.Equals(key, SqlErrmVariableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsTriggerContextVariable(string key)
        => string.Equals(key, NewVariableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, OldVariableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, TgOpVariableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, TgTableNameVariableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, TgWhenVariableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, TgNameVariableName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemVariable(string key)
        => IsFoundVariable(key) || IsSqlStateVariable(key) || IsSqlErrmVariable(key);

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
