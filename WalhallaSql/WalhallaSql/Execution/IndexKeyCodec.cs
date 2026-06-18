using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using WalhallaSql.Sql;

namespace WalhallaSql.Execution;

internal static class IndexKeyCodec
{
    /// <summary>Null sentinel byte — empty key sorts before all non-null values.</summary>
    private static readonly byte[] NullSentinel = Array.Empty<byte>();

    /// <summary>String/binary terminator — ensures shorter strings sort before longer ones with same prefix.</summary>
    private const byte Terminator = 0x00;

    // ── Single-value sortable encode ──────────────────────────────────────────

    public static byte[] EncodeSortable(object? value, SqlScalarType type)
    {
        if (value == null || value == DBNull.Value)
            return NullSentinel;

        return type switch
        {
            SqlScalarType.Int32 => Int32Sortable(Convert.ToInt32(value)),
            SqlScalarType.Int64 => Int64Sortable(Convert.ToInt64(value)),
            SqlScalarType.Int16 => Int16Sortable(Convert.ToInt16(value)),
            SqlScalarType.Double => DoubleSortable(Convert.ToDouble(value)),
            SqlScalarType.Decimal => DecimalSortable((decimal)value),
            SqlScalarType.Boolean => BoolSortable((bool)value),
            SqlScalarType.DateTime => DateTimeSortable((DateTime)value),
            SqlScalarType.Date => DateTimeSortable(value is DateTime dt ? dt : Convert.ToDateTime(value).Date),
            SqlScalarType.Time => TimeSortable(value is TimeSpan ts ? ts : TimeSpan.Parse(value.ToString()!)),
            SqlScalarType.Guid => GuidSortable((Guid)value),
            SqlScalarType.Binary => BinarySortable((byte[])value),
            _ => StringSortable(value.ToString() ?? string.Empty)
        };
    }

    // ── Integer encoding: sign-bit flip + big-endian ──────────────────────────

    public static byte[] Int32Sortable(int v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, v);
        buf[0] ^= 0x80; // flip sign bit
        return buf;
    }

    public static byte[] Int64Sortable(long v)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, v);
        buf[0] ^= 0x80;
        return buf;
    }

    public static byte[] Int16Sortable(short v)
    {
        var buf = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, v);
        buf[0] ^= 0x80;
        return buf;
    }

    // ── Double: IEEE 754 sign-flip + big-endian ───────────────────────────────

    public static byte[] DoubleSortable(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        // If negative, flip all bits. If non-negative, flip only the sign bit.
        if (bits < 0)
            bits = ~bits;
        else
            bits ^= 1L << 63;

        var buf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, bits);
        return buf;
    }

    // ── Decimal: 4× Int32 components, each sign-flipped + big-endian ──────────

    public static byte[] DecimalSortable(decimal v)
    {
        var bits = decimal.GetBits(v); // [lo, mid, hi, flags]
        var buf = new byte[16];
        int offset = 0;
        for (int i = 0; i < 4; i++)
        {
            int component = bits[i];
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(offset), component);
            buf[offset] ^= 0x80;
            offset += 4;
        }
        // Negate: flip all bits of all components if negative
        if (v < 0)
        {
            for (int i = 0; i < 16; i++)
                buf[i] ^= 0xFF;
        }
        return buf;
    }

    // ── Boolean: 0x00/0x01 ────────────────────────────────────────────────────

    public static byte[] BoolSortable(bool v)
    {
        return new[] { v ? (byte)1 : (byte)0 };
    }

    // ── DateTime: ticks as sortable Int64 ─────────────────────────────────────

    public static byte[] DateTimeSortable(DateTime v)
    {
        return Int64Sortable(v.ToUniversalTime().Ticks);
    }

    public static byte[] TimeSortable(TimeSpan v)
    {
        return Int64Sortable(v.Ticks);
    }

    // ── String: UTF-8 + \0 terminator ─────────────────────────────────────────

    public static byte[] StringSortable(string s)
    {
        var utf8 = Encoding.UTF8.GetBytes(s);
        // Escape embedded \0 in strings (shouldn't happen normally, but guard)
        bool hasNull = false;
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == 0) { hasNull = true; break; }
        }

        if (!hasNull)
        {
            var result = new byte[utf8.Length + 1];
            Buffer.BlockCopy(utf8, 0, result, 0, utf8.Length);
            result[utf8.Length] = Terminator;
            return result;
        }

        // Escape: \0 → \x00\xFF, terminator → \x00\x00
        var escaped = new List<byte>(utf8.Length + 2);
        foreach (var b in utf8)
        {
            if (b == 0)
                escaped.Add(0xFF);
            else
                escaped.Add(b);
        }
        escaped.Add(Terminator);
        return escaped.ToArray();
    }

    // ── Guid: 16-byte binary with endianness fixes ────────────────────────────

    public static byte[] GuidSortable(Guid v)
    {
        var bytes = v.ToByteArray();
        // Guid.ToByteArray returns little-endian layout for the int/short parts.
        // Convert to big-endian for the first 8 bytes (int + 2× short).
        var result = new byte[16];
        result[0] = bytes[3]; result[1] = bytes[2]; result[2] = bytes[1]; result[3] = bytes[0];
        result[4] = bytes[5]; result[5] = bytes[4];
        result[6] = bytes[7]; result[7] = bytes[6];
        // Remaining 8 bytes are already in big-endian order.
        Buffer.BlockCopy(bytes, 8, result, 8, 8);
        return result;
    }

    // ── Binary: raw bytes + \0 terminator ─────────────────────────────────────

    public static byte[] BinarySortable(byte[] v)
    {
        var result = new byte[v.Length + 1];
        Buffer.BlockCopy(v, 0, result, 0, v.Length);
        result[v.Length] = Terminator;
        return result;
    }

    // ── Composite key building ────────────────────────────────────────────────

    /// <summary>
    /// Build a composite key from individual values.
    /// Format: [col0_len:4 BE][col0_bytes][col1_len:4 BE][col1_bytes]...
    /// </summary>
    public static byte[] BuildCompositeKey(object?[] values, SqlScalarType[] types)
    {
        int totalLen = 0;
        for (int i = 0; i < values.Length; i++)
            totalLen += 4 + GetSortableLength(values[i], types[i]);

        var key = new byte[totalLen];
        WriteCompositeKey(key, values, types);
        return key;
    }

    /// <summary>
    /// Build an index key from a full row, extracting only the indexed columns.
    /// </summary>
    public static byte[] BuildIndexKey(object?[] row, int[] colIndices, SqlScalarType[] types)
    {
        int totalLen = 0;
        for (int i = 0; i < colIndices.Length; i++)
            totalLen += 4 + GetSortableLength(row[colIndices[i]], types[i]);

        var key = new byte[totalLen];
        int offset = 0;
        for (int i = 0; i < colIndices.Length; i++)
        {
            int len = WriteSortableTo(key.AsSpan(offset + 4), row[colIndices[i]], types[i]);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(offset), len);
            offset += 4 + len;
        }
        return key;
    }

    /// <summary>
    /// Build an index key from a pre-projected value array (values.Length == types.Length).
    /// </summary>
    public static byte[] BuildIndexKey(object?[] values, SqlScalarType[] types)
    {
        int totalLen = 0;
        for (int i = 0; i < values.Length; i++)
            totalLen += 4 + GetSortableLength(values[i], types[i]);

        var key = new byte[totalLen];
        int offset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int len = WriteSortableTo(key.AsSpan(offset + 4), values[i], types[i]);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(offset), len);
            offset += 4 + len;
        }
        return key;
    }

    public static int WriteCompositeKey(Span<byte> dest, object?[] values, SqlScalarType[] types)
    {
        int offset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int len = WriteSortableTo(dest.Slice(offset + 4), values[i], types[i]);
            BinaryPrimitives.WriteInt32BigEndian(dest.Slice(offset), len);
            offset += 4 + len;
        }
        return offset;
    }

    private static int GetSortableLength(object? value, SqlScalarType type)
    {
        if (value == null || value == DBNull.Value)
            return 0;

        return type switch
        {
            SqlScalarType.Int16 => 2,
            SqlScalarType.Int32 => 4,
            SqlScalarType.Int64 or SqlScalarType.Double or SqlScalarType.DateTime
                or SqlScalarType.Date or SqlScalarType.Time => 8,
            SqlScalarType.Decimal => 16,
            SqlScalarType.Boolean => 1,
            SqlScalarType.Guid => 16,
            SqlScalarType.Binary => ((byte[])value).Length + 1,
            _ => GetStringSortableLength(value.ToString() ?? string.Empty)
        };
    }

    private static int GetStringSortableLength(string s)
    {
        int utf8Len = Encoding.UTF8.GetByteCount(s);
        // Check for embedded \0 — rare, but must match WriteSortableTo behavior.
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\0')
                return utf8Len + 1 + 1; // each \0 is escaped as \x00\xFF + terminator
        }
        return utf8Len + 1; // terminator only
    }

    private static int WriteSortableTo(Span<byte> dest, object? value, SqlScalarType type)
    {
        if (value == null || value == DBNull.Value)
            return 0;

        switch (type)
        {
            case SqlScalarType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(dest, Convert.ToInt16(value));
                dest[0] ^= 0x80;
                return 2;
            case SqlScalarType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(dest, Convert.ToInt32(value));
                dest[0] ^= 0x80;
                return 4;
            case SqlScalarType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(dest, Convert.ToInt64(value));
                dest[0] ^= 0x80;
                return 8;
            case SqlScalarType.Double:
            {
                long bits = BitConverter.DoubleToInt64Bits(Convert.ToDouble(value));
                if (bits < 0) bits = ~bits;
                else bits ^= 1L << 63;
                BinaryPrimitives.WriteInt64BigEndian(dest, bits);
                return 8;
            }
            case SqlScalarType.Decimal:
            {
                var v = Convert.ToDecimal(value);
                var bits = decimal.GetBits(v);
                int offset = 0;
                for (int i = 0; i < 4; i++)
                {
                    BinaryPrimitives.WriteInt32BigEndian(dest.Slice(offset), bits[i]);
                    dest[offset] ^= 0x80;
                    offset += 4;
                }
                if (v < 0)
                {
                    for (int i = 0; i < 16; i++)
                        dest[i] ^= 0xFF;
                }
                return 16;
            }
            case SqlScalarType.Boolean:
                dest[0] = (value is bool bv ? bv : Convert.ToBoolean(value)) ? (byte)1 : (byte)0;
                return 1;
            case SqlScalarType.DateTime:
                return WriteInt64Sortable(dest, (value is DateTime dt2 ? dt2 : Convert.ToDateTime(value)).ToUniversalTime().Ticks);
            case SqlScalarType.Date:
                return WriteInt64Sortable(dest, (value is DateTime dt ? dt : Convert.ToDateTime(value)).Date.ToUniversalTime().Ticks);
            case SqlScalarType.Time:
                return WriteInt64Sortable(dest, (value is TimeSpan ts ? ts : TimeSpan.Parse(value.ToString()!)).Ticks);
            case SqlScalarType.Guid:
            {
                var guid = value is Guid g ? g : Guid.Parse(value.ToString()!);
                var bytes = guid.ToByteArray();
                dest[0] = bytes[3]; dest[1] = bytes[2]; dest[2] = bytes[1]; dest[3] = bytes[0];
                dest[4] = bytes[5]; dest[5] = bytes[4];
                dest[6] = bytes[7]; dest[7] = bytes[6];
                bytes.AsSpan(8, 8).CopyTo(dest.Slice(8));
                return 16;
            }
            case SqlScalarType.Binary:
            {
                var raw = (byte[])value;
                raw.AsSpan().CopyTo(dest);
                dest[raw.Length] = Terminator;
                return raw.Length + 1;
            }
            default:
            {
                var s = value is string sv ? sv : value.ToString() ?? string.Empty;
                return WriteStringSortableTo(dest, s);
            }
        }
    }

    private static int WriteInt64Sortable(Span<byte> dest, long v)
    {
        BinaryPrimitives.WriteInt64BigEndian(dest, v);
        dest[0] ^= 0x80;
        return 8;
    }

    private static int WriteStringSortableTo(Span<byte> dest, string s)
    {
        int byteCount = Encoding.UTF8.GetByteCount(s);
        // Fast path: no embedded nulls
        bool hasNull = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\0') { hasNull = true; break; }
        }

        if (!hasNull)
        {
            Encoding.UTF8.GetBytes(s, dest);
            dest[byteCount] = Terminator;
            return byteCount + 1;
        }

        // Escape embedded \0 → \xFF (terminator is \x00\x00)
        int pos = 0;
        for (int i = 0; i < s.Length; i++)
        {
            byte[] utf8Char = Encoding.UTF8.GetBytes(s[i].ToString());
            for (int j = 0; j < utf8Char.Length; j++)
            {
                dest[pos++] = utf8Char[j] == 0 ? (byte)0xFF : utf8Char[j];
            }
        }
        dest[pos++] = Terminator;
        return pos;
    }

    // ── Composite key decoding ────────────────────────────────────────────────

    public static object?[] DecodeCompositeKey(ReadOnlySpan<byte> key, SqlScalarType[] types)
    {
        var values = new object?[types.Length];
        int offset = 0;
        for (int i = 0; i < types.Length; i++)
        {
            if (offset + 4 > key.Length)
                break;

            int len = BinaryPrimitives.ReadInt32BigEndian(key.Slice(offset));
            offset += 4;

            if (len == 0)
            {
                values[i] = null;
            }
            else if (offset + len <= key.Length)
            {
                values[i] = DecodeSortable(key.Slice(offset, len), types[i]);
                offset += len;
            }
        }
        return values;
    }

    // ── Single-value sortable decode ──────────────────────────────────────────

    private static object? DecodeSortable(ReadOnlySpan<byte> bytes, SqlScalarType type)
    {
        if (bytes.IsEmpty)
            return null;

        return type switch
        {
            SqlScalarType.Int32 => Int32FromSortable(bytes),
            SqlScalarType.Int64 => Int64FromSortable(bytes),
            SqlScalarType.Int16 => Int16FromSortable(bytes),
            SqlScalarType.Double => DoubleFromSortable(bytes),
            SqlScalarType.Decimal => DecimalFromSortable(bytes),
            SqlScalarType.Boolean => bytes[0] != 0,
            SqlScalarType.DateTime => DateTimeFromSortable(bytes),
            SqlScalarType.Date => DateTimeFromSortable(bytes).Date,
            SqlScalarType.Time => new TimeSpan(Int64FromSortable(bytes)),
            SqlScalarType.Guid => GuidFromSortable(bytes),
            SqlScalarType.Binary => BinaryFromSortable(bytes),
            _ => StringFromSortable(bytes)
        };
    }

    private static int Int32FromSortable(ReadOnlySpan<byte> b)
    {
        Span<byte> tmp = stackalloc byte[4];
        b.CopyTo(tmp);
        tmp[0] ^= 0x80;
        return BinaryPrimitives.ReadInt32BigEndian(tmp);
    }

    private static long Int64FromSortable(ReadOnlySpan<byte> b)
    {
        Span<byte> tmp = stackalloc byte[8];
        b.CopyTo(tmp);
        tmp[0] ^= 0x80;
        return BinaryPrimitives.ReadInt64BigEndian(tmp);
    }

    private static short Int16FromSortable(ReadOnlySpan<byte> b)
    {
        Span<byte> tmp = stackalloc byte[2];
        b.CopyTo(tmp);
        tmp[0] ^= 0x80;
        return BinaryPrimitives.ReadInt16BigEndian(tmp);
    }

    private static double DoubleFromSortable(ReadOnlySpan<byte> b)
    {
        long bits = BinaryPrimitives.ReadInt64BigEndian(b);
        if (bits < 0)
            bits ^= 1L << 63;
        else
            bits = ~bits;
        return BitConverter.Int64BitsToDouble(bits);
    }

    private static decimal DecimalFromSortable(ReadOnlySpan<byte> b)
    {
        Span<byte> tmp = stackalloc byte[16];
        b.CopyTo(tmp);

        bool negative = (tmp[0] & 0x80) == 0; // after sign-flip, MSB=0 means original was negative
        if (negative)
        {
            for (int i = 0; i < 16; i++)
                tmp[i] ^= 0xFF;
        }

        var components = new int[4];
        int offset = 0;
        for (int i = 0; i < 4; i++)
        {
            tmp[offset] ^= 0x80;
            components[i] = BinaryPrimitives.ReadInt32BigEndian(tmp.Slice(offset));
            offset += 4;
        }
        return new decimal(components);
    }

    private static DateTime DateTimeFromSortable(ReadOnlySpan<byte> b)
    {
        long ticks = Int64FromSortable(b);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static Guid GuidFromSortable(ReadOnlySpan<byte> b)
    {
        var bytes = new byte[16];
        // Reverse the big-endian conversion
        bytes[3] = b[0]; bytes[2] = b[1]; bytes[1] = b[2]; bytes[0] = b[3];
        bytes[5] = b[4]; bytes[4] = b[5];
        bytes[7] = b[6]; bytes[6] = b[7];
        Buffer.BlockCopy(b.ToArray(), 8, bytes, 8, 8);
        return new Guid(bytes);
    }

    private static string StringFromSortable(ReadOnlySpan<byte> b)
    {
        if (b[^1] != Terminator)
            return Encoding.UTF8.GetString(b);
        return Encoding.UTF8.GetString(b.Slice(0, b.Length - 1));
    }

    private static byte[] BinaryFromSortable(ReadOnlySpan<byte> b)
    {
        if (b[^1] != Terminator)
            return b.ToArray();
        return b.Slice(0, b.Length - 1).ToArray();
    }

    // ── Key comparison ────────────────────────────────────────────────────────

    /// <summary>Compare two sortable keys byte-by-byte.</summary>
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    // ── Range bounds from sargable predicates ─────────────────────────────────

    /// <summary>
    /// Build range start/end keys from sargable predicates.
    /// The predicates must be on consecutive leading columns of the index.
    /// </summary>
    public static (byte[] Start, byte[] End, bool StartInclusive, bool EndInclusive)
        BuildRangeBounds(
            List<SargablePredicate> predicates,
            SqlScalarType[] indexKeyTypes,
            object?[]? boundParams = null)
    {
        int prefixCols = predicates.Count; // all equality + one range
        if (prefixCols == 0)
        {
            // No predicates: full index scan
            return (Array.Empty<byte>(), Array.Empty<byte>(), true, true);
        }

        // Build start key: all equality values + range lower bound
        var startValues = new object?[prefixCols];
        var endValues = new object?[prefixCols];
        bool startInclusive = true;
        bool endInclusive = true;

        for (int i = 0; i < predicates.Count; i++)
        {
            var p = predicates[i];
            var value = ResolveValue(p.Value, p.ValueIsParameter, boundParams);

            switch (p.Operator)
            {
                case SqlWhereComparisonOperator.Equal:
                    startValues[i] = value;
                    endValues[i] = value;
                    break;

                case SqlWhereComparisonOperator.GreaterThan:
                    startValues[i] = value;
                    startInclusive = false;
                    endValues[i] = null; // unbounded upper
                    endInclusive = true;
                    break;

                case SqlWhereComparisonOperator.GreaterThanOrEqual:
                    startValues[i] = value;
                    startInclusive = true;
                    endValues[i] = null;
                    break;

                case SqlWhereComparisonOperator.LessThan:
                    endValues[i] = value;
                    endInclusive = false;
                    startValues[i] = null;
                    startInclusive = true;
                    break;

                case SqlWhereComparisonOperator.LessThanOrEqual:
                    endValues[i] = value;
                    endInclusive = true;
                    startValues[i] = null;
                    break;
            }
        }

        // Fast path: when all predicates are equality, build the key once.
        bool allEqual = true;
        for (int i = 0; i < predicates.Count; i++)
        {
            if (predicates[i].Operator != SqlWhereComparisonOperator.Equal)
            { allEqual = false; break; }
        }

        if (allEqual)
        {
            var key = BuildCompositeKey(startValues, indexKeyTypes);
            return (key, key, true, true);
        }

        var startKey = BuildCompositeKey(startValues, indexKeyTypes);
        var endKey = BuildCompositeKey(endValues, indexKeyTypes);

        return (startKey, endKey, startInclusive, endInclusive);
    }

    private const uint IndexEntrySentinel = 0xFFFFFFFE;

    /// <summary>
    /// Build a full index entry key from a row in one allocation, combining
    /// sort-key encoding and the index entry prefix/suffix.
    /// Format: [IndexSentinel:4 LE][indexId:4 LE][sortKey...][tableId:4 LE][rowId:8 LE]
    /// </summary>
    public static byte[] BuildIndexEntryKey(object?[] row, int indexId, int[] colIndices,
        SqlScalarType[] types, int tableId, long rowId)
    {
        int sortKeyLen = 0;
        for (int i = 0; i < colIndices.Length; i++)
            sortKeyLen += 4 + GetSortableLength(row[colIndices[i]], types[i]);

        int totalLen = 4 + 4 + sortKeyLen + 4 + 8;
        var key = new byte[totalLen];

        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(0), IndexEntrySentinel);
        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(4), indexId);

        int offset = 8;
        for (int i = 0; i < colIndices.Length; i++)
        {
            int len = WriteSortableTo(key.AsSpan(offset + 4), row[colIndices[i]], types[i]);
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(offset), len);
            offset += 4 + len;
        }

        BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(offset), tableId);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(key.AsSpan(offset), rowId);

        return key;
    }

    private static object? ResolveValue(object? value, bool isParameter, object?[]? boundParams)
    {
        if (isParameter && value is int idx && boundParams != null && idx >= 0 && idx < boundParams.Length)
            return boundParams[idx];
        return value;
    }
}
