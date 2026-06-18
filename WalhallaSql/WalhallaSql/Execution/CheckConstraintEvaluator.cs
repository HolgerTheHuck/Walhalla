using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

/// <summary>
/// Evaluates CHECK-constraint predicates against table rows using SQL three-valued logic.
/// A row violates a constraint only when its predicate evaluates to <c>FALSE</c>; both
/// <c>TRUE</c> and <c>UNKNOWN</c> (NULL) satisfy the constraint, per the SQL standard.
/// </summary>
internal static class CheckConstraintEvaluator
{
    private static readonly ConditionalWeakTable<IReadOnlyList<SqlCheckConstraint>, ParsedCheck[]> _cache = new();

    private sealed record ParsedCheck(string Name, string ExpressionText, SqlWhereExpression Ast);

    /// <summary>
    /// Validates that every CHECK expression in <paramref name="checks"/> parses and references
    /// only supported constructs and existing columns. Throws on any problem (used at DDL time).
    /// </summary>
    public static void Validate(IReadOnlyList<SqlCheckConstraint> checks, SqlTableDefinition table)
    {
        _ = GetParsed(checks, table);
    }

    /// <summary>
    /// Throws a <see cref="WalhallaException"/> (SQLSTATE 23514) if <paramref name="row"/> violates
    /// any CHECK constraint declared on <paramref name="table"/>.
    /// </summary>
    public static void Enforce(SqlTableDefinition table, object?[] row)
    {
        var checks = table.CheckConstraints;
        if (checks == null || checks.Count == 0) return;

        var parsed = GetParsed(checks, table);
        foreach (var check in parsed)
        {
            var result = Evaluate(check.Ast, table, row);
            if (result == false)
                throw new WalhallaConstraintException(
                    $"CHECK constraint '{check.Name}' violated by row.", "23514");
        }
    }

    private static ParsedCheck[] GetParsed(IReadOnlyList<SqlCheckConstraint> checks, SqlTableDefinition table)
    {
        if (_cache.TryGetValue(checks, out var cached))
            return cached;

        var parsed = new ParsedCheck[checks.Count];
        for (var i = 0; i < checks.Count; i++)
        {
            var ast = SqlWhereParser.Parse(checks[i].Expression);
            ValidateSupported(ast, table, checks[i]);
            parsed[i] = new ParsedCheck(checks[i].Name, checks[i].Expression, ast);
        }

        _cache.AddOrUpdate(checks, parsed);
        return parsed;
    }

    // ── Validation (DDL-time fast fail) ───────────────────────────────────────

    private static void ValidateSupported(SqlWhereExpression expr, SqlTableDefinition table, SqlCheckConstraint check)
    {
        switch (expr)
        {
            case SqlWhereAndExpression and:
                foreach (var c in and.Children) ValidateSupported(c, table, check);
                break;
            case SqlWhereOrExpression or:
                foreach (var c in or.Children) ValidateSupported(c, table, check);
                break;
            case SqlWhereNotExpression not:
                ValidateSupported(not.Inner, table, check);
                break;
            case SqlWhereTruthyExpression truthy:
                ValidateValue(truthy.Value, table, check);
                break;
            case SqlWhereComparisonExpression cmp:
                ValidateValue(cmp.Left, table, check);
                ValidateValue(cmp.Right, table, check);
                break;
            case SqlWhereInListExpression inList:
                ValidateValue(inList.Left, table, check);
                foreach (var v in inList.Values) ValidateValue(v, table, check);
                break;
            case SqlWhereLikeExpression like:
                ValidateValue(like.Left, table, check);
                ValidateValue(like.Pattern, table, check);
                break;
            case SqlWhereBetweenExpression between:
                ValidateValue(between.Value, table, check);
                ValidateValue(between.Lower, table, check);
                ValidateValue(between.Upper, table, check);
                break;
            case SqlWhereNullCheckExpression nullCheck:
                ValidateValue(nullCheck.Value, table, check);
                break;
            case SqlWhereDistinctFromExpression distinct:
                ValidateValue(distinct.Left, table, check);
                ValidateValue(distinct.Right, table, check);
                break;
            default:
                throw Unsupported(check, expr.GetType().Name);
        }
    }

    private static void ValidateValue(SqlWhereValueExpression value, SqlTableDefinition table, SqlCheckConstraint check)
    {
        switch (value)
        {
            case SqlWhereColumnExpression col:
                if (FindColumnIndex(table, col.SimpleName) < 0)
                    throw new WalhallaConstraintException(
                        $"CHECK constraint '{check.Name}' references unknown column '{col.SimpleName}'.");
                break;
            case SqlWhereLiteralExpression:
                break;
            case SqlWhereUnaryValueExpression unary:
                ValidateValue(unary.Operand, table, check);
                break;
            case SqlWhereBinaryValueExpression binary:
                ValidateValue(binary.Left, table, check);
                ValidateValue(binary.Right, table, check);
                break;
            case SqlWhereCastExpression cast:
                ValidateValue(cast.Inner, table, check);
                break;
            case SqlWhereFunctionCallExpression fn:
                if (!IsSupportedFunction(fn.FunctionName))
                    throw Unsupported(check, $"function '{fn.FunctionName}'");
                foreach (var a in fn.Arguments) ValidateValue(a, table, check);
                break;
            default:
                throw Unsupported(check, value.GetType().Name);
        }
    }

    private static WalhallaException Unsupported(SqlCheckConstraint check, string what)
        => new($"CHECK constraint '{check.Name}' uses an unsupported construct: {what}.");

    // ── Three-valued predicate evaluation ─────────────────────────────────────

    private static bool? Evaluate(SqlWhereExpression expr, SqlTableDefinition table, object?[] row)
    {
        switch (expr)
        {
            case SqlWhereAndExpression and:
            {
                var sawNull = false;
                foreach (var c in and.Children)
                {
                    var r = Evaluate(c, table, row);
                    if (r == false) return false;
                    if (r == null) sawNull = true;
                }
                return sawNull ? null : true;
            }
            case SqlWhereOrExpression or:
            {
                var sawNull = false;
                foreach (var c in or.Children)
                {
                    var r = Evaluate(c, table, row);
                    if (r == true) return true;
                    if (r == null) sawNull = true;
                }
                return sawNull ? null : false;
            }
            case SqlWhereNotExpression not:
            {
                var r = Evaluate(not.Inner, table, row);
                return r == null ? null : !r;
            }
            case SqlWhereTruthyExpression truthy:
            {
                var v = EvaluateValue(truthy.Value, table, row);
                if (v == null) return null;
                if (v is bool b) return b;
                return ToDouble(v) != 0d;
            }
            case SqlWhereComparisonExpression cmp:
            {
                var left = EvaluateValue(cmp.Left, table, row);
                var right = EvaluateValue(cmp.Right, table, row);
                if (left == null || right == null) return null;
                return WhereCompiler.CompareValues(left, right, cmp.Operator);
            }
            case SqlWhereInListExpression inList:
            {
                var left = EvaluateValue(inList.Left, table, row);
                if (left == null) return null;
                var sawNull = false;
                var matched = false;
                foreach (var ve in inList.Values)
                {
                    var v = EvaluateValue(ve, table, row);
                    if (v == null) { sawNull = true; continue; }
                    if (WhereCompiler.CompareValues(left, v, SqlWhereComparisonOperator.Equal))
                    {
                        matched = true;
                        break;
                    }
                }
                bool? result = matched ? true : (sawNull ? (bool?)null : false);
                if (result == null) return null;
                return inList.Negated ? !result : result;
            }
            case SqlWhereLikeExpression like:
            {
                var left = EvaluateValue(like.Left, table, row);
                var pattern = EvaluateValue(like.Pattern, table, row);
                if (left == null || pattern == null) return null;
                var match = LikeMatch(ToStringValue(left), ToStringValue(pattern));
                return like.Negated ? !match : match;
            }
            case SqlWhereBetweenExpression between:
            {
                var value = EvaluateValue(between.Value, table, row);
                var lower = EvaluateValue(between.Lower, table, row);
                var upper = EvaluateValue(between.Upper, table, row);
                if (value == null) return null;
                bool? geLower = lower == null
                    ? null
                    : WhereCompiler.CompareValues(value, lower, SqlWhereComparisonOperator.GreaterThanOrEqual);
                bool? leUpper = upper == null
                    ? null
                    : WhereCompiler.CompareValues(value, upper, SqlWhereComparisonOperator.LessThanOrEqual);
                var inRange = And3(geLower, leUpper);
                if (inRange == null) return null;
                return between.Negated ? !inRange : inRange;
            }
            case SqlWhereNullCheckExpression nullCheck:
            {
                var v = EvaluateValue(nullCheck.Value, table, row);
                var isNull = v == null;
                return nullCheck.Negated ? !isNull : isNull;
            }
            case SqlWhereDistinctFromExpression distinct:
            {
                var left = EvaluateValue(distinct.Left, table, row);
                var right = EvaluateValue(distinct.Right, table, row);
                bool isDistinct;
                if (left == null && right == null) isDistinct = false;
                else if (left == null || right == null) isDistinct = true;
                else isDistinct = !WhereCompiler.CompareValues(left, right, SqlWhereComparisonOperator.Equal);
                return distinct.Negated ? !isDistinct : isDistinct;
            }
            default:
                throw new WalhallaConstraintException(
                    $"CHECK constraint uses an unsupported construct: {expr.GetType().Name}.");
        }
    }

    private static bool? And3(bool? a, bool? b)
    {
        if (a == false || b == false) return false;
        if (a == null || b == null) return null;
        return true;
    }

    // ── Value evaluation ──────────────────────────────────────────────────────

    private static object? EvaluateValue(SqlWhereValueExpression value, SqlTableDefinition table, object?[] row)
    {
        switch (value)
        {
            case SqlWhereColumnExpression col:
            {
                var idx = FindColumnIndex(table, col.SimpleName);
                if (idx < 0)
                    throw new WalhallaException($"CHECK references unknown column '{col.SimpleName}'.");
                return row[idx];
            }
            case SqlWhereLiteralExpression lit:
                return lit.Value;
            case SqlWhereUnaryValueExpression unary:
            {
                var operand = EvaluateValue(unary.Operand, table, row);
                if (operand == null) return null;
                return unary.Operator switch
                {
                    SqlWhereUnaryOperator.Plus => operand,
                    SqlWhereUnaryOperator.Minus => Negate(operand),
                    SqlWhereUnaryOperator.BitwiseNot => ~ToInt64(operand),
                    _ => operand
                };
            }
            case SqlWhereBinaryValueExpression binary:
            {
                var left = EvaluateValue(binary.Left, table, row);
                var right = EvaluateValue(binary.Right, table, row);
                if (left == null || right == null) return null;
                return Arithmetic(left, right, binary.Operator);
            }
            case SqlWhereCastExpression cast:
            {
                var inner = EvaluateValue(cast.Inner, table, row);
                if (inner == null) return null;
                return ApplyCast(inner, cast.TargetType);
            }
            case SqlWhereFunctionCallExpression fn:
                return EvaluateFunction(fn, table, row);
            default:
                throw new WalhallaConstraintException(
                    $"CHECK uses an unsupported value expression: {value.GetType().Name}.");
        }
    }

    private static bool IsSupportedFunction(string name) => name.ToUpperInvariant() switch
    {
        "LENGTH" or "CHAR_LENGTH" or "LOWER" or "UPPER" or "ABS" or "TRIM" => true,
        _ => false
    };

    private static object? EvaluateFunction(SqlWhereFunctionCallExpression fn, SqlTableDefinition table, object?[] row)
    {
        var name = fn.FunctionName.ToUpperInvariant();
        var args = new object?[fn.Arguments.Count];
        for (var i = 0; i < args.Length; i++)
            args[i] = EvaluateValue(fn.Arguments[i], table, row);

        switch (name)
        {
            case "LENGTH":
            case "CHAR_LENGTH":
                return args.Length == 1 && args[0] != null ? (long)ToStringValue(args[0]!).Length : (object?)null;
            case "LOWER":
                return args.Length == 1 && args[0] != null ? ToStringValue(args[0]!).ToLowerInvariant() : (object?)null;
            case "UPPER":
                return args.Length == 1 && args[0] != null ? ToStringValue(args[0]!).ToUpperInvariant() : (object?)null;
            case "TRIM":
                return args.Length == 1 && args[0] != null ? ToStringValue(args[0]!).Trim() : (object?)null;
            case "ABS":
                return args.Length == 1 && args[0] != null ? Math.Abs(ToDouble(args[0]!)) : (object?)null;
            default:
                throw new WalhallaConstraintException($"CHECK uses an unsupported function '{fn.FunctionName}'.");
        }
    }

    // ── Numeric & string helpers ──────────────────────────────────────────────

    private static object Negate(object v) => v switch
    {
        long l => -l,
        int i => -(long)i,
        short s => -(long)s,
        double d => -d,
        decimal m => -m,
        _ => -ToDouble(v)
    };

    private static object Arithmetic(object left, object right, SqlWhereBinaryOperator op)
    {
        if (left is double || right is double || left is float || right is float)
        {
            var a = ToDouble(left);
            var b = ToDouble(right);
            return op switch
            {
                SqlWhereBinaryOperator.Add => a + b,
                SqlWhereBinaryOperator.Subtract => a - b,
                SqlWhereBinaryOperator.Multiply => a * b,
                SqlWhereBinaryOperator.Divide => a / b,
                SqlWhereBinaryOperator.Modulo => a % b,
                _ => throw new WalhallaException("Unsupported arithmetic operator in CHECK.")
            };
        }

        if (left is decimal || right is decimal)
        {
            var a = ToDecimal(left);
            var b = ToDecimal(right);
            return op switch
            {
                SqlWhereBinaryOperator.Add => a + b,
                SqlWhereBinaryOperator.Subtract => a - b,
                SqlWhereBinaryOperator.Multiply => a * b,
                SqlWhereBinaryOperator.Divide => a / b,
                SqlWhereBinaryOperator.Modulo => a % b,
                _ => throw new WalhallaException("Unsupported arithmetic operator in CHECK.")
            };
        }

        var x = ToInt64(left);
        var y = ToInt64(right);
        return op switch
        {
            SqlWhereBinaryOperator.Add => x + y,
            SqlWhereBinaryOperator.Subtract => x - y,
            SqlWhereBinaryOperator.Multiply => x * y,
            SqlWhereBinaryOperator.Divide => x / y,
            SqlWhereBinaryOperator.Modulo => x % y,
            _ => throw new WalhallaException("Unsupported arithmetic operator in CHECK.")
        };
    }

    private static object ApplyCast(object value, SqlScalarType target) => target switch
    {
        SqlScalarType.Int16 => (short)ToInt64(value),
        SqlScalarType.Int32 => (int)ToInt64(value),
        SqlScalarType.Int64 => ToInt64(value),
        SqlScalarType.Double => ToDouble(value),
        SqlScalarType.Decimal => ToDecimal(value),
        SqlScalarType.String => ToStringValue(value),
        SqlScalarType.Boolean => value is bool b ? b : ToDouble(value) != 0d,
        _ => value
    };

    private static double ToDouble(object v) => v switch
    {
        long l => l,
        int i => i,
        short s => s,
        double d => d,
        float f => f,
        decimal m => (double)m,
        bool b => b ? 1d : 0d,
        string s => double.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture)
    };

    private static decimal ToDecimal(object v) => v switch
    {
        long l => l,
        int i => i,
        short s => s,
        double d => (decimal)d,
        float f => (decimal)f,
        decimal m => m,
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture)
    };

    private static long ToInt64(object v) => v switch
    {
        long l => l,
        int i => i,
        short s => s,
        double d => (long)d,
        float f => (long)f,
        decimal m => (long)m,
        bool b => b ? 1L : 0L,
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture)
    };

    private static string ToStringValue(object v) => v?.ToString() ?? string.Empty;

    private static int FindColumnIndex(SqlTableDefinition table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static bool LikeMatch(string value, string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '%': sb.Append(".*"); break;
                case '_': sb.Append('.'); break;
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        sb.Append('$');
        return Regex.IsMatch(value, sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
