// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Walhalla.Storage.Core.Comparers;

/// <summary>
/// Collection of pre-registered <see cref="IKeyComparator"/> implementations.
/// Set <see cref="Walhalla.Storage.Core.Configuration.WalhallaOptions.KeyComparatorId"/> to one of the
/// <c>*Id</c> constants to activate the desired ordering.
/// </summary>
public static class BuiltInKeyComparators
{
    /// <summary>Identifier for the unsigned bytewise lexicographic comparator (default).</summary>
    public const string BytewiseId = "builtin.bytewise";

    /// <summary>Identifier for the reverse bytewise comparator (descending order).</summary>
    public const string ReverseBytewiseId = "builtin.reverse-bytewise";

    /// <summary>Identifier for the unsigned 64-bit big-endian integer comparator.</summary>
    public const string UnsignedInt64Id = "builtin.uint64-be";

    /// <summary>Identifier for the signed 64-bit big-endian integer comparator.</summary>
    public const string SignedInt64Id = "builtin.int64-be";

    /// <summary>Identifier for the UTF-8 ordinal string comparator.</summary>
    public const string Utf8OrdinalId = "builtin.utf8-ordinal";

    /// <summary>Identifier for the length-first, then bytewise comparator.</summary>
    public const string LengthThenBytewiseId = "builtin.length-then-bytewise";

    /// <summary>Unsigned bytewise lexicographic comparator.  Keys are sorted byte-by-byte as unsigned values.
    /// Shorter arrays sort before longer ones when all leading bytes are equal.  This is the default.</summary>
    public static IKeyComparator Bytewise { get; } = new BytewiseComparator();

    /// <summary>Reverse of <see cref="Bytewise"/>: larger byte sequences sort first.</summary>
    public static IKeyComparator ReverseBytewise { get; } = new ReverseBytewiseComparator();

    /// <summary>Treats each 8-byte key as a big-endian <c>ulong</c> for numeric ordering.
    /// Falls back to bytewise comparison for keys with lengths other than 8 bytes.</summary>
    public static IKeyComparator UnsignedInt64BigEndian { get; } = new UnsignedInt64BigEndianComparator();

    /// <summary>Treats each 8-byte key as a big-endian <c>long</c> for signed numeric ordering.
    /// Falls back to bytewise comparison for keys with lengths other than 8 bytes.</summary>
    public static IKeyComparator SignedInt64BigEndian { get; } = new SignedInt64BigEndianComparator();

    /// <summary>Decodes keys as UTF-8 strings and compares them using <see cref="System.StringComparison.Ordinal"/>.
    /// Keys that are not valid UTF-8 fall back to bytewise comparison to keep the total order stable.</summary>
    public static IKeyComparator Utf8Ordinal { get; } = new Utf8OrdinalComparator();

    /// <summary>Sorts by key length first (shorter keys first), then bytewise within the same length.
    /// Useful for fixed-width encodings padded to the same size.</summary>
    public static IKeyComparator LengthThenBytewise { get; } = new LengthThenBytewiseComparator();

    /// <summary>Dictionary containing all built-in comparators keyed by their <c>Id</c>.</summary>
    public static IReadOnlyDictionary<string, IKeyComparator> All { get; } =
        new Dictionary<string, IKeyComparator>(StringComparer.Ordinal)
        {
            [BytewiseId] = Bytewise,
            [ReverseBytewiseId] = ReverseBytewise,
            [UnsignedInt64Id] = UnsignedInt64BigEndian,
            [SignedInt64Id] = SignedInt64BigEndian,
            [Utf8OrdinalId] = Utf8Ordinal,
            [LengthThenBytewiseId] = LengthThenBytewise
        };

    private sealed class BytewiseComparator : IKeyComparator
    {
        public string Id => BytewiseId;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var minLength = Math.Min(left.Length, right.Length);
            for (var i = 0; i < minLength; i++)
            {
                var cmp = left[i].CompareTo(right[i]);
                if (cmp != 0)
                    return cmp;
            }

            return left.Length.CompareTo(right.Length);
        }
    }

    private sealed class ReverseBytewiseComparator : IKeyComparator
    {
        public string Id => ReverseBytewiseId;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            return -Bytewise.Compare(left, right);
        }
    }

    private sealed class UnsignedInt64BigEndianComparator : IKeyComparator
    {
        public string Id => UnsignedInt64Id;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length == sizeof(ulong) && right.Length == sizeof(ulong))
            {
                var a = BinaryPrimitives.ReadUInt64BigEndian(left);
                var b = BinaryPrimitives.ReadUInt64BigEndian(right);
                return a.CompareTo(b);
            }

            return Bytewise.Compare(left, right);
        }
    }

    private sealed class SignedInt64BigEndianComparator : IKeyComparator
    {
        public string Id => SignedInt64Id;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length == sizeof(long) && right.Length == sizeof(long))
            {
                var a = BinaryPrimitives.ReadInt64BigEndian(left);
                var b = BinaryPrimitives.ReadInt64BigEndian(right);
                return a.CompareTo(b);
            }

            return Bytewise.Compare(left, right);
        }
    }

    private sealed class Utf8OrdinalComparator : IKeyComparator
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public string Id => Utf8OrdinalId;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            try
            {
                var leftText = StrictUtf8.GetString(left);
                var rightText = StrictUtf8.GetString(right);
                return string.CompareOrdinal(leftText, rightText);
            }
            catch (DecoderFallbackException)
            {
                // Keep ordering total and stable even for non-UTF8 payloads.
                return Bytewise.Compare(left, right);
            }
        }
    }

    private sealed class LengthThenBytewiseComparator : IKeyComparator
    {
        public string Id => LengthThenBytewiseId;

        public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var lengthCompare = left.Length.CompareTo(right.Length);
            if (lengthCompare != 0)
                return lengthCompare;

            return Bytewise.Compare(left, right);
        }
    }
}
