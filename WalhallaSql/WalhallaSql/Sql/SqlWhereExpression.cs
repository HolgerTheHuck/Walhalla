using System;
using System.Collections.Generic;

namespace WalhallaSql.Sql;

public enum SqlWhereComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

public abstract record SqlWhereExpression;

public sealed record SqlWhereAndExpression(IReadOnlyList<SqlWhereExpression> Children) : SqlWhereExpression;

public sealed record SqlWhereOrExpression(IReadOnlyList<SqlWhereExpression> Children) : SqlWhereExpression;

public sealed record SqlWhereNotExpression(SqlWhereExpression Inner) : SqlWhereExpression;

public sealed record SqlWhereTruthyExpression(SqlWhereValueExpression Value) : SqlWhereExpression;

public sealed record SqlWhereComparisonExpression(
    SqlWhereValueExpression Left,
    SqlWhereComparisonOperator Operator,
    SqlWhereValueExpression Right) : SqlWhereExpression;

public sealed record SqlWhereInListExpression(
    SqlWhereValueExpression Left,
    IReadOnlyList<SqlWhereValueExpression> Values,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereInSubqueryExpression(
    SqlWhereValueExpression Left,
    string SubquerySql,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereLikeExpression(
    SqlWhereValueExpression Left,
    SqlWhereValueExpression Pattern,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereBetweenExpression(
    SqlWhereValueExpression Value,
    SqlWhereValueExpression Lower,
    SqlWhereValueExpression Upper,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereNullCheckExpression(
    SqlWhereValueExpression Value,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereDistinctFromExpression(
    SqlWhereValueExpression Left,
    SqlWhereValueExpression Right,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereExistsExpression(
    string SubquerySql,
    bool Negated) : SqlWhereExpression;

public sealed record SqlWhereQuantifiedComparisonExpression(
    SqlWhereValueExpression Left,
    SqlWhereComparisonOperator Operator,
    SqlWhereQuantifier Quantifier,
    string SubquerySql) : SqlWhereExpression;

public enum SqlWhereQuantifier
{
    Any,
    Some,
    All
}

public abstract record SqlWhereValueExpression;

public sealed record SqlWhereColumnExpression(string FullName, string SimpleName, string? Collation = null) : SqlWhereValueExpression;

public sealed record SqlWhereLiteralExpression(object? Value) : SqlWhereValueExpression;

public sealed record SqlWhereCaseExpression(string ExpressionText) : SqlWhereValueExpression;

public sealed record SqlWhereScalarSubqueryValueExpression(string SubquerySql) : SqlWhereValueExpression;

public enum SqlWhereUnaryOperator
{
    Plus,
    Minus,
    BitwiseNot
}

public sealed record SqlWhereUnaryValueExpression(
    SqlWhereUnaryOperator Operator,
    SqlWhereValueExpression Operand) : SqlWhereValueExpression;

public enum SqlWhereBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo
}

public sealed record SqlWhereBinaryValueExpression(
    SqlWhereValueExpression Left,
    SqlWhereBinaryOperator Operator,
    SqlWhereValueExpression Right) : SqlWhereValueExpression;

public sealed record SqlWhereParameterExpression(string Name, int Index) : SqlWhereValueExpression;

public sealed record SqlWhereCastExpression(
    SqlWhereValueExpression Inner,
    SqlScalarType TargetType) : SqlWhereValueExpression;

public sealed record SqlWhereFunctionCallExpression(
    string FunctionName,
    IReadOnlyList<SqlWhereValueExpression> Arguments) : SqlWhereValueExpression;

internal static class SqlWhereExpressionExtensions
{
    /// <summary>Collects distinct column indices referenced by this WHERE expression.</summary>
    public static int[] CollectColumnIndices(this SqlWhereExpression? expression, SqlTableDefinition table)
    {
        if (expression == null) return Array.Empty<int>();
        var set = new HashSet<int>();
        CollectColumns(expression, set, table);
        var result = new int[set.Count];
        set.CopyTo(result);
        Array.Sort(result);
        return result;
    }

    static void CollectColumns(SqlWhereExpression expr, HashSet<int> indices, SqlTableDefinition table)
    {
        switch (expr)
        {
            case SqlWhereAndExpression and:
                foreach (var c in and.Children) CollectColumns(c, indices, table);
                break;
            case SqlWhereOrExpression or:
                foreach (var c in or.Children) CollectColumns(c, indices, table);
                break;
            case SqlWhereNotExpression not:
                CollectColumns(not.Inner, indices, table);
                break;
            case SqlWhereTruthyExpression truthy:
                CollectValueColumns(truthy.Value, indices, table);
                break;
            case SqlWhereComparisonExpression cmp:
                CollectValueColumns(cmp.Left, indices, table);
                CollectValueColumns(cmp.Right, indices, table);
                break;
            case SqlWhereInListExpression inList:
                CollectValueColumns(inList.Left, indices, table);
                foreach (var v in inList.Values) CollectValueColumns(v, indices, table);
                break;
            case SqlWhereInSubqueryExpression inSub:
                CollectValueColumns(inSub.Left, indices, table);
                break;
            case SqlWhereLikeExpression like:
                CollectValueColumns(like.Left, indices, table);
                CollectValueColumns(like.Pattern, indices, table);
                break;
            case SqlWhereBetweenExpression between:
                CollectValueColumns(between.Value, indices, table);
                CollectValueColumns(between.Lower, indices, table);
                CollectValueColumns(between.Upper, indices, table);
                break;
            case SqlWhereNullCheckExpression nullCheck:
                CollectValueColumns(nullCheck.Value, indices, table);
                break;
            case SqlWhereDistinctFromExpression distinct:
                CollectValueColumns(distinct.Left, indices, table);
                CollectValueColumns(distinct.Right, indices, table);
                break;
            case SqlWhereExistsExpression:
                break;
            case SqlWhereQuantifiedComparisonExpression quantified:
                CollectValueColumns(quantified.Left, indices, table);
                break;
            case SqlWhereJsonContainsExpression contains:
                CollectValueColumns(contains.Left, indices, table);
                CollectValueColumns(contains.Right, indices, table);
                break;
            case SqlWhereJsonKeyExistsExpression keyExists:
                CollectValueColumns(keyExists.Left, indices, table);
                CollectValueColumns(keyExists.Right, indices, table);
                break;
        }
    }

    static void CollectValueColumns(SqlWhereValueExpression value, HashSet<int> indices, SqlTableDefinition table)
    {
        switch (value)
        {
            case SqlWhereColumnExpression col:
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    if (string.Equals(table.Columns[i].Name, col.SimpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        indices.Add(i);
                        break;
                    }
                }
                break;
            case SqlWhereUnaryValueExpression unary:
                CollectValueColumns(unary.Operand, indices, table);
                break;
            case SqlWhereBinaryValueExpression binary:
                CollectValueColumns(binary.Left, indices, table);
                CollectValueColumns(binary.Right, indices, table);
                break;
            case SqlWhereCastExpression cast:
                CollectValueColumns(cast.Inner, indices, table);
                break;
            case SqlWhereFunctionCallExpression func:
                foreach (var a in func.Arguments) CollectValueColumns(a, indices, table);
                break;
            // Literal, Parameter, ScalarSubquery, Case — no column refs
        }
    }
}

public sealed record SqlWhereJsonArrowExpression(
    SqlWhereValueExpression Source,
    string JsonPath,
    bool Unquote,
    SqlJsonPathKind PathKind) : SqlWhereValueExpression;

/// <summary>
/// Distinguishes how the operand of a JSON accessor is interpreted.
/// <see cref="Member"/> is a single key/index or a <c>$.path</c> expression
/// (Postgres <c>-&gt;</c> / <c>-&gt;&gt;</c>). <see cref="Path"/> is a Postgres
/// text-array path such as <c>'{a,b,0}'</c> (Postgres <c>#&gt;</c> / <c>#&gt;&gt;</c>).
/// </summary>
public enum SqlJsonPathKind
{
    Member,
    Path,
}

public enum SqlJsonContainmentOperator { Contains, ContainedBy }

public sealed record SqlWhereJsonContainsExpression(
    SqlWhereValueExpression Left,
    SqlWhereValueExpression Right,
    SqlJsonContainmentOperator Operator) : SqlWhereExpression;

public enum SqlJsonKeyExistsOperator { HasKey, HasAnyKey, HasAllKeys }

public sealed record SqlWhereJsonKeyExistsExpression(
    SqlWhereValueExpression Left,
    SqlWhereValueExpression Right,
    SqlJsonKeyExistsOperator Operator) : SqlWhereExpression;
