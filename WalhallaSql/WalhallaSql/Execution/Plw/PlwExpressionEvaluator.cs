using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WalhallaSql.Parsing.Plw;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Wertet Ausdruecke im PLW-AST aus. Unterstuetzt arithmetische, logische und
/// Vergleichsoperatoren, String-Konkatenation, eingebaute Funktionen und Record-Zugriffe.
/// </summary>
internal sealed class PlwExpressionEvaluator
{
    public object? Evaluate(PlwExpression? expression, PlwEnvironment env)
    {
        if (expression == null)
            return null;

        return expression switch
        {
            PlwIdentifierExpression id => env.Get(id.Name),
            PlwNumberExpression num => ParseNumber(num.Text),
            PlwStringExpression str => str.Value,
            PlwBooleanExpression b => b.Value,
            PlwNullExpression => null,
            PlwParameterReference pr => env.Get($"__param{pr.Index}"),
            PlwFieldAccessExpression fa => EvaluateFieldAccess(fa, env),
            PlwUnaryExpression u => EvaluateUnary(u, env),
            PlwBinaryExpression b => EvaluateBinary(b, env),
            _ => throw new WalhallaException($"Nicht unterstuetzter PLW-Ausdruck: {expression.GetType().Name}")
        };
    }

    private static object? ParseNumber(string text)
    {
        if (text.Contains('.') || text.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;

        return text;
    }

    private object? EvaluateUnary(PlwUnaryExpression unary, PlwEnvironment env)
    {
        var operand = Evaluate(unary.Operand, env);

        return unary.Operator switch
        {
            PlwTokenKind.Not => !ToBoolean(operand),
            PlwTokenKind.Minus => ApplyNumericUnary(operand, x => -x),
            _ => throw new WalhallaException($"Unaerer Operator '{unary.Operator}' nicht unterstuetzt.")
        };
    }

    private object? EvaluateBinary(PlwBinaryExpression binary, PlwEnvironment env)
    {
        if (binary.Operator == PlwTokenKind.LeftParen)
            return EvaluateFunctionCall(binary, env);

        if (binary.Operator == PlwTokenKind.Dot)
            return EvaluateMemberAccess(binary, env);

        var left = Evaluate(binary.Left, env);
        var right = Evaluate(binary.Right, env);

        return binary.Operator switch
        {
            PlwTokenKind.Plus => ApplyPlus(left, right),
            PlwTokenKind.Minus => ApplyNumeric(left, right, (a, b) => a - b),
            PlwTokenKind.Star => ApplyNumeric(left, right, (a, b) => a * b),
            PlwTokenKind.Slash => ApplyNumeric(left, right, (a, b) => a / b),
            PlwTokenKind.Percent => ApplyNumeric(left, right, (a, b) => a % b),
            PlwTokenKind.Concat => string.Concat(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture)),

            PlwTokenKind.Equals => AreEqual(left, right),
            PlwTokenKind.NotEquals => !AreEqual(left, right),
            PlwTokenKind.LessThan => Compare(left, right) < 0,
            PlwTokenKind.GreaterThan => Compare(left, right) > 0,
            PlwTokenKind.LessEquals => Compare(left, right) <= 0,
            PlwTokenKind.GreaterEquals => Compare(left, right) >= 0,

            PlwTokenKind.And => ToBoolean(left) && ToBoolean(right),
            PlwTokenKind.Or => ToBoolean(left) || ToBoolean(right),

            _ => throw new WalhallaException($"Binaerer Operator '{binary.Operator}' nicht unterstuetzt.")
        };
    }

    private object? EvaluateFunctionCall(PlwBinaryExpression call, PlwEnvironment env)
    {
        if (call.Left is not PlwIdentifierExpression functionName)
            throw new WalhallaException("Funktionsaufruf erwartet einen Bezeichner links vom Klammerpaar.");

        var name = functionName.Name;
        var args = ExtractArguments(call.Right, env);

        return name.ToUpperInvariant() switch
        {
            "QUOTE_IDENT" => QuoteIdentifier(SingleStringArg(args, name)),
            "QUOTE_LITERAL" => QuoteLiteral(SingleArg(args, name)),
            "COALESCE" => args.FirstOrDefault(a => a != null),
            "LOWER" => SingleStringArg(args, name)?.ToLowerInvariant(),
            "UPPER" => SingleStringArg(args, name)?.ToUpperInvariant(),
            _ => throw new WalhallaException($"Unbekannte PLW-Funktion '{name}'.")
        };
    }

    private object? EvaluateFieldAccess(PlwFieldAccessExpression access, PlwEnvironment env)
    {
        var record = Evaluate(access.Record, env);
        return AccessMember(record, access.FieldName);
    }

    private object? EvaluateMemberAccess(PlwBinaryExpression access, PlwEnvironment env)
    {
        var left = Evaluate(access.Left, env);
        if (access.Right is not PlwIdentifierExpression member)
            throw new WalhallaException("Record-Zugriff erwartet einen Bezeichner rechts vom Punkt.");

        return AccessMember(left, member.Name);
    }

    private IReadOnlyList<object?> ExtractArguments(PlwExpression argsNode, PlwEnvironment env)
    {
        if (argsNode is PlwSqlFragment { Arguments.Count: 0 })
            return Array.Empty<object?>();

        if (argsNode is PlwSqlFragment frag)
            return frag.Arguments.Select(a => Evaluate(a, env)).ToArray();

        return new[] { Evaluate(argsNode, env) };
    }

    private static object? SingleArg(IReadOnlyList<object?> args, string functionName)
    {
        if (args.Count != 1)
            throw new WalhallaException($"Funktion '{functionName}' erwartet genau ein Argument.");
        return args[0];
    }

    private static string? SingleStringArg(IReadOnlyList<object?> args, string functionName)
    {
        var value = SingleArg(args, functionName);
        return value?.ToString();
    }

    private static string QuoteIdentifier(string? value)
    {
        if (value == null)
            return "\"\"";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string? QuoteLiteral(object? value)
    {
        if (value == null)
            return "NULL";
        return "'" + value.ToString()!.Replace("'", "''") + "'";
    }

    internal static object? AccessMember(object? container, string memberName)
    {
        if (container is IReadOnlyDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, memberName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        if (container is WalhallaRow row && row.TryGetValue(memberName, out var value))
            return value;

        return null;
    }

    private static object? ApplyPlus(object? left, object? right)
    {
        if (left is string || right is string)
            return string.Concat(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture));

        return ApplyNumeric(left, right, (a, b) => a + b);
    }

    private static object? ApplyNumeric(object? left, object? right, Func<double, double, double> op)
    {
        var l = Convert.ToDouble(left, CultureInfo.InvariantCulture);
        var r = Convert.ToDouble(right, CultureInfo.InvariantCulture);
        var result = op(l, r);

        if (left is int && right is int && result == Math.Truncate(result))
        {
            if (result >= int.MinValue && result <= int.MaxValue)
                return (int)result;

            if (result >= long.MinValue && result <= long.MaxValue)
                return (long)result;
        }

        return result;
    }

    private static object? ApplyNumericUnary(object? operand, Func<double, double> op)
    {
        var value = Convert.ToDouble(operand, CultureInfo.InvariantCulture);
        var result = op(value);

        if (operand is int && result == Math.Truncate(result))
        {
            if (result >= int.MinValue && result <= int.MaxValue)
                return (int)result;

            if (result >= long.MinValue && result <= long.MaxValue)
                return (long)result;
        }

        return result;
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        if (left is bool || right is bool)
            return ToBoolean(left) == ToBoolean(right);

        if (IsNumericObject(left) && IsNumericObject(right))
            return Math.Abs(Convert.ToDouble(left, CultureInfo.InvariantCulture) - Convert.ToDouble(right, CultureInfo.InvariantCulture)) < 1e-10;

        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture),
                             Convert.ToString(right, CultureInfo.InvariantCulture),
                             StringComparison.Ordinal);
    }

    private static int Compare(object? left, object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        if (IsNumericObject(left) && IsNumericObject(right))
        {
            var ld = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var rd = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            return ld.CompareTo(rd);
        }

        var ls = Convert.ToString(left, CultureInfo.InvariantCulture);
        var rs = Convert.ToString(right, CultureInfo.InvariantCulture);
        return string.CompareOrdinal(ls, rs);
    }

    internal static bool ToBoolean(object? value)
    {
        if (value == null)
            return false;

        if (value is bool b)
            return b;

        if (value is string s)
            return s.Length > 0
                && !s.Equals("false", StringComparison.OrdinalIgnoreCase)
                && !s.Equals("0", StringComparison.OrdinalIgnoreCase);

        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsNumericObject(object value)
        => value is byte or sbyte or short or ushort or int or uint
               or long or ulong or float or double or decimal;
}
