using System;

namespace WalhallaSql;

public class WalhallaException : Exception
{
    public WalhallaException(string message) : base(message) { }
    public WalhallaException(string message, Exception inner) : base(message, inner) { }
    public WalhallaException(string message, string? sqlState) : base(message) { SqlState = sqlState; }
    public WalhallaException(string message, string? sqlState, string? hint, string? detail)
        : base(message)
    {
        SqlState = sqlState;
        Hint = hint;
        Detail = detail;
    }

    /// <summary>
    /// Optional PostgreSQL-compatible SQLSTATE code (e.g. <c>23514</c> for check_violation).
    /// When null, callers default to the generic <c>XX000</c>.
    /// </summary>
    public string? SqlState { get; }

    /// <summary>
    /// Optional HINT-Text, z. B. aus einem PLW-RAISE-EXCEPTION-Handler.
    /// </summary>
    public string? Hint { get; }

    /// <summary>
    /// Optional DETAIL-Text, z. B. aus einem PLW-RAISE-EXCEPTION-Handler.
    /// </summary>
    public string? Detail { get; }
}
