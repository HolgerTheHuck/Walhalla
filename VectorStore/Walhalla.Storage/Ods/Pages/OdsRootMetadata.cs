// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;

namespace Walhalla.Storage.Ods.Pages;

/// <summary>
/// Fixed-size metadata stored in page 0 of every ODS file.
/// Records the B+Tree root page and the last page ID that was allocated,
/// allowing the pager to resume correct allocation after a reopen.
/// </summary>
internal readonly record struct OdsRootMetadata(int RootPageId, int LastAllocatedPageId)
{
    public const int SizeInBytes = sizeof(int) + sizeof(int);

    public static OdsRootMetadata Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new ArgumentException("Buffer is too small for ODS metadata.", nameof(buffer));

        var rootPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var lastAllocatedPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[sizeof(int)..]);
        return new OdsRootMetadata(rootPageId, lastAllocatedPageId);
    }

    public void Write(Span<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new ArgumentException("Buffer is too small for ODS metadata.", nameof(buffer));

        BinaryPrimitives.WriteInt32LittleEndian(buffer, RootPageId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[sizeof(int)..], LastAllocatedPageId);
    }
}
