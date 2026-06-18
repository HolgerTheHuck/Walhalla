using System;

namespace WalhallaSql;

public class WalhallaException : Exception
{
    public WalhallaException(string message) : base(message) { }
    public WalhallaException(string message, Exception inner) : base(message, inner) { }
    public WalhallaException(string message, string? sqlState) : base(message) { SqlState = sqlState; }

    /// <summary>
    /// Optional PostgreSQL-compatible SQLSTATE code (e.g. <c>23514</c> for check_violation).
    /// When null, callers default to the generic <c>XX000</c>.
    /// </summary>
    public string? SqlState { get; }
}
