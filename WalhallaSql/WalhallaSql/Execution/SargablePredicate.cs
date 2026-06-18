using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal enum GinPredicateType
{
    None,
    Contains,     // @>
    ContainedBy,  // <@
    KeyExists,    // ?
    AnyKey,       // ?|
    AllKeys       // ?&
}

internal sealed record SargablePredicate(
    int ColumnIndex,
    SqlWhereComparisonOperator Operator,
    object? Value,
    bool ValueIsParameter = false,
    object? SecondValue = null,
    bool IsNullCheck = false,
    string? JsonPath = null,
    string? SourceColumnName = null,
    GinPredicateType GinOperator = GinPredicateType.None,
    string? GinQueryJson = null);

internal sealed record IndexSelection(
    SqlIndexDefinition Index,
    int IndexId,
    int Score,
    bool IsCovering,
    int[] IndexColumnIndices,
    SqlScalarType[] IndexKeyTypes,
    System.Collections.Generic.List<SargablePredicate> MatchedPredicates,
    System.Collections.Generic.List<SargablePredicate> ResidualPredicates);
