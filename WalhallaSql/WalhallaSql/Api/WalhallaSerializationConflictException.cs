using System;

namespace WalhallaSql;

/// <summary>
/// Thrown when an MVCC transaction encounters a serialization conflict
/// (e.g. concurrent write on the same row under snapshot isolation).
/// SQLSTATE: 40001 (serialization_failure).
/// </summary>
public sealed class WalhallaSerializationConflictException : WalhallaException
{
    public WalhallaSerializationConflictException(string message) : base(message, "40001") { }
    public WalhallaSerializationConflictException(string message, Exception inner) : base(message, "40001") { }
}
