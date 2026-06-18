using System;
using System.Collections.Generic;

namespace WalhallaSql.PgWire;

// ── Public virtual-table types (used by the backend interface) ──────────────

public enum PgVirtualRelationKind
{
    Table,
    View
}

public sealed record PgVirtualColumnDefinition(
    string Name,
    string DataType,
    bool IsNullable = true,
    bool IsPrimaryKey = false,
    string? Collation = null);

public sealed record PgVirtualTableDefinition(
    string Name,
    IReadOnlyList<PgVirtualColumnDefinition> Columns,
    PgVirtualRelationKind RelationKind = PgVirtualRelationKind.Table);

// ── Backend abstraction interfaces ──────────────────────────────────────────

public interface IPgWireBackendConnection : IDisposable
{
    string DatabaseName { get; }
    string DatabaseCollation => "C";
    string DatabaseCType => "C";
    IPgWireBackendCommand CreateCommand();
    IPgWireBackendTransaction BeginTransaction();
    IReadOnlyList<PgVirtualTableDefinition> DiscoverTables();

    /// <summary>
    /// Describe the result columns of a query without executing it (optional optimization).
    /// Return null or empty to signal that the caller should fall back to executing the query.
    /// </summary>
    IReadOnlyList<(string Name, Type ClrType)>? TryDescribeQuery(string sql);

    /// <summary>
    /// Attempts to retrieve the stored SCRAM-SHA-256 hash for a user.
    /// Return false if the user does not exist or auth is not configured.
    /// </summary>
    bool TryGetStoredHash(string username, out string storedHash)
    {
        storedHash = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns all pg_stats rows for all analyzed tables. Used by the PgWire virtual table handler.
    /// Default returns an empty list; override in backends that support statistics.
    /// </summary>
    IReadOnlyList<Dictionary<string, object?>> GetPgStatsRows()
        => Array.Empty<Dictionary<string, object?>>();
}

public interface IPgWireBackendCommand : IDisposable
{
    string CommandText { get; set; }
    IPgWireBackendTransaction? Transaction { get; set; }
    IPgWireBackendReader ExecuteReader();
    int ExecuteNonQuery();
}

public interface IPgWireBackendReader : IDisposable
{
    int FieldCount { get; }
    string GetName(int i);
    Type GetFieldType(int i);
    bool Read();
    bool IsDBNull(int i);
    object GetValue(int i);
}

public interface IPgWireBackendTransaction : IDisposable
{
    void Commit();
    void Rollback();
}
