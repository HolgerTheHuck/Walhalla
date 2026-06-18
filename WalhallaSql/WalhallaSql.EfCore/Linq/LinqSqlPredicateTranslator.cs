using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore.Linq;

internal static class LinqSqlPredicateTranslator
{
    public static string Translate(LambdaExpression predicate)
    {
        return Translate(predicate, entityType: null);
    }

    public static string Translate(LambdaExpression predicate, IEntityType? entityType)
    {
        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        return TranslateExpression(predicate.Body, entityType);
    }

    private static string TranslateExpression(Expression expression, IEntityType? entityType)
    {
        expression = Unwrap(expression);

        if (expression is UnaryExpression notExpr && notExpr.NodeType == ExpressionType.Not)
            return $"NOT ({TranslateExpression(notExpr.Operand, entityType)})";

        if (expression is MethodCallExpression methodCall)
            return TranslateMethodCall(methodCall, entityType);

        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso)
                return $"({TranslateExpression(binary.Left, entityType)} AND {TranslateExpression(binary.Right, entityType)})";

            if (binary.NodeType == ExpressionType.OrElse)
                return $"({TranslateExpression(binary.Left, entityType)} OR {TranslateExpression(binary.Right, entityType)})";

            if (!TryGetColumnAndValue(binary.Left, binary.Right, entityType, out var column, out var value, out var property, out var swapped))
                throw LinqGuardrail.PredicateMemberToConstantOnly();

            var op = swapped ? SwapOperator(binary.NodeType) : binary.NodeType;
            if (value == null)
            {
                return op switch
                {
                    ExpressionType.Equal => $"{column} IS NULL",
                    ExpressionType.NotEqual => $"{column} IS NOT NULL",
                    _ => throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.PredicateOperatorUnsupported, $"Operator '{op}' is not supported for NULL comparisons.")
                };
            }

            var sqlOp = ToSqlOperator(op);
            return $"{column} {sqlOp} {WalhallaSqlEfCoreSqlRenderer.FormatProviderSqlLiteral(value, property)}";
        }

        throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.PredicateExpressionNode, $"Expression '{expression.NodeType}' is not supported.");
    }

    private static string TranslateMethodCall(MethodCallExpression methodCall, IEntityType? entityType)
    {
        if (methodCall.Method.Name == nameof(string.StartsWith) &&
            methodCall.Object != null &&
            methodCall.Object.Type == typeof(string) &&
            methodCall.Arguments.Count == 1 &&
            TryGetColumn(methodCall.Object, entityType, out var startsWithColumn, out _) &&
            TryGetValue(methodCall.Arguments[0], out var startsWithRaw) &&
            startsWithRaw != null)
        {
            var prefix = Convert.ToString(startsWithRaw, CultureInfo.InvariantCulture) ?? string.Empty;
            var escaped = prefix
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal)
                .Replace("'", "''", StringComparison.Ordinal);

            return $"{startsWithColumn} LIKE '{escaped}%'";
        }

        if (methodCall.Method.Name == nameof(Enumerable.Contains))
        {
            Expression sequenceExpr;
            Expression itemExpr;

            if (methodCall.Object == null && methodCall.Arguments.Count == 2)
            {
                sequenceExpr = methodCall.Arguments[0];
                itemExpr = methodCall.Arguments[1];
            }
            else if (methodCall.Object != null && methodCall.Arguments.Count == 1)
            {
                sequenceExpr = methodCall.Object;
                itemExpr = methodCall.Arguments[0];
            }
            else
            {
                throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.PredicateContainsSignature, "Contains signature is not supported.");
            }

            if (!TryGetColumn(itemExpr, entityType, out var column, out var property))
                throw LinqGuardrail.PredicateContainsColumnMembershipOnly();

            if (!TryExtractEnumerableValues(sequenceExpr, property, out var values))
                throw LinqGuardrail.PredicateContainsRequiresEnumerableSource();

            if (values.Length == 0)
                return "(1 = 0)";

            return $"{column} IN ({string.Join(", ", values.Select(value => WalhallaSqlEfCoreSqlRenderer.FormatProviderSqlLiteral(value, property)))})";
        }

        throw LinqGuardrail.PredicateMethodNotSupported(methodCall.Method.Name);
    }

    private static bool TryExtractEnumerableValues(Expression expression, IProperty? property, out object?[] values)
    {
        expression = Unwrap(expression);

        expression = NormalizeSequenceExpression(expression);

        if (expression is NewArrayExpression newArray)
        {
            values = new object?[newArray.Expressions.Count];
            for (var i = 0; i < newArray.Expressions.Count; i++)
                values[i] = WalhallaSqlEfCoreSqlRenderer.ToProviderValue(EvaluateExpressionValue(newArray.Expressions[i]), property);

            return true;
        }

        if (TryGetValue(expression, out var raw) && raw is IEnumerable enumerable)
        {
            values = enumerable.Cast<object?>().Select(value => WalhallaSqlEfCoreSqlRenderer.ToProviderValue(value, property)).ToArray();
            return true;
        }

        values = Array.Empty<object?>();
        return false;
    }

    private static Expression NormalizeSequenceExpression(Expression expression)
    {
        expression = Unwrap(expression);

        while (expression is MethodCallExpression methodCall)
        {
            if (methodCall.Method.Name == "AsSpan" && methodCall.Arguments.Count >= 1)
            {
                expression = Unwrap(methodCall.Arguments[0]);
                continue;
            }

            if ((methodCall.Type.FullName?.StartsWith("System.ReadOnlySpan", StringComparison.Ordinal) ?? false) &&
                methodCall.Arguments.Count >= 1)
            {
                expression = Unwrap(methodCall.Arguments[0]);
                continue;
            }

            break;
        }

        return expression;
    }

    private static bool TryGetColumnAndValue(Expression left, Expression right, IEntityType? entityType, out string column, out object? value, out IProperty? property, out bool swapped)
    {
        if (TryGetColumn(left, entityType, out column, out var resolvedProperty) && TryGetValue(right, out value))
        {
            value = WalhallaSqlEfCoreSqlRenderer.ToProviderValue(value, resolvedProperty);
            property = resolvedProperty;
            swapped = false;
            return true;
        }

        if (TryGetColumn(right, entityType, out column, out resolvedProperty) && TryGetValue(left, out value))
        {
            value = WalhallaSqlEfCoreSqlRenderer.ToProviderValue(value, resolvedProperty);
            property = resolvedProperty;
            swapped = true;
            return true;
        }

        column = string.Empty;
        value = null;
        property = null;
        swapped = false;
        return false;
    }

    private static bool TryGetColumn(Expression expression, IEntityType? entityType, out string column, out IProperty? property)
    {
        expression = Unwrap(expression);

        if (expression is MemberExpression member && member.Expression is ParameterExpression)
        {
            property = entityType?.FindProperty(member.Member.Name);
            column = property == null ? member.Member.Name : GetColumnName(property);
            return true;
        }

        column = string.Empty;
        property = null;
        return false;
    }

    private static bool TryGetValue(Expression expression, out object? value)
    {
        expression = Unwrap(expression);
        try
        {
            value = EvaluateExpressionValue(expression);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static object? EvaluateExpressionValue(Expression expression)
    {
        expression = Unwrap(expression);

        if (expression is ConstantExpression constant)
            return constant.Value;

        if (expression is MemberExpression member)
        {
            var instance = member.Expression == null ? null : EvaluateExpressionValue(member.Expression);

            if (member.Member is System.Reflection.FieldInfo field)
                return field.GetValue(instance);

            if (member.Member is System.Reflection.PropertyInfo property)
                return property.GetValue(instance);
        }

        if (expression is NewArrayExpression newArray)
        {
            var values = new object?[newArray.Expressions.Count];
            for (var i = 0; i < newArray.Expressions.Count; i++)
                values[i] = EvaluateExpressionValue(newArray.Expressions[i]);

            return values;
        }

        var lambda = Expression.Lambda(expression);
        return lambda.Compile().DynamicInvoke();
    }

    private static string ToSqlOperator(ExpressionType expressionType)
    {
        return expressionType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw LinqGuardrail.NotSupported(LinqGuardrail.Codes.PredicateOperatorUnsupported, $"Operator '{expressionType}' is not supported.")
        };
    }

    private static ExpressionType SwapOperator(ExpressionType expressionType)
    {
        return expressionType switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => expressionType
        };
    }

    private static string GetColumnName(IProperty property)
    {
        var declaringEntityType = property.DeclaringType as IEntityType;
        var tableName = declaringEntityType?.GetTableName();
        if (!string.IsNullOrEmpty(tableName))
        {
            var storeObject = StoreObjectIdentifier.Table(tableName, declaringEntityType!.GetSchema());
            var relationalName = property.GetColumnName(storeObject);
            if (!string.IsNullOrEmpty(relationalName))
                return relationalName;
        }

        var columnName = property.FindAnnotation("Relational:ColumnName")?.Value as string;
        return string.IsNullOrEmpty(columnName) ? property.Name : columnName;
    }


    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
               (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unary.Operand;
        }

        return expression;
    }
}
