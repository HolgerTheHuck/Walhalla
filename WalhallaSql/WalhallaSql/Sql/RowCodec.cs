using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using WalhallaSql.Storage;

namespace WalhallaSql.Sql;

internal static class RowCodec
{
    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;

    private static readonly object[] BoxedInt32Cache = BuildBoxedInt32Cache();
    private const int BoxedInt32CacheOffset = 16;
    private const int BoxedInt32CacheSize = 288;

    private static object[] BuildBoxedInt32Cache()
    {
        var cache = new object[BoxedInt32CacheSize];
        for (int i = 0; i < cache.Length; i++)
            cache[i] = i - BoxedInt32CacheOffset;
        return cache;
    }

    private static object BoxInt32(int value)
    {
        int idx = value + BoxedInt32CacheOffset;
        if ((uint)idx < (uint)BoxedInt32CacheSize)
            return BoxedInt32Cache[idx];
        return value;
    }

    private static readonly object[] BoxedInt64Cache = BuildBoxedInt64Cache();
    private const int BoxedInt64CacheOffset = 16;
    private const int BoxedInt64CacheSize = 288;

    private static object[] BuildBoxedInt64Cache()
    {
        var cache = new object[BoxedInt64CacheSize];
        for (int i = 0; i < cache.Length; i++)
            cache[i] = (long)(i - BoxedInt64CacheOffset);
        return cache;
    }

    private static object BoxInt64(long value)
    {
        if (value >= -BoxedInt64CacheOffset && value < BoxedInt64CacheSize - BoxedInt64CacheOffset)
        {
            var idx = (int)(value + BoxedInt64CacheOffset);
            return BoxedInt64Cache[idx];
        }
        return value;
    }

    // ── Encode ────────────────────────────────────────────────────────────────

    public static byte[] Encode(object?[] row, SqlTableDefinition table)
    {
        var columns = table.Columns;
        int colCount = columns.Count;
        int bitmapSize = Math.Max(1, (colCount + 7) / 8);

        // Phase 1: compute total encoded size.
        int totalValueBytes = 0;
        for (int i = 0; i < colCount; i++)
        {
            var value = row[i];
            if (value != null)
                totalValueBytes += GetEncodedSize(value, columns[i].Type);
        }

        int totalSize = 1 + bitmapSize + totalValueBytes;

        // Phase 2: write directly into result array.
        var result = new byte[totalSize];
        int pos = 0;
        result[pos++] = (byte)bitmapSize;

        // Null bitmap
        result.AsSpan(pos, bitmapSize).Clear();
        int bitmapOffset = pos;
        pos += bitmapSize;

        // Values
        for (int i = 0; i < colCount; i++)
        {
            var value = row[i];
            if (value == null)
            {
                result[bitmapOffset + i / 8] |= (byte)(1 << (i % 8));
            }
            else
            {
                pos += EncodeValueInto(value, columns[i].Type, result.AsSpan(pos));
            }
        }

        return result;
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    public static object?[] DecodeToArray(ReadOnlySpan<byte> bytes, SqlTableDefinition table)
    {
        var columns = table.Columns;
        int colCount = columns.Count;

        var values = new object?[colCount];
        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;
            if (isNull)
            {
                values[i] = null;
            }
            else
            {
                values[i] = DecodeValue(bytes, ref pos, columns[i].Type);
            }
        }

        return values;
    }

    public static object?[] DecodeToArray(byte[] bytes, SqlTableDefinition table)
        => DecodeToArray(bytes.AsSpan(), table);

    /// <summary>Decodes a row into a pre-allocated buffer (size ≥ table.Columns.Count).</summary>
    public static void DecodeToBuffer(ReadOnlySpan<byte> bytes, SqlTableDefinition table, object?[] buffer)
    {
        var columns = table.Columns;
        int colCount = columns.Count;

        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;
            buffer[i] = isNull ? null : DecodeValue(bytes, ref pos, columns[i].Type);
        }
    }

    /// <summary>Rents a buffer from the shared ArrayPool and decodes into it.</summary>
    public static object?[] DecodeToPooledArray(ReadOnlySpan<byte> bytes, SqlTableDefinition table)
    {
        var buffer = ArrayPool<object?>.Shared.Rent(table.Columns.Count);
        DecodeToBuffer(bytes, table, buffer);
        return buffer;
    }

    /// <summary>Returns a rented array to the shared ArrayPool (clear first to avoid GC rooting).</summary>
    public static void ReturnPooledArray(object?[] array)
    {
        ArrayPool<object?>.Shared.Return(array, clearArray: true);
    }

    public static object?[] DecodeToArrayLazy(byte[] bytes, SqlTableDefinition table)
    {
        var columns = table.Columns;
        int colCount = columns.Count;

        var values = new object?[colCount];
        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;
            if (isNull)
            {
                values[i] = null;
            }
            else
            {
                values[i] = DecodeValueLazy(bytes, ref pos, columns[i].Type);
            }
        }

        return values;
    }

    public static object?[] DecodeColumns(ReadOnlySpan<byte> bytes, SqlTableDefinition table, int[] columnIndices)
    {
        var columns = table.Columns;
        int colCount = columns.Count;
        var result = new object?[columnIndices.Length];

        // Build reverse map: column index → output index (-1 = not projected)
        var colToOutput = new int[colCount];
        for (int i = 0; i < colCount; i++)
            colToOutput[i] = -1;
        for (int i = 0; i < columnIndices.Length; i++)
        {
            var colIdx = columnIndices[i];
            if (colIdx >= 0)
                colToOutput[colIdx] = i;
        }

        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            int outputIdx = colToOutput[i];
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;

            if (isNull)
            {
                if (outputIdx >= 0) result[outputIdx] = null;
                continue;
            }

            if (outputIdx >= 0)
            {
                result[outputIdx] = DecodeValue(bytes, ref pos, columns[i].Type);
            }
            else
            {
                SkipValue(bytes, ref pos, columns[i].Type);
            }
        }

        return result;
    }

    public static object?[] DecodeColumnsLazy(byte[] bytes, SqlTableDefinition table, int[] columnIndices)
    {
        var columns = table.Columns;
        int colCount = columns.Count;
        var result = new object?[columnIndices.Length];

        // Build reverse map: column index → output index (-1 = not projected)
        var colToOutput = new int[colCount];
        for (int i = 0; i < colCount; i++)
            colToOutput[i] = -1;
        for (int i = 0; i < columnIndices.Length; i++)
        {
            var colIdx = columnIndices[i];
            if (colIdx >= 0)
                colToOutput[colIdx] = i;
        }

        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            int outputIdx = colToOutput[i];
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;

            if (isNull)
            {
                if (outputIdx >= 0) result[outputIdx] = null;
                continue;
            }

            if (outputIdx >= 0)
            {
                result[outputIdx] = DecodeValueLazy(bytes, ref pos, columns[i].Type);
            }
            else
            {
                SkipValue(bytes, ref pos, columns[i].Type);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a decodeIndexToOutputIndex array (size = colCount) that maps each
    /// column index to a dense output index for only the requested columns.
    /// Columns not in <paramref name="columnIndices"/> get -1 (skipped).
    /// </summary>
    public static int[] BuildColumnIndexMap(SqlTableDefinition table, int[] columnIndices)
    {
        var colCount = table.Columns.Count;
        var map = new int[colCount];
        for (int i = 0; i < colCount; i++)
            map[i] = -1;
        foreach (var colIdx in columnIndices)
        {
            if (colIdx >= 0 && colIdx < colCount)
                map[colIdx] = colIdx;
        }
        return map;
    }

    public static void DecodeColumnsToRowBuffer(
        byte[] bytes, SqlTableDefinition table, object?[] outputValues,
        int[] decodeIndexToOutputIndex, bool lazyBlobs = false)
        => DecodeColumnsToRowBuffer(bytes.AsSpan(), table, outputValues, decodeIndexToOutputIndex, lazyBlobs);

    public static void DecodeColumnsToRowBuffer(
        ReadOnlySpan<byte> bytes, SqlTableDefinition table, object?[] outputValues,
        int[] decodeIndexToOutputIndex, bool lazyBlobs = false)
    {
        var columns = table.Columns;
        int colCount = columns.Count;

        int pos = 0;
        int bitmapSize = bytes[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            bool isNull = (bytes[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;

            var outputIdx = decodeIndexToOutputIndex[i];

            if (isNull)
            {
                if (outputIdx >= 0) outputValues[outputIdx] = null;
                continue;
            }

            if (outputIdx >= 0)
            {
                outputValues[outputIdx] = lazyBlobs
                    ? DecodeValueLazy(bytes, ref pos, columns[i].Type)
                    : DecodeValue(bytes, ref pos, columns[i].Type);
            }
            else
            {
                SkipValue(bytes, ref pos, columns[i].Type);
            }
        }
    }

    // ── Encode single value ──────────────────────────────────────────────────

    private static int GetEncodedSize(object value, SqlScalarType type)
    {
        switch (type)
        {
            case SqlScalarType.Int16: return 2;
            case SqlScalarType.Int32: return 4;
            case SqlScalarType.Int64:
            case SqlScalarType.Double:
            case SqlScalarType.DateTime:
            case SqlScalarType.Date:
            case SqlScalarType.Time: return 8;
            case SqlScalarType.Decimal: return 16;
            case SqlScalarType.Boolean: return 1;
            case SqlScalarType.Binary when value is BlobRef: return 4 + BlobRef.SizeInBytes;
            case SqlScalarType.Binary when value is byte[] raw: return 4 + raw.Length;
            case SqlScalarType.Guid:
            {
                var guid = value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                var s = guid.ToString("D", CultureInfo.InvariantCulture);
                return 4 + Encoding.UTF8.GetByteCount(s);
            }
            default:
            {
                var s = value is string sv ? sv : (value?.ToString() ?? string.Empty);
                return 4 + Encoding.UTF8.GetByteCount(s);
            }
        }
    }

    private static int EncodeValueInto(object value, SqlScalarType type, Span<byte> dest)
    {
        switch (type)
        {
            case SqlScalarType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(dest, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                return 4;
            case SqlScalarType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(dest, Convert.ToInt16(value, CultureInfo.InvariantCulture));
                return 2;
            case SqlScalarType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(dest, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return 8;
            case SqlScalarType.Double:
            {
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                BinaryPrimitives.WriteInt64LittleEndian(dest, BitConverter.DoubleToInt64Bits(d));
                return 8;
            }
            case SqlScalarType.Decimal:
            {
                var bits = decimal.GetBits(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                for (var i = 0; i < bits.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(i * 4, 4), bits[i]);
                return 16;
            }
            case SqlScalarType.Boolean:
            {
                bool b = value is bool bv ? bv : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                dest[0] = b ? (byte)1 : (byte)0;
                return 1;
            }
            case SqlScalarType.DateTime:
            {
                DateTime dt;
                if (value is DateTime d) dt = d;
                else if (value is string s) dt = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                else dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
                BinaryPrimitives.WriteInt64LittleEndian(dest, dt.ToBinary());
                return 8;
            }
            case SqlScalarType.Date:
            {
                DateTime dt;
                if (value is DateTime d) dt = d.Date;
                else if (value is string s) dt = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Date;
                else dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date;
                BinaryPrimitives.WriteInt64LittleEndian(dest, dt.ToBinary());
                return 8;
            }
            case SqlScalarType.Time:
            {
                var ts = value is TimeSpan t ? t
                    : value is string s ? TimeSpan.Parse(s, CultureInfo.InvariantCulture)
                    : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture);
                BinaryPrimitives.WriteInt64LittleEndian(dest, ts.Ticks);
                return 8;
            }
            case SqlScalarType.Binary when value is BlobRef blobRef:
                BinaryPrimitives.WriteUInt32LittleEndian(dest, BlobRef.Sentinel);
                blobRef.Encode().AsSpan().CopyTo(dest.Slice(4));
                return 4 + BlobRef.SizeInBytes;
            case SqlScalarType.Binary when value is byte[] raw:
                BinaryPrimitives.WriteInt32LittleEndian(dest, raw.Length);
                raw.AsSpan().CopyTo(dest.Slice(4));
                return 4 + raw.Length;
            case SqlScalarType.Guid:
            {
                var guid = value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                var s = guid.ToString("D", CultureInfo.InvariantCulture);
                return EncodeStringInto(s, dest);
            }
            default:
            {
                var s = value is string sv ? sv : (value?.ToString() ?? string.Empty);
                return EncodeStringInto(s, dest);
            }
        }
    }

    private static int EncodeStringInto(string s, Span<byte> dest)
    {
        int byteCount = Encoding.UTF8.GetByteCount(s);
        BinaryPrimitives.WriteInt32LittleEndian(dest, byteCount);
        Encoding.UTF8.GetBytes(s, dest.Slice(4));
        return 4 + byteCount;
    }

    // ── Encode single value (legacy, kept for other callers) ─────────────────

    private static byte[] EncodeValue(object value, SqlScalarType type)
    {
        switch (type)
        {
            case SqlScalarType.Int32:
                return BitConverter.GetBytes(Convert.ToInt32(value, CultureInfo.InvariantCulture));

            case SqlScalarType.Int16:
                return BitConverter.GetBytes(Convert.ToInt16(value, CultureInfo.InvariantCulture));

            case SqlScalarType.Int64:
                return BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture));

            case SqlScalarType.Double:
                return BitConverter.GetBytes(Convert.ToDouble(value, CultureInfo.InvariantCulture));

            case SqlScalarType.Decimal:
            {
                var bits = decimal.GetBits(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                var result = new byte[16];
                for (var i = 0; i < bits.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(i * 4, 4), bits[i]);
                return result;
            }

            case SqlScalarType.Boolean:
            {
                bool b = value is bool bv ? bv : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return new[] { b ? (byte)1 : (byte)0 };
            }

            case SqlScalarType.DateTime:
            {
                DateTime dt;
                if (value is DateTime d) dt = d;
                else if (value is string s) dt = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                else dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
                return BitConverter.GetBytes(dt.ToBinary());
            }

            case SqlScalarType.Date:
            {
                DateTime dt;
                if (value is DateTime d) dt = d.Date;
                else if (value is string s) dt = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Date;
                else dt = Convert.ToDateTime(value, CultureInfo.InvariantCulture).Date;
                return BitConverter.GetBytes(dt.ToBinary());
            }

            case SqlScalarType.Time:
            {
                var ts = value is TimeSpan t ? t
                    : value is string s ? TimeSpan.Parse(s, CultureInfo.InvariantCulture)
                    : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture);
                return BitConverter.GetBytes(ts.Ticks);
            }

            case SqlScalarType.Binary when value is byte[] raw:
                return EncodeLengthPrefixed(raw);

            case SqlScalarType.Guid:
            {
                var guid = value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return EncodeString(guid.ToString("D", CultureInfo.InvariantCulture));
            }

            default:
                return EncodeString(value is string sv ? sv : (value?.ToString() ?? string.Empty));
        }
    }

    // ── Decode value ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object DecodeValue(ReadOnlySpan<byte> bytes, ref int pos, SqlScalarType type)
    {
        switch (type)
        {
            case SqlScalarType.Int32:
            {
                var v = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                return BoxInt32(v);
            }
            case SqlScalarType.Int16:
            {
                var v = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(pos, 2));
                pos += 2;
                return (short)v;
            }
            case SqlScalarType.Int64:
            {
                var v = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return BoxInt64(v);
            }
            case SqlScalarType.Double:
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return BitConverter.Int64BitsToDouble(raw);
            }
            case SqlScalarType.Decimal:
            {
                Span<int> bits = stackalloc int[4];
                for (var i = 0; i < 4; i++)
                    bits[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + (i * 4), 4));
                pos += 16;
                return new decimal(bits);
            }
            case SqlScalarType.Boolean:
            {
                var v = bytes[pos] != 0;
                pos += 1;
                return v ? BoxedTrue : BoxedFalse;
            }
            case SqlScalarType.DateTime:
            {
                var binary = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return DateTime.FromBinary(binary);
            }
            case SqlScalarType.Date:
            {
                var binary = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return DateTime.FromBinary(binary).Date;
            }
            case SqlScalarType.Time:
            {
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return new TimeSpan(ticks);
            }
            case SqlScalarType.Binary:
            {
                var lenU = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
                if (lenU == BlobRef.Sentinel)
                {
                    pos += 4;
                    var blobRef = BlobRef.Decode(bytes.Slice(pos, BlobRef.SizeInBytes));
                    pos += BlobRef.SizeInBytes;
                    return blobRef;
                }
                return DecodeLengthPrefixed(bytes, ref pos);
            }
            case SqlScalarType.Guid:
                return Guid.Parse(DecodeString(bytes, ref pos));
            default:
                return DecodeString(bytes, ref pos);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object DecodeValueLazy(byte[] bytes, ref int pos, SqlScalarType type)
        => DecodeValueLazy(bytes.AsSpan(), ref pos, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object DecodeValueLazy(ReadOnlySpan<byte> bytes, ref int pos, SqlScalarType type)
    {
        switch (type)
        {
            case SqlScalarType.Int32:
            {
                var v = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                return BoxInt32(v);
            }
            case SqlScalarType.Int16:
            {
                var v = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(pos, 2));
                pos += 2;
                return (short)v;
            }
            case SqlScalarType.Int64:
            {
                var v = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return BoxInt64(v);
            }
            case SqlScalarType.Double:
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return BitConverter.Int64BitsToDouble(raw);
            }
            case SqlScalarType.Decimal:
            {
                Span<int> bits = stackalloc int[4];
                for (var i = 0; i < 4; i++)
                    bits[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + (i * 4), 4));
                pos += 16;
                return new decimal(bits);
            }
            case SqlScalarType.Boolean:
            {
                var v = bytes[pos] != 0;
                pos += 1;
                return v ? BoxedTrue : BoxedFalse;
            }
            case SqlScalarType.DateTime:
            {
                var binary = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return DateTime.FromBinary(binary);
            }
            case SqlScalarType.Date:
            {
                var binary = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return DateTime.FromBinary(binary).Date;
            }
            case SqlScalarType.Time:
            {
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                return new TimeSpan(ticks);
            }
            case SqlScalarType.Binary:
            {
                var lenU = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
                if (lenU == BlobRef.Sentinel)
                {
                    pos += 4;
                    var blobRef = BlobRef.Decode(bytes.Slice(pos, BlobRef.SizeInBytes));
                    pos += BlobRef.SizeInBytes;
                    // Return raw BlobRef; TableStore will resolve to PendingBlobValue.
                    return blobRef;
                }
                var len = (int)lenU;
                pos += 4;
                var sentinel = new PendingBlobValue(bytes.Slice(pos, len));
                pos += len;
                return sentinel;
            }
            case SqlScalarType.Guid:
                return Guid.Parse(DecodeString(bytes, ref pos));
            default:
                return DecodeString(bytes, ref pos);
        }
    }

    // ── Skip ─────────────────────────────────────────────────────────────────

    private static void SkipValue(ReadOnlySpan<byte> bytes, ref int pos, SqlScalarType type)
    {
        switch (type)
        {
            case SqlScalarType.Int16:
                pos += 2; return;
            case SqlScalarType.Int32:
                pos += 4; return;
            case SqlScalarType.Int64:
            case SqlScalarType.Double:
            case SqlScalarType.DateTime:
            case SqlScalarType.Date:
            case SqlScalarType.Time:
                pos += 8; return;
            case SqlScalarType.Decimal:
                pos += 16; return;
            case SqlScalarType.Boolean:
                pos += 1; return;
            default:
            {
                var lenU = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
                if (lenU == BlobRef.Sentinel)
                {
                    pos += 4 + BlobRef.SizeInBytes;
                    return;
                }
                pos += 4 + (int)lenU;
                return;
            }
        }
    }

    // ── String/binary helpers ─────────────────────────────────────────────────

    private static byte[] EncodeString(string s)
    {
        var strBytes = Encoding.UTF8.GetBytes(s);
        var result = new byte[4 + strBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, strBytes.Length);
        Buffer.BlockCopy(strBytes, 0, result, 4, strBytes.Length);
        return result;
    }

    // ── String cache ────────────────────────────────────────────────────────
    // 1-entry cache: returns same instance for consecutive identical values.
    private static string? _cachedString;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string DecodeString(ReadOnlySpan<byte> bytes, ref int pos)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
        pos += 4;
        var s = Encoding.UTF8.GetString(bytes.Slice(pos, len));
        pos += len;

        // For low-cardinality columns, the same value repeats. String.Equals
        // exits on first mismatch (or length mismatch), so cost is minimal
        // for unique values (just the function call overhead).
        if (s.Length == _cachedString?.Length && string.Equals(s, _cachedString, StringComparison.Ordinal))
            return _cachedString;

        _cachedString = s;
        return s;
    }

    private static byte[] EncodeLengthPrefixed(byte[] raw)
    {
        var result = new byte[4 + raw.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, raw.Length);
        Buffer.BlockCopy(raw, 0, result, 4, raw.Length);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] DecodeLengthPrefixed(ReadOnlySpan<byte> bytes, ref int pos)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
        pos += 4;
        var raw = bytes.Slice(pos, len).ToArray();
        pos += len;
        return raw;
    }

    // ── Predicate decode with RawStringRef reuse ────────────────────────────

    /// <summary>
    /// Decodes predicate columns into a reusable buffer. String columns that have
    /// a <see cref="RawStringRef"/> pre-placed in the buffer are updated in-place
    /// without allocating a new string.
    /// </summary>
    public static void DecodePredicateColumns(
        byte[] bytes, SqlTableDefinition table, object?[] outputValues,
        int[] decodeIndexToOutputIndex)
    {
        DecodePredicateColumnsImpl(bytes.AsSpan(), bytes, 0, table, outputValues, decodeIndexToOutputIndex);
    }

    /// <summary>
    /// Zero-copy variant for page-backed storage. Reads from <paramref name="owner"/>
    /// starting at <paramref name="startOffset"/> for <paramref name="length"/> bytes.
    /// RawStringRef references the page buffer directly — no per-row byte[] allocation.
    /// </summary>
    public static void DecodePredicateColumns(
        byte[] owner, int startOffset, int length,
        SqlTableDefinition table, object?[] outputValues,
        int[] decodeIndexToOutputIndex)
    {
        DecodePredicateColumnsImpl(owner.AsSpan(startOffset, length), owner, startOffset,
            table, outputValues, decodeIndexToOutputIndex);
    }

    private static void DecodePredicateColumnsImpl(
        ReadOnlySpan<byte> span, byte[] owner, int ownerBaseOffset,
        SqlTableDefinition table, object?[] outputValues,
        int[] decodeIndexToOutputIndex)
    {
        var columns = table.Columns;
        int colCount = columns.Count;

        int pos = 0;
        int bitmapSize = span[pos++];
        int bitmapOffset = pos;
        pos += bitmapSize;

        for (int i = 0; i < colCount; i++)
        {
            bool isNull = (span[bitmapOffset + i / 8] & (1 << (i % 8))) != 0;
            var outputIdx = decodeIndexToOutputIndex[i];

            if (isNull)
            {
                if (outputIdx >= 0) outputValues[outputIdx] = null;
                continue;
            }

            if (outputIdx >= 0)
            {
                if (outputValues[outputIdx] is RawStringRef existing)
                {
                    var strLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
                    existing.Set(owner, ownerBaseOffset + pos + 4, strLen);
                    pos += 4 + strLen;
                }
                else
                {
                    outputValues[outputIdx] = DecodeValue(span, ref pos, columns[i].Type);
                }
            }
            else
            {
                SkipValue(span, ref pos, columns[i].Type);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SkipString(ReadOnlySpan<byte> bytes, ref int pos)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
        pos += 4 + len;
    }
}

/// <summary>
/// Mutable wrapper around raw encoded string bytes. Reused across rows during
/// predicate evaluation to avoid per-row string allocations.
/// </summary>
internal sealed class RawStringRef
{
    private byte[] _owner = null!;
    private int _offset;
    private int _length;
    private string? _decoded; // lazily decoded

    internal RawStringRef() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(byte[] owner, int offset, int length)
    {
        _owner = owner;
        _offset = offset;
        _length = length;
        _decoded = null;
    }

    public override bool Equals(object? obj)
    {
        if (obj is string s)
            return RawStringEquals(s);
        if (obj is RawStringRef other)
            return RawBytesEqual(other);
        return false;
    }

    public override int GetHashCode()
    {
        // Quick hash from first bytes
        int h = _length;
        int end = Math.Min(_offset + _length, _offset + 8);
        for (int i = _offset; i < end; i++)
            h = (h * 31) + _owner[i];
        return h;
    }

    public override string? ToString() => _decoded ??= Encoding.UTF8.GetString(_owner, _offset, _length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RawStringEquals(string other)
    {
        int otherLen = Encoding.UTF8.GetByteCount(other);
        if (otherLen != _length) return false;

        byte[]? rented = null;
        Span<byte> buf = otherLen <= 256 ? stackalloc byte[otherLen] : (rented = ArrayPool<byte>.Shared.Rent(otherLen));
        Encoding.UTF8.GetBytes(other, buf);
        bool equal = buf.SequenceEqual(_owner.AsSpan(_offset, _length));
        if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        return equal;
    }

    /// <summary>Case-insensitive byte comparison against a string.</summary>
    internal int CompareTo(string other)
    {
        int otherLen = Encoding.UTF8.GetByteCount(other);

        byte[]? rented = null;
        Span<byte> otherBytes = otherLen <= 256 ? stackalloc byte[otherLen] : (rented = ArrayPool<byte>.Shared.Rent(otherLen));
        Encoding.UTF8.GetBytes(other, otherBytes);

        int minLen = Math.Min(_length, otherLen);
        var span = _owner.AsSpan(_offset, _length);
        for (int i = 0; i < minLen; i++)
        {
            byte lb = span[i];
            byte rb = otherBytes[i];
            if (lb != rb)
            {
                // ASCII case folding: 'A'-'Z' (65-90) ↔ 'a'-'z' (97-122)
                if (lb is >= 65 and <= 90) lb = (byte)(lb + 32);
                if (rb is >= 65 and <= 90) rb = (byte)(rb + 32);
                if (lb != rb)
                {
                    int result = lb < rb ? -1 : 1;
                    if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                    return result;
                }
            }
        }

        int cmpResult = _length.CompareTo(otherLen);
        if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        return cmpResult;
    }

    /// <summary>Collation-aware comparison. Falls back to <see cref="CompareTo(string)"/> for null collation.</summary>
    internal int CompareTo(string other, CompareInfo? collation)
    {
        if (collation == null)
            return CompareTo(other);
        return WalhallaSql.Collation.CollationManager.Compare(ToString()!, other, collation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RawBytesEqual(RawStringRef other)
        => _length == other._length
        && _owner.AsSpan(_offset, _length).SequenceEqual(other._owner.AsSpan(other._offset, other._length));
}
