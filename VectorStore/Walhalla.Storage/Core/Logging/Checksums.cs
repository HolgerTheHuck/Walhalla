// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;

namespace Walhalla.Storage.Core.Logging;

/// <summary>
/// FNV-1a 32-bit checksum helpers used to detect WAL record corruption on replay.
/// The same algorithm is applied to every WAL record header and payload; a mismatch
/// causes the record (and all subsequent records in the same transaction) to be discarded.
/// </summary>
internal static class Checksums
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>Computes the FNV-1a 32-bit checksum of <paramref name="data"/>.</summary>
    public static uint Fnva32(ReadOnlySpan<byte> data)
    {
        return Fnva32Update(FnvOffset, data);
    }

    /// <summary>Returns the FNV-1a seed value for use with incremental hashing.</summary>
    public static uint Fnva32Begin() => FnvOffset;

    /// <summary>
    /// Incorporates <paramref name="data"/> into an in-progress FNV-1a hash.
    /// Call <see cref="Fnva32Begin"/> to start and chain further calls for multi-segment data.
    /// </summary>
    public static uint Fnva32Update(uint hash, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }
}
