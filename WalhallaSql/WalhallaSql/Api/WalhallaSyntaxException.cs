using System;

namespace WalhallaSql;

/// <summary>
/// Thrown when the SQL parser encounters a syntax error or unsupported construct.
/// SQLSTATE: 42601 (syntax_error).
/// </summary>
public sealed class WalhallaSyntaxException : WalhallaException
{
    public WalhallaSyntaxException(string message) : base(message, "42601") { }
    public WalhallaSyntaxException(string message, Exception inner) : base(message, "42601") { }
}
