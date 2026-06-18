using System.Collections.Generic;

namespace WalhallaSql.AdoNet.SqlClient;

public sealed record SqlClientStreamResult(
    int AffectedRows,
    IEnumerable<IReadOnlyDictionary<string, object?>> Rows,
    SqlOptimizationInfo? Optimization = null)
{
    internal ScalarResultData? ScalarData { get; init; }
}
