using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlSqlTranslatingExpressionVisitorFactory(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies)
    : IRelationalSqlTranslatingExpressionVisitorFactory
{
    public RelationalSqlTranslatingExpressionVisitor Create(
        QueryCompilationContext queryCompilationContext,
        QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
        => new WalhallaSqlSqlTranslatingExpressionVisitor(
            dependencies,
            queryCompilationContext,
            queryableMethodTranslatingExpressionVisitor);
}

internal sealed class WalhallaSqlSqlTranslatingExpressionVisitor(
    RelationalSqlTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
    : RelationalSqlTranslatingExpressionVisitor(dependencies, queryCompilationContext, queryableMethodTranslatingExpressionVisitor)
{
    private static readonly MethodInfo ConstructStartsWithPatternParameterMethod = typeof(WalhallaSqlSqlTranslatingExpressionVisitor)
        .GetMethod(nameof(ConstructStartsWithPatternParameter), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ConstructEnumTokenParameterMethod = typeof(WalhallaSqlSqlTranslatingExpressionVisitor)
        .GetMethod(nameof(ConstructEnumTokenParameter), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ConstructDelimitedContainsPatternParameterMethod = typeof(WalhallaSqlSqlTranslatingExpressionVisitor)
        .GetMethod(nameof(ConstructDelimitedContainsPatternParameter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly QueryCompilationContext _queryCompilationContext = queryCompilationContext;
    private readonly ISqlExpressionFactory _sqlExpressionFactory = dependencies.SqlExpressionFactory;
    private readonly RelationalTypeMapping _stringTypeMapping = (RelationalTypeMapping)(dependencies.TypeMappingSource.FindMapping(typeof(string))
        ?? throw new InvalidOperationException("WalhallaSql requires a string type mapping."));

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        try
        {
            if (TryTranslateStringStartsWith(methodCallExpression, out var startsWithTranslation))
                return startsWithTranslation;

            if (TryTranslateEnumCollectionContains(methodCallExpression, out var translation))
                return translation;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    private bool TryTranslateStringStartsWith(
        MethodCallExpression methodCallExpression,
        out SqlExpression translation)
    {
        translation = null!;

        if (methodCallExpression.Method.Name != nameof(string.StartsWith)
            || methodCallExpression.Object == null
            || methodCallExpression.Object.Type != typeof(string)
            || methodCallExpression.Arguments.Count != 1)
        {
            return false;
        }

        var instanceSql = Visit(StripConvert(methodCallExpression.Object)) as SqlExpression;
        if (instanceSql == null)
            return false;

        var prefixSql = Visit(StripConvert(methodCallExpression.Arguments[0])) as SqlExpression;
        if (!TryBuildStartsWithPattern(methodCallExpression.Arguments[0], prefixSql, out var patternSql))
            return false;

        translation = _sqlExpressionFactory.Like(ApplyStringTypeMapping(instanceSql), patternSql);
        return true;
    }

    private bool TryTranslateEnumCollectionContains(
        MethodCallExpression methodCallExpression,
        out SqlExpression translation)
    {
        translation = null!;

        if (!IsCollectionContains(methodCallExpression, out var elementType)
            || !elementType.IsEnum)
        {
            return false;
        }

        var collectionExpression = GetCollectionExpression(methodCallExpression);
        var itemExpression = GetItemExpression(methodCallExpression);

        var collectionSql = Visit(StripConvert(collectionExpression)) as SqlExpression;
        if (collectionSql == null)
            return false;

        if (!string.Equals(collectionSql.TypeMapping?.StoreType, "TEXT", StringComparison.OrdinalIgnoreCase)
            && collectionSql.Type != typeof(string))
        {
            return false;
        }

        var argumentSql = Visit(itemExpression) as SqlExpression;
        if (argumentSql == null)
            return false;

        if (!TryBuildTokenExpressions(itemExpression, argumentSql, out var token, out var prefixPattern, out var suffixPattern, out var middlePattern))
            return false;

        var mappedCollectionSql = ApplyStringTypeMapping(collectionSql);
        var tokenIsNotNull = _sqlExpressionFactory.IsNotNull(token);
        var membership = _sqlExpressionFactory.OrElse(
            _sqlExpressionFactory.Equal(mappedCollectionSql, token),
            _sqlExpressionFactory.OrElse(
                _sqlExpressionFactory.Like(mappedCollectionSql, prefixPattern),
                _sqlExpressionFactory.OrElse(
                    _sqlExpressionFactory.Like(mappedCollectionSql, suffixPattern),
                    _sqlExpressionFactory.Like(mappedCollectionSql, middlePattern))));

        translation = _sqlExpressionFactory.AndAlso(tokenIsNotNull, membership);
        return true;
    }

    private bool TryBuildStartsWithPattern(
        Expression originalArgument,
        SqlExpression? translatedArgument,
        out SqlExpression pattern)
    {
        pattern = null!;

        if (translatedArgument is SqlConstantExpression)
        {
            if (!TryEvaluateValue(originalArgument, out var rawValue) || rawValue == null)
                return false;

            var prefix = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
            pattern = _sqlExpressionFactory.Constant(prefix + "%", _stringTypeMapping);
            return true;
        }

        if (translatedArgument is SqlParameterExpression sqlParameter)
        {
            var lambda = Expression.Lambda(
                Expression.Call(
                    ConstructStartsWithPatternParameterMethod,
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(sqlParameter.Name)),
                QueryCompilationContext.QueryContextParameter);

            var parameter = _queryCompilationContext.RegisterRuntimeParameter($"{sqlParameter.Name}_startswith", lambda);
            pattern = new SqlParameterExpression(parameter.Name!, parameter.Type, _stringTypeMapping);
            return true;
        }

        return false;
    }

    private bool TryBuildTokenExpressions(
        Expression originalArgument,
        SqlExpression translatedArgument,
        out SqlExpression token,
        out SqlExpression prefixPattern,
        out SqlExpression suffixPattern,
        out SqlExpression middlePattern)
    {
        token = null!;
        prefixPattern = null!;
        suffixPattern = null!;
        middlePattern = null!;

        if (translatedArgument is SqlConstantExpression)
        {
            if (!TryEvaluateValue(originalArgument, out var rawValue))
                return false;

            var tokenValue = ConvertEnumValueToToken(rawValue);
            token = _sqlExpressionFactory.Constant(tokenValue, _stringTypeMapping);
            prefixPattern = _sqlExpressionFactory.Constant(BuildDelimitedContainsPattern(tokenValue, DelimitedPatternKind.Prefix), _stringTypeMapping);
            suffixPattern = _sqlExpressionFactory.Constant(BuildDelimitedContainsPattern(tokenValue, DelimitedPatternKind.Suffix), _stringTypeMapping);
            middlePattern = _sqlExpressionFactory.Constant(BuildDelimitedContainsPattern(tokenValue, DelimitedPatternKind.Middle), _stringTypeMapping);
            return true;
        }

        if (translatedArgument is SqlParameterExpression sqlParameter)
        {
            token = RegisterPatternParameter(sqlParameter, "token", DelimitedPatternKind.Token);
            prefixPattern = RegisterPatternParameter(sqlParameter, "prefix", DelimitedPatternKind.Prefix);
            suffixPattern = RegisterPatternParameter(sqlParameter, "suffix", DelimitedPatternKind.Suffix);
            middlePattern = RegisterPatternParameter(sqlParameter, "middle", DelimitedPatternKind.Middle);
            return true;
        }

        return false;
    }

    private SqlParameterExpression RegisterPatternParameter(
        SqlParameterExpression sqlParameter,
        string suffix,
        DelimitedPatternKind patternKind)
    {
        LambdaExpression lambda;

        if (patternKind == DelimitedPatternKind.Token)
        {
            lambda = Expression.Lambda(
                Expression.Call(
                    ConstructEnumTokenParameterMethod,
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(sqlParameter.Name)),
                QueryCompilationContext.QueryContextParameter);
        }
        else
        {
            lambda = Expression.Lambda(
                Expression.Call(
                    ConstructDelimitedContainsPatternParameterMethod,
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(sqlParameter.Name),
                    Expression.Constant(patternKind)),
                QueryCompilationContext.QueryContextParameter);
        }

        var parameter = _queryCompilationContext.RegisterRuntimeParameter($"{sqlParameter.Name}_{suffix}", lambda);
        return new SqlParameterExpression(parameter.Name!, parameter.Type, _stringTypeMapping);
    }

    private SqlExpression ApplyStringTypeMapping(SqlExpression expression)
        => expression.TypeMapping == _stringTypeMapping
            ? expression
            : _sqlExpressionFactory.ApplyTypeMapping(expression, _stringTypeMapping)!;

    private static bool IsCollectionContains(MethodCallExpression methodCallExpression, out Type elementType)
    {
        elementType = null!;

        if (methodCallExpression.Method.Name != nameof(ICollection<int>.Contains))
        {
            return false;
        }

        Expression collectionExpression;

        if (methodCallExpression.Object != null && methodCallExpression.Arguments.Count == 1)
        {
            collectionExpression = methodCallExpression.Object;
        }
        else if (methodCallExpression.Object == null
                 && methodCallExpression.Arguments.Count == 2
                 && methodCallExpression.Method.DeclaringType == typeof(Enumerable))
        {
            collectionExpression = methodCallExpression.Arguments[0];
        }
        else
        {
            return false;
        }

        var strippedCollectionExpression = StripConvert(collectionExpression);
        var collectionType = strippedCollectionExpression.Type;
        if (collectionType == typeof(string) || collectionType == typeof(byte[]))
            return false;

        if (typeof(IQueryable).IsAssignableFrom(collectionType))
            return false;

        if (strippedCollectionExpression is MethodCallExpression)
            return false;

        elementType = collectionType
            .GetInterfaces()
            .Concat([collectionType])
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0]!;

        return elementType != null;
    }

    private static Expression GetCollectionExpression(MethodCallExpression methodCallExpression)
        => methodCallExpression.Object ?? methodCallExpression.Arguments[0];

    private static Expression GetItemExpression(MethodCallExpression methodCallExpression)
        => methodCallExpression.Object != null ? methodCallExpression.Arguments[0] : methodCallExpression.Arguments[1];

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unaryExpression
               && (unaryExpression.NodeType == ExpressionType.Convert || unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }

    private static bool TryEvaluateValue(Expression expression, out object? value)
    {
        try
        {
            var lambda = Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object)));
            value = lambda.Compile().Invoke();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static string? ConstructEnumTokenParameter(QueryContext queryContext, string baseParameterName)
        => ConvertEnumValueToToken(GetQueryParameterValue(queryContext, baseParameterName));

    private static string? ConstructStartsWithPatternParameter(QueryContext queryContext, string baseParameterName)
    {
        var value = GetQueryParameterValue(queryContext, baseParameterName);
        return value == null ? null : (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "%";
    }

    private static string? ConstructDelimitedContainsPatternParameter(
        QueryContext queryContext,
        string baseParameterName,
        DelimitedPatternKind patternKind)
        => BuildDelimitedContainsPattern(ConvertEnumValueToToken(GetQueryParameterValue(queryContext, baseParameterName)), patternKind);

    private static object? GetQueryParameterValue(QueryContext queryContext, string parameterName)
    {
        var parametersProperty = queryContext.GetType().GetProperty("Parameters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? queryContext.GetType().GetProperty("ParameterValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (parametersProperty?.GetValue(queryContext) is IReadOnlyDictionary<string, object?> readOnlyDictionary
            && readOnlyDictionary.TryGetValue(parameterName, out var readOnlyValue))
        {
            return readOnlyValue;
        }

        if (parametersProperty?.GetValue(queryContext) is IDictionary<string, object?> dictionary
            && dictionary.TryGetValue(parameterName, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Query parameter '{parameterName}' could not be resolved.");
    }

    private static string? ConvertEnumValueToToken(object? value)
    {
        if (value == null)
            return null;

        var runtimeType = value.GetType();
        if (runtimeType.IsEnum)
            return value.ToString();

        var nullableUnderlyingType = Nullable.GetUnderlyingType(runtimeType);
        if (nullableUnderlyingType?.IsEnum == true)
            return Convert.ToString(value);

        return value as string ?? Convert.ToString(value);
    }

    private static string? BuildDelimitedContainsPattern(string? token, DelimitedPatternKind patternKind)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        return patternKind switch
        {
            DelimitedPatternKind.Token => token,
            DelimitedPatternKind.Prefix => token + ",%",
            DelimitedPatternKind.Suffix => "%," + token,
            _ => "%," + token + ",%"
        };
    }

    private enum DelimitedPatternKind
    {
        Token,
        Prefix,
        Suffix,
        Middle
    }
}
