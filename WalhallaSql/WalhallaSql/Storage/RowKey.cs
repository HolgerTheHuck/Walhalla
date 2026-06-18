using System;

namespace WalhallaSql.Storage;

internal readonly struct RowKey(int tableId, long rowId) : IEquatable<RowKey>
{
    public readonly int TableId = tableId;
    public readonly long RowId = rowId;

    public bool Equals(RowKey other) => TableId == other.TableId && RowId == other.RowId;
    public override bool Equals(object? obj) => obj is RowKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TableId, RowId);
    public static bool operator ==(RowKey a, RowKey b) => a.Equals(b);
    public static bool operator !=(RowKey a, RowKey b) => !a.Equals(b);
}
