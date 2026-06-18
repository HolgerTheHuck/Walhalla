namespace WalhallaSql.Execution;

/// <summary>PK range scan info extracted from WHERE clause (BETWEEN, &gt;, &gt;=, &lt;, &lt;=).</summary>
internal sealed class PkRangeLookup
{
    public int MinParameterIndex { get; init; } = -1;
    public int MaxParameterIndex { get; init; } = -1;
    public bool MinInclusive { get; init; }
    public bool MaxInclusive { get; init; }
    public int ColumnIndex { get; init; } = -1;

    // Literal-bounded ranges (for direct Execute without parameters).
    public bool HasLiteralBounds { get; init; }
    public long LiteralMin { get; init; }
    public long LiteralMax { get; init; }
}
