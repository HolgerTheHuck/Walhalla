using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WalhallaSql.Parsing.Plw;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Fuehrt ein geparstes PLW-Programm aus. Verantwortlich fuer Parameterbindung,
/// Variablen-Scopes, SQL-Ausfuehrung, Output-Parameter und RETURN QUERY.
/// </summary>
internal sealed class PlwInterpreter
{
    private readonly PlwEnvironment _env;
    private readonly PlwSqlExecutor _executor;
    private readonly PlwExpressionEvaluator _evaluator;
    private readonly PlwExecutionContext _context;
    private readonly SqlStoredProcedureDefinition _procedure;
    private readonly IReadOnlyList<SqlExecArgument> _arguments;

    public PlwInterpreter(
        PlwEnvironment env,
        PlwSqlExecutor executor,
        PlwExpressionEvaluator evaluator,
        PlwExecutionContext context,
        SqlStoredProcedureDefinition procedure,
        IReadOnlyList<SqlExecArgument> arguments)
    {
        _env = env;
        _executor = executor;
        _evaluator = evaluator;
        _context = context;
        _procedure = procedure;
        _arguments = arguments;
    }

    public static WalhallaResultSet Execute(
        SqlStoredProcedureDefinition procedure,
        PlwProgram program,
        IReadOnlyList<SqlExecArgument> arguments,
        WalhallaEngine engine,
        PlwExecutionContext context)
    {
        var env = new PlwEnvironment();
        var evaluator = new PlwExpressionEvaluator();
        var executor = new PlwSqlExecutor(engine, evaluator);
        var interpreter = new PlwInterpreter(env, executor, evaluator, context, procedure, arguments);

        interpreter.BindParameters();

        WalhallaResultSet? result = null;
        try
        {
            interpreter.ExecuteBlock(program.Body);
        }
        catch (PlwFlowControlException ex) when (ex.Kind == PlwFlowControlKind.Return)
        {
            result = ex.ReturnResultSet;
        }

        result ??= WalhallaResultSet.Affected(0);

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in procedure.Parameters)
        {
            if (!parameter.IsOutput)
                continue;

            var key = parameter.Name.TrimStart('@');
            outputs[key] = env.TryGet(parameter.Name, out var value) ? value : null;
        }

        if (outputs.Count > 0)
            result = result.WithOutputParameters(outputs);

        return result;
    }

    /// <summary>
    /// Fuehrt einen PLW-Trigger-Body aus. Der Trigger hat keine Parameter, dafuer
    /// aber Trigger-Kontextvariablen (NEW, OLD, TG_OP, TG_TABLE_NAME, TG_WHEN, TG_NAME).
    /// </summary>
    public static WalhallaResultSet ExecuteTrigger(
        SqlTriggerDefinition trigger,
        PlwProgram program,
        WalhallaEngine engine,
        PlwExecutionContext context,
        object? newRow,
        object? oldRow)
    {
        var env = new PlwEnvironment();
        var evaluator = new PlwExpressionEvaluator();
        var executor = new PlwSqlExecutor(engine, evaluator);
        var interpreter = new PlwInterpreter(
            env,
            executor,
            evaluator,
            context,
            new SqlStoredProcedureDefinition(trigger.Name, Array.Empty<SqlProcedureParameter>(), trigger.Body, trigger.Language),
            Array.Empty<SqlExecArgument>());

        env.SetTriggerContext(
            newRow,
            oldRow,
            trigger.Event.ToString().ToUpperInvariant(),
            trigger.TableName,
            trigger.Timing.ToString().ToUpperInvariant(),
            trigger.Name);

        try
        {
            interpreter.ExecuteBlock(program.Body);
        }
        catch (PlwFlowControlException ex) when (ex.Kind == PlwFlowControlKind.Return)
        {
            return ex.ReturnResultSet ?? WalhallaResultSet.Affected(0);
        }

        return WalhallaResultSet.Affected(0);
    }

    private void BindParameters()
    {
        var named = new Dictionary<string, SqlExecArgument>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<SqlExecArgument>();

        foreach (var argument in _arguments)
        {
            if (argument.ParameterName != null)
                named[argument.ParameterName.TrimStart('@')] = argument;
            else
                positional.Add(argument);
        }

        var positionalIndex = 0;
        foreach (var parameter in _procedure.Parameters)
        {
            var key = parameter.Name.TrimStart('@');
            object? value;

            if (named.TryGetValue(key, out var namedArg))
            {
                value = ResolveParameterValue(namedArg, parameter);
            }
            else if (positionalIndex < positional.Count)
            {
                var posArg = positional[positionalIndex++];
                value = ResolveParameterValue(posArg, parameter);
            }
            else if (parameter.DefaultValue != null)
            {
                value = parameter.DefaultValue;
            }
            else if (parameter.IsNullable)
            {
                value = null;
            }
            else
            {
                throw new WalhallaException($"Parameter '{parameter.Name}' wurde fuer Prozedur '{_procedure.Name}' nicht angegeben.");
            }

            var typeName = TypeNameFromScalarType(parameter.Type);
            _env.Declare(parameter.Name, typeName, value, allowOverwrite: true);
        }
    }

    private object? ResolveParameterValue(SqlExecArgument argument, SqlProcedureParameter parameter)
    {
        if (parameter.Direction == SqlParameterDirection.Out)
            return null;

        var expression = argument.ValueExpression?.Trim();
        if (string.IsNullOrEmpty(expression))
            return null;

        if (expression.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        // Variablenreferenz aus dem aufrufenden Kontext? Im einfachen Engine-Aufruf
        // gibt es keinen; AdoNet/Dapper wandeln Variablen vorher in Literale um.
        // Wir erlauben aber explizite @name-Referenzen, falls die Engine sie
        // auflösen kann (zukuenftige Erweiterung).
        if (expression.StartsWith("@") && expression.Length > 1)
        {
            var varName = expression.Substring(1);
            if (_env.Contains(varName))
                return _env.Get(varName);
        }

        return ParseLiteral(expression, parameter.Type);
    }

    private void ExecuteBlock(PlwBlock block)
    {
        try
        {
            foreach (var declaration in block.Declarations)
            {
                ExecuteNode(declaration);
            }

            foreach (var statement in block.Body)
            {
                ExecuteNode(statement);
            }
        }
        catch (WalhallaException ex)
        {
            var handler = FindExceptionHandler(block.ExceptionHandlers, ex);
            if (handler == null)
                throw;

            _env.SetErrorState(ex.SqlState ?? "P0001", ex.Message);
            ExecuteNode(handler.Body);
        }
    }

    private static PlwExceptionHandler? FindExceptionHandler(IReadOnlyList<PlwExceptionHandler>? handlers, WalhallaException ex)
    {
        if (handlers == null || handlers.Count == 0)
            return null;

        foreach (var handler in handlers)
        {
            var condition = handler.Condition;
            if (condition.Equals("OTHERS", StringComparison.OrdinalIgnoreCase))
                return handler;

            if (!string.IsNullOrEmpty(ex.SqlState))
            {
                if (condition.Equals(ex.SqlState, StringComparison.OrdinalIgnoreCase))
                    return handler;

                // Vordefinierte Namen, die auf SQLSTATE gemappt werden.
                var mapped = MapExceptionNameToSqlState(condition);
                if (mapped.Equals(ex.SqlState, StringComparison.OrdinalIgnoreCase))
                    return handler;
            }
        }

        return null;
    }

    private static string MapExceptionNameToSqlState(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "DIVISION_BY_ZERO" => "22012",
            "NO_DATA_FOUND" => "P0002",
            "TOO_MANY_ROWS" => "P0003",
            "UNIQUE_VIOLATION" => "23505",
            "FOREIGN_KEY_VIOLATION" => "23503",
            "CHECK_VIOLATION" => "23514",
            _ => name
        };
    }

    private void ExecuteNode(PlwNode node)
    {
        _context.Step();

        switch (node)
        {
            case PlwBlock block:
                ExecuteBlock(block);
                break;

            case PlwVariableDeclaration decl:
            {
                var defaultValue = decl.DefaultValue != null
                    ? _evaluator.Evaluate(decl.DefaultValue, _env)
                    : null;
                _env.Declare(decl.Name, decl.TypeName, defaultValue, allowOverwrite: false);
                break;
            }

            case PlwAssignment assignment:
                Assign(assignment.Target, _evaluator.Evaluate(assignment.Value, _env));
                break;

            case PlwIf ifStmt:
                ExecuteIf(ifStmt);
                break;

            case PlwSimpleLoop simpleLoop:
                ExecuteSimpleLoop(simpleLoop);
                break;

            case PlwWhileLoop whileLoop:
                ExecuteWhileLoop(whileLoop);
                break;

            case PlwForIntegerLoop forInt:
                ExecuteForIntegerLoop(forInt);
                break;

            case PlwForQueryLoop forQuery:
                ExecuteForQueryLoop(forQuery);
                break;

            case PlwExit exit:
            {
                if (exit.WhenCondition == null || PlwExpressionEvaluator.ToBoolean(_evaluator.Evaluate(exit.WhenCondition, _env)))
                    throw new PlwFlowControlException(PlwFlowControlKind.Exit);
                break;
            }

            case PlwContinue continueStmt:
            {
                if (continueStmt.WhenCondition == null || PlwExpressionEvaluator.ToBoolean(_evaluator.Evaluate(continueStmt.WhenCondition, _env)))
                    throw new PlwFlowControlException(PlwFlowControlKind.Continue);
                break;
            }

            case PlwReturn returnStmt:
            {
                if (returnStmt.Value != null)
                    throw new WalhallaException("RETURN mit Ausdruck ist in PLW-Prozeduren nicht erlaubt. Verwenden Sie OUT-Parameter oder RETURN QUERY.");

                throw new PlwFlowControlException(PlwFlowControlKind.Return);
            }

            case PlwReturnQuery returnQuery:
            {
                var result = _executor.Execute(returnQuery.Query, _env);
                _env.SetFound(result.Rows.Count > 0);
                throw new PlwFlowControlException(result);
            }

            case PlwPerform perform:
                SetFoundFromResult(_executor.Execute(perform.Statement, _env));
                break;

            case PlwSelectInto selectInto:
                ExecuteSelectInto(selectInto);
                break;

            case PlwExecute execute:
                ExecuteDynamicSql(execute);
                break;

            case PlwRaise raise:
                ExecuteRaise(raise);
                break;

            // PlwExceptionHandler ist kein ausfuehrbarer Knoten; wird von ExecuteBlock verarbeitet.
            case PlwExceptionHandler:
                throw new WalhallaException("Exception-Handler duerfen nur direkt in einem BEGIN ... EXCEPTION ... END-Block stehen.");

            case PlwSqlStatement sqlStatement:
                SetFoundFromResult(_executor.Execute(sqlStatement.Sql, _env));
                break;

            case PlwCursorDeclaration cursorDecl:
                _env.DeclareCursor(cursorDecl.Name, new PlwCursor(cursorDecl.Name, cursorDecl.Query));
                break;

            case PlwOpenCursor openCursor:
                _env.GetCursor(openCursor.CursorName).Open(_executor, _env);
                break;

            case PlwFetchCursor fetchCursor:
                ExecuteFetchCursor(fetchCursor);
                break;

            case PlwCloseCursor closeCursor:
                _env.GetCursor(closeCursor.CursorName).Close();
                break;

            default:
                throw new WalhallaException($"Nicht unterstuetzter PLW-Knoten: {node.GetType().Name}");
        }
    }

    private void ExecuteIf(PlwIf ifStmt)
    {
        if (PlwExpressionEvaluator.ToBoolean(_evaluator.Evaluate(ifStmt.Condition, _env)))
        {
            ExecuteNode(ifStmt.ThenBranch);
            return;
        }

        foreach (var elsif in ifStmt.ElsifBranches)
        {
            if (PlwExpressionEvaluator.ToBoolean(_evaluator.Evaluate(elsif.Condition, _env)))
            {
                ExecuteNode(elsif.ThenBranch);
                return;
            }
        }

        if (ifStmt.ElseBranch != null)
            ExecuteNode(ifStmt.ElseBranch);
    }

    private void ExecuteSimpleLoop(PlwSimpleLoop loop)
    {
        while (true)
        {
            try
            {
                ExecuteNode(loop.Body);
            }
            catch (PlwFlowControlException ex)
            {
                if (ex.Kind == PlwFlowControlKind.Exit)
                    break;
                if (ex.Kind == PlwFlowControlKind.Continue)
                    continue;
                throw;
            }
        }
    }

    private void ExecuteWhileLoop(PlwWhileLoop loop)
    {
        while (PlwExpressionEvaluator.ToBoolean(_evaluator.Evaluate(loop.Condition, _env)))
        {
            try
            {
                ExecuteNode(loop.Body);
            }
            catch (PlwFlowControlException ex)
            {
                if (ex.Kind == PlwFlowControlKind.Exit)
                    break;
                if (ex.Kind == PlwFlowControlKind.Continue)
                    continue;
                throw;
            }
        }
    }

    private void ExecuteForIntegerLoop(PlwForIntegerLoop loop)
    {
        var lowerValue = _evaluator.Evaluate(loop.Lower, _env);
        var upperValue = _evaluator.Evaluate(loop.Upper, _env);

        var lower = Convert.ToInt32(lowerValue, CultureInfo.InvariantCulture);
        var upper = Convert.ToInt32(upperValue, CultureInfo.InvariantCulture);

        _env.Declare(loop.VariableName, "INT", null, allowOverwrite: true);

        if (loop.Reverse)
        {
            for (var i = upper; i >= lower; i--)
            {
                _env.Set(loop.VariableName, i);
                try
                {
                    ExecuteNode(loop.Body);
                }
                catch (PlwFlowControlException ex)
                {
                    if (ex.Kind == PlwFlowControlKind.Exit)
                        break;
                    if (ex.Kind == PlwFlowControlKind.Continue)
                        continue;
                    throw;
                }
            }
        }
        else
        {
            for (var i = lower; i <= upper; i++)
            {
                _env.Set(loop.VariableName, i);
                try
                {
                    ExecuteNode(loop.Body);
                }
                catch (PlwFlowControlException ex)
                {
                    if (ex.Kind == PlwFlowControlKind.Exit)
                        break;
                    if (ex.Kind == PlwFlowControlKind.Continue)
                        continue;
                    throw;
                }
            }
        }
    }

    private void ExecuteForQueryLoop(PlwForQueryLoop loop)
    {
        var result = _executor.Execute(loop.Query, _env);
        _env.SetFound(result.Rows.Count > 0);

        foreach (var row in result.Rows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < result.ColumnNames.Count; i++)
                dict[result.ColumnNames[i]] = row.GetValue(i);

            _env.Declare(loop.VariableName, "RECORD", dict, allowOverwrite: true);

            try
            {
                ExecuteNode(loop.Body);
            }
            catch (PlwFlowControlException ex)
            {
                if (ex.Kind == PlwFlowControlKind.Exit)
                    break;
                if (ex.Kind == PlwFlowControlKind.Continue)
                    continue;
                throw;
            }
        }
    }

    private void ExecuteSelectInto(PlwSelectInto selectInto)
    {
        var result = _executor.Execute(selectInto.SelectSql, _env);

        var targets = selectInto.Targets
            .Select(t => t as PlwIdentifierExpression ?? throw new WalhallaException("SELECT INTO erwartet einfache Variablen als Ziele."))
            .ToArray();

        if (result.Rows.Count == 0)
        {
            foreach (var target in targets)
                _env.Set(target.Name, null);
            return;
        }

        if (result.Rows.Count > 1)
        {
            throw new WalhallaException(
                $"SELECT INTO lieferte {result.Rows.Count} Zeilen zurueck, erwartet wurde hoechstens eine.",
                "P0003");
        }

        _env.SetFound(result.Rows.Count > 0);

        var row = result.Rows[0];
        for (var i = 0; i < Math.Min(targets.Length, result.ColumnNames.Count); i++)
        {
            _env.Set(targets[i].Name, row.GetValue(i));
        }
    }

    private void ExecuteFetchCursor(PlwFetchCursor fetch)
    {
        var cursor = _env.GetCursor(fetch.CursorName);
        var hasRow = cursor.FetchNext();
        _env.SetFound(hasRow);

        if (!hasRow)
            return;

        var row = cursor.CurrentRow;
        var targets = fetch.Targets
            .Select(t => t as PlwIdentifierExpression ?? throw new WalhallaException("FETCH INTO erwartet einfache Variablen als Ziele."))
            .ToArray();

        for (var i = 0; i < Math.Min(targets.Length, cursor.ColumnNames.Count); i++)
        {
            _env.Set(targets[i].Name, row.GetValue(i));
        }
    }

    private void ExecuteDynamicSql(PlwExecute execute)
    {
        var sql = Convert.ToString(_evaluator.Evaluate(execute.SqlExpression, _env), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(sql))
            throw new WalhallaException("EXECUTE erwartet einen nicht-leeren SQL-String.");

        var args = execute.UsingArguments.Select(a => _evaluator.Evaluate(a, _env)).ToArray();
        var result = _executor.Execute(sql, args);

        if (execute.IntoTargets.Count == 0)
            return;

        var targets = execute.IntoTargets
            .Select(t => t as PlwIdentifierExpression ?? throw new WalhallaException("EXECUTE INTO erwartet einfache Variablen als Ziele."))
            .ToArray();

        if (result.Rows.Count == 0)
        {
            foreach (var target in targets)
                _env.Set(target.Name, null);
            return;
        }

        if (result.Rows.Count > 1)
        {
            throw new WalhallaException(
                $"EXECUTE INTO lieferte {result.Rows.Count} Zeilen zurueck, erwartet wurde hoechstens eine.",
                "P0003");
        }

        _env.SetFound(result.Rows.Count > 0);

        var row = result.Rows[0];
        for (var i = 0; i < Math.Min(targets.Length, result.ColumnNames.Count); i++)
        {
            _env.Set(targets[i].Name, row.GetValue(i));
        }
    }

    private void SetFoundFromResult(WalhallaResultSet result)
    {
        bool found = result.AffectedRows > 0 || result.Rows.Count > 0;
        _env.SetFound(found);
    }

    private void ExecuteRaise(PlwRaise raise)
    {
        var level = raise.Level.ToUpperInvariant();
        var message = raise.Message != null
            ? Convert.ToString(_evaluator.Evaluate(raise.Message, _env), CultureInfo.InvariantCulture)
            : string.Empty;

        message = FormatRaiseMessage(message, raise.Arguments);

        if (level == "EXCEPTION")
        {
            var sqlState = raise.SqlStateExpression != null
                ? Convert.ToString(_evaluator.Evaluate(raise.SqlStateExpression, _env), CultureInfo.InvariantCulture)
                : "P0001";
            throw new WalhallaException(message, sqlState);
        }

        // NOTICE/INFO/WARNING werden vorerst als Diagnose-Ausgabe behandelt.
        System.Diagnostics.Debug.WriteLine($"[PLW {level}] {message}");
    }

    private string FormatRaiseMessage(string message, IReadOnlyList<PlwExpression> arguments)
    {
        if (arguments.Count == 0)
            return message;

        var sb = new System.Text.StringBuilder(message.Length + arguments.Count * 8);
        int argIndex = 0;
        for (int i = 0; i < message.Length; i++)
        {
            var c = message[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }

            if (i + 1 < message.Length && message[i + 1] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }

            if (argIndex >= arguments.Count)
                throw new WalhallaException("Zu wenige Argumente fuer Format-Platzhalter in RAISE.");

            var value = _evaluator.Evaluate(arguments[argIndex], _env);
            sb.Append(FormatRaiseArgument(value));
            argIndex++;
        }

        return sb.ToString();
    }

    private static string FormatRaiseArgument(object? value)
    {
        if (value == null)
            return "NULL";

        if (value is bool b)
            return b ? "true" : "false";

        if (value is DateTime dt)
            return dt.ToString("O", CultureInfo.InvariantCulture);

        if (value is DateOnly dateOnly)
            return dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (value is TimeSpan timeSpan)
            return timeSpan.ToString("c", CultureInfo.InvariantCulture);

        if (value is TimeOnly timeOnly)
            return timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        if (value is Guid guid)
            return guid.ToString();

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void Assign(PlwExpression target, object? value)
    {
        if (target is PlwIdentifierExpression id)
        {
            _env.Set(id.Name, value);
            return;
        }

        if (target is PlwBinaryExpression { Operator: PlwTokenKind.Dot } dotted
            && dotted.Left is PlwIdentifierExpression record
            && dotted.Right is PlwIdentifierExpression member)
        {
            var container = _env.Get(record.Name);
            if (container is IDictionary<string, object?> dict)
            {
                dict[member.Name] = value;
                return;
            }
        }

        throw new WalhallaException("Ungueltiges Zuweisungsziel im PLW-Interpreter.");
    }

    private static object? ParseLiteral(string? expression, SqlScalarType type)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var trimmed = expression.Trim();
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (type == SqlScalarType.Boolean)
            return ParseBoolean(trimmed);

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            var unquoted = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return type switch
            {
                SqlScalarType.DateTime => DateTime.Parse(unquoted, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                SqlScalarType.Date => DateOnly.Parse(unquoted, CultureInfo.InvariantCulture),
                SqlScalarType.Time => TimeSpan.Parse(unquoted, CultureInfo.InvariantCulture),
                SqlScalarType.Guid => Guid.Parse(unquoted),
                _ => unquoted
            };
        }

        return type switch
        {
            SqlScalarType.Int32 => int.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Int64 => long.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Int16 => short.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Double => double.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Decimal => decimal.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Boolean => ParseBoolean(trimmed),
            SqlScalarType.DateTime => DateTime.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Date => DateOnly.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Time => TimeSpan.Parse(trimmed, CultureInfo.InvariantCulture),
            SqlScalarType.Guid => Guid.Parse(trimmed),
            _ => InferLiteral(trimmed)
        };
    }

    private static object? InferLiteral(string text)
    {
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;

        if (ParseBoolean(text) is bool b)
            return b;

        return text;
    }

    private static bool? ParseBoolean(string text)
    {
        if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text == "1")
            return true;
        if (text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || text == "0")
            return false;
        return bool.TryParse(text, out var result) ? result : null;
    }

    private static string TypeNameFromScalarType(SqlScalarType type)
        => type switch
        {
            SqlScalarType.Int32 => "INT",
            SqlScalarType.Int64 => "BIGINT",
            SqlScalarType.Int16 => "SMALLINT",
            SqlScalarType.Double => "DOUBLE",
            SqlScalarType.Decimal => "DECIMAL",
            SqlScalarType.String => "STRING",
            SqlScalarType.Boolean => "BOOLEAN",
            SqlScalarType.DateTime => "DATETIME",
            SqlScalarType.Date => "DATE",
            SqlScalarType.Time => "TIME",
            SqlScalarType.Guid => "GUID",
            SqlScalarType.Binary => "BINARY",
            SqlScalarType.Json => "JSON",
            _ => "STRING"
        };
}
