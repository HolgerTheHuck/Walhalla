using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Microsoft.EntityFrameworkCore.Query;

internal sealed class WalhallaSqlQueryableMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new WalhallaSqlQueryableMethodTranslatingExpressionVisitor(
            dependencies,
            relationalDependencies,
            (RelationalQueryCompilationContext)queryCompilationContext);
}

internal sealed class WalhallaSqlQueryableMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext)
    : RelationalQueryableMethodTranslatingExpressionVisitor(dependencies, relationalDependencies, queryCompilationContext)
{
    private static readonly MethodInfo QueryableCountMethodDefinition = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Count)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 1);

    private static readonly MethodInfo QueryableWhereMethodDefinition = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Where)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo QueryableAnyWithPredicateMethodDefinition = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Any)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo QueryableSelectMethodDefinition = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Select)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].IsGenericType
            && method.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Func<,>));

    private static readonly MethodInfo QueryableDistinctMethodDefinition = typeof(Queryable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Queryable.Distinct)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 1);

    protected override DeleteExpression TranslateExecuteDelete(ShapedQueryExpression source)
    {
        var prunedSource = PruneIncludes(source);
        if (TryGetSharedTableDeleteTarget(prunedSource.ShaperExpression, out var entityType, out var targetTable)
            && AreOtherNonOwnedEntityTypesInTheTable(entityType.GetRootType(), targetTable))
        {
            AddTranslationErrorDetails(GetExecuteDeleteOnTableSplittingMessage(targetTable.SchemaQualifiedName));
            return null!;
        }

        return base.TranslateExecuteDelete(prunedSource)!;
    }

    protected override UpdateExpression TranslateExecuteUpdate(
        ShapedQueryExpression source,
        IReadOnlyList<ExecuteUpdateSetter> setters)
    {
        var prunedSource = PruneIncludes(source);
        return base.TranslateExecuteUpdate(prunedSource, setters)!;
    }

    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        if (selector.Body == selector.Parameters[0])
            return source;

        WalhallaSqlQueryTranslationDebugState.LastSelectRewriteInfo = $"SelectorBefore={selector}";

        var newSelectorBody = RemapLambdaBodyCompat(source, selector);
        var rewrittenSelectorBody = new JoinCountDeduplicationRewriter().Visit(newSelectorBody);
        WalhallaSqlQueryTranslationDebugState.LastSelectRewriteInfo +=
            $"{Environment.NewLine}SelectorAfter={rewrittenSelectorBody}{Environment.NewLine}RewriteChanged={rewrittenSelectorBody != newSelectorBody}";

        if (rewrittenSelectorBody == newSelectorBody)
            return base.TranslateSelect(source, selector)!;

        var selectExpression = (SelectExpression)source.QueryExpression;
        if (selectExpression.IsDistinct)
        {
            selectExpression.PushdownIntoSubquery();
        }

        return source.UpdateShaperExpression(TranslateProjection(selectExpression, rewrittenSelectorBody));
    }

    private static ShapedQueryExpression PruneIncludes(ShapedQueryExpression source)
        => source.UpdateShaperExpression(new ExecuteNonQueryIncludePruner().Visit(source.ShaperExpression));

    private static LambdaExpression? UnwrapLambda(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
            ? lambda
            : expression as LambdaExpression;

    private static bool TryGetSharedTableDeleteTarget(
        Expression shaperExpression,
        out IEntityType entityType,
        out ITableBase targetTable)
    {
        entityType = null!;
        targetTable = null!;

        var shaper = TryGetStructuralTypeShaper(shaperExpression);
        if (shaper?.StructuralType is not IEntityType shapedEntityType)
            return false;

        var tableMappings = shapedEntityType.GetTableMappings().ToList();
        if (tableMappings.Count != 1)
            return false;

        entityType = shapedEntityType;
        targetTable = tableMappings[0].Table;
        return true;
    }

    private static StructuralTypeShaperExpression? TryGetStructuralTypeShaper(Expression expression)
        => expression switch
        {
            StructuralTypeShaperExpression shaper => shaper,
            IncludeExpression { Navigation: INavigation, EntityExpression: var entityExpression } => TryGetStructuralTypeShaper(entityExpression),
            _ => null
        };

    private static bool AreOtherNonOwnedEntityTypesInTheTable(IEntityType rootType, ITableBase table)
    {
        foreach (var entityTypeMapping in table.EntityTypeMappings)
        {
            var typeBase = entityTypeMapping.TypeBase;
            if ((entityTypeMapping.IsSharedTablePrincipal == true
                    && typeBase != rootType)
                || (entityTypeMapping.IsSharedTablePrincipal == false
                    && typeBase is IEntityType entityType
                    && entityType.GetRootType() != rootType
                    && !entityType.IsOwned()))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ExecuteNonQueryIncludePruner : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
            => node switch
            {
                IncludeExpression { Navigation: ISkipNavigation or not INavigation } include => include,
                IncludeExpression include => Visit(include.EntityExpression),
                _ => base.VisitExtension(node)
            };
    }

    private sealed class JoinCountDeduplicationRewriter : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
            => node is RelationalGroupByShaperExpression
                ? node
                : base.VisitExtension(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(Queryable.Count)
                && node.Arguments.Count == 1
                && TryRewriteJoinCount(node.Arguments[0], out var rewritten))
            {
                return Visit(rewritten);
            }

            return base.VisitMethodCall(node);
        }

        private static bool TryRewriteJoinCount(Expression joinSource, out Expression rewritten)
        {
            rewritten = null!;
            if (joinSource is not MethodCallExpression joinCall
                || joinCall.Method.Name != nameof(Queryable.Join)
                || joinCall.Arguments.Count != 5)
            {
                return false;
            }

            var outerSource = joinCall.Arguments[0];
            var innerSource = joinCall.Arguments[1];
            var outerKeySelector = UnwrapLambda(joinCall.Arguments[2]);
            var innerKeySelector = UnwrapLambda(joinCall.Arguments[3]);
            var outerElementType = GetSequenceElementType(outerSource.Type);
            var innerElementType = GetSequenceElementType(innerSource.Type);

            if (outerKeySelector == null
                || innerKeySelector == null
                || outerElementType == null
                || innerElementType == null)
            {
                return false;
            }

            var innerParameter = Expression.Parameter(innerElementType, innerKeySelector.Parameters[0].Name ?? "inner");
            var rewrittenInnerKey = new ParameterReplacingExpressionVisitor(innerKeySelector.Parameters[0], innerParameter)
                .Visit(innerKeySelector.Body)!;
            var anyPredicate = Expression.Lambda(
                BuildKeyEquality(outerKeySelector.Body, rewrittenInnerKey),
                outerKeySelector.Parameters[0]);

            var anyCall = Expression.Call(
                QueryableAnyWithPredicateMethodDefinition.MakeGenericMethod(outerElementType),
                outerSource,
                Expression.Quote(anyPredicate));

            var wherePredicate = Expression.Lambda(anyCall, innerParameter);
            var whereCall = Expression.Call(
                QueryableWhereMethodDefinition.MakeGenericMethod(innerElementType),
                innerSource,
                Expression.Quote(wherePredicate));

            var distinctKeySelector = Expression.Lambda(rewrittenInnerKey, innerParameter);
            var selectDistinctKeysCall = Expression.Call(
                QueryableSelectMethodDefinition.MakeGenericMethod(innerElementType, rewrittenInnerKey.Type),
                whereCall,
                Expression.Quote(distinctKeySelector));

            var distinctCall = Expression.Call(
                QueryableDistinctMethodDefinition.MakeGenericMethod(rewrittenInnerKey.Type),
                selectDistinctKeysCall);

            rewritten = Expression.Call(QueryableCountMethodDefinition.MakeGenericMethod(rewrittenInnerKey.Type), distinctCall);
            return true;
        }

        private static Expression BuildKeyEquality(Expression left, Expression right)
        {
            left = UnwrapConvert(left);
            right = UnwrapConvert(right);

            if (left.Type != right.Type)
            {
                if (right.Type.IsAssignableFrom(left.Type))
                {
                    left = Expression.Convert(left, right.Type);
                }
                else if (left.Type.IsAssignableFrom(right.Type))
                {
                    right = Expression.Convert(right, left.Type);
                }
            }

            return Expression.Equal(left, right);
        }

        private static Expression UnwrapConvert(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression)
            {
                expression = unaryExpression.Operand;
            }

            return expression;
        }

        private static Type? GetSequenceElementType(Type sequenceType)
        {
            if (sequenceType.IsArray)
                return sequenceType.GetElementType();

            return sequenceType
                .GetInterfaces()
                .Concat([sequenceType])
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        }
    }

    private Expression RemapLambdaBodyCompat(ShapedQueryExpression shapedQueryExpression, LambdaExpression lambdaExpression)
        => new ReplacingExpressionVisitor(
                [lambdaExpression.Parameters.Single()],
                [shapedQueryExpression.ShaperExpression])
            .Visit(lambdaExpression.Body)!;

    private Expression TranslateProjection(SelectExpression selectExpression, Expression selectorBody)
    {
        var projectionBindingExpressionVisitorField = typeof(RelationalQueryableMethodTranslatingExpressionVisitor)
            .GetField("_projectionBindingExpressionVisitor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Projection binding visitor field was not found.");

        var projectionBindingExpressionVisitor = projectionBindingExpressionVisitorField.GetValue(this)
            ?? throw new InvalidOperationException("Projection binding visitor was not initialized.");

        var translateMethod = projectionBindingExpressionVisitor.GetType().GetMethod(
            "Translate",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(SelectExpression), typeof(Expression)],
            modifiers: null)
            ?? throw new InvalidOperationException("Projection binding Translate method was not found.");

        try
        {
            return (Expression)translateMethod.Invoke(projectionBindingExpressionVisitor, [selectExpression, selectorBody])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed class ParameterReplacingExpressionVisitor(Expression source, Expression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : base.VisitParameter(node);
    }

    private static string GetExecuteDeleteOnTableSplittingMessage(string tableName)
    {
        var relationalAssembly = typeof(RelationalQueryableMethodTranslatingExpressionVisitor).Assembly;
        var relationalStringsType = relationalAssembly.GetType("Microsoft.EntityFrameworkCore.Diagnostics.RelationalStrings")
            ?? relationalAssembly.GetType("Microsoft.EntityFrameworkCore.RelationalStrings");
        var method = relationalStringsType?.GetMethod(
            "ExecuteDeleteOnTableSplitting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string)],
            modifiers: null);

        if (method?.Invoke(null, [tableName]) is string message)
            return message;

        return $"The operation 'ExecuteDelete' is being applied on the table '{tableName}', which contains data for multiple entity types. Executing this delete is not supported for table splitting.";
    }
}

internal static class WalhallaSqlQueryTranslationDebugState
{
    public static string? LastSelectRewriteInfo { get; set; }
}
