using System;
using WalhallaSql.Parsing.Plw;

namespace WalhallaSql.Execution.Plw;

/// <summary>
/// Steuerfluss-Exception fuer EXIT, CONTINUE und RETURN innerhalb von PLW.
/// </summary>
internal sealed class PlwFlowControlException : Exception
{
    public PlwFlowControlKind Kind { get; }
    public string? Label { get; }
    public WalhallaResultSet? ReturnResultSet { get; }
    public object? ReturnValue { get; }

    public PlwFlowControlException(PlwFlowControlKind kind, string? label = null)
        : base($"PLW flow control: {kind}")
    {
        Kind = kind;
        Label = label;
    }

    public PlwFlowControlException(WalhallaResultSet returnResultSet)
        : base("PLW RETURN QUERY")
    {
        Kind = PlwFlowControlKind.Return;
        ReturnResultSet = returnResultSet;
    }

    public PlwFlowControlException(object? returnValue)
        : base("PLW RETURN")
    {
        Kind = PlwFlowControlKind.Return;
        ReturnValue = returnValue;
    }
}

internal enum PlwFlowControlKind
{
    Exit,
    Continue,
    Return
}
