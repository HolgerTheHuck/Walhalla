using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlFilteredIncludeQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        WalhallaSqlFilteredIncludeQueryExpressionDebugState.LastQueryExpression = queryExpression.ToString();
        WalhallaSqlFilteredIncludeQueryExpressionDebugState.LastRewrittenQueryExpression = queryExpression.ToString();
        WalhallaSqlFilteredIncludeQueryExpressionDebugState.LastRewriteChanged = false;
        return queryExpression;
    }
}

internal static class WalhallaSqlFilteredIncludeQueryExpressionDebugState
{
    public static string? LastQueryExpression { get; set; }
    public static string? LastRewrittenQueryExpression { get; set; }
    public static bool LastRewriteChanged { get; set; }
}
