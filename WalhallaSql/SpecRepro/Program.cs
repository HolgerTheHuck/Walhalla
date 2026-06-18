using System;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;

var sql = "SELECT COUNT(*) FROM Singularities WHERE Id = 35";
var stmt = SqlStatementParser.Parse(sql);
if (stmt is SqlSelectStatement select)
{
    Console.WriteLine($"Table: {select.TableName}");
    Console.WriteLine($"Columns: {select.Columns.Count}");
    foreach (var col in select.Columns)
    {
        Console.WriteLine($"  Expr={col.Expression}, Alias={col.Alias}, Aggregate={col.Aggregate?.Function}, Window={col.WindowFunction?.Function}");
    }
    var hasAgg = select.Columns.Any(c => c.Aggregate != null && c.WindowFunction == null);
    Console.WriteLine($"HasAggregates: {hasAgg}");
}
else
{
    Console.WriteLine($"Parsed as {stmt.GetType().Name}");
}
