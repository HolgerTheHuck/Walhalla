using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create() => new WalhallaSqlQuerySqlGenerator(dependencies);
}

internal sealed class WalhallaSqlQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
    : QuerySqlGenerator(dependencies)
{
    protected override Expression VisitJsonScalar(JsonScalarExpression jsonScalarExpression)
    {
        if (jsonScalarExpression.Path.Count == 0)
        {
            Visit(jsonScalarExpression.Json);
            return jsonScalarExpression;
        }

        if (jsonScalarExpression.Json is not ColumnExpression jsonColumn
            || !WalhallaSqlJsonProjectionHelper.TryBuildProjectionName(jsonColumn, jsonScalarExpression.Path, out var projectionName))
        {
            throw new NotSupportedException("WalhallaSql unterstützt JSON-Skalare derzeit nur für konstante Property-Pfade auf JSON-Container-Spalten.");
        }

        if (!string.IsNullOrEmpty(jsonColumn.TableAlias))
        {
            Sql
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(jsonColumn.TableAlias))
                .Append(".");
        }

        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(projectionName));
        return jsonScalarExpression;
    }
}
