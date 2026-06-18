using System;

namespace WalhallaSql;

/// <summary>
/// Thrown when a table constraint (CHECK, UNIQUE, FOREIGN KEY, NOT NULL) is violated.
/// Common SQLSTATEs:
///   23505 — unique_violation
///   23514 — check_violation
///   23503 — foreign_key_violation
///   23502 — not_null_violation
/// </summary>
public sealed class WalhallaConstraintException : WalhallaException
{
    public WalhallaConstraintException(string message) : base(message) { }
    public WalhallaConstraintException(string message, string sqlState) : base(message, sqlState) { }
    public WalhallaConstraintException(string message, Exception inner) : base(message, inner) { }
}
