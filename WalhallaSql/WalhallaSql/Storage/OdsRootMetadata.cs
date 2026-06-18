using System;
using System.Buffers.Binary;

namespace WalhallaSql.Storage;

internal readonly record struct OdsRootMetadata(int RootPageId, int LastAllocatedPageId, uint Magic = 0, int PageSize = 0)
{
    public const uint ExpectedMagic = 0x57484C30; // 'WHL0'
    public const int SizeInBytes = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(int);

    public static OdsRootMetadata Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(int) + sizeof(int))
            throw new ArgumentException("Buffer is too small for ODS metadata.", nameof(buffer));

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (magic == ExpectedMagic)
        {
            if (buffer.Length < SizeInBytes)
                throw new ArgumentException("Buffer is too small for ODS v2 metadata.", nameof(buffer));

            var pageSize = BinaryPrimitives.ReadInt32LittleEndian(buffer[sizeof(uint)..]);
            var rootPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[(sizeof(uint) + sizeof(int))..]);
            var lastAllocatedPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[(sizeof(uint) + sizeof(int) * 2)..]);
            return new OdsRootMetadata(rootPageId, lastAllocatedPageId, magic, pageSize);
        }
        else
        {
            // Legacy format (no magic/page size)
            var rootPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            var lastAllocatedPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[sizeof(int)..]);
            return new OdsRootMetadata(rootPageId, lastAllocatedPageId);
        }
    }

    public void Write(Span<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new ArgumentException("Buffer is too small for ODS metadata.", nameof(buffer));

        if (Magic != ExpectedMagic)
            throw new InvalidOperationException($"Cannot write legacy ODS root metadata. Expected magic 0x{ExpectedMagic:X8}, got 0x{Magic:X8}.");

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[sizeof(uint)..], PageSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[(sizeof(uint) + sizeof(int))..], RootPageId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[(sizeof(uint) + sizeof(int) * 2)..], LastAllocatedPageId);
    }

    public OdsRootMetadata WithRootPageId(int rootPageId) => new(rootPageId, LastAllocatedPageId, Magic, PageSize);
    public OdsRootMetadata WithLastAllocatedPageId(int lastAllocatedPageId) => new(RootPageId, lastAllocatedPageId, Magic, PageSize);
}
