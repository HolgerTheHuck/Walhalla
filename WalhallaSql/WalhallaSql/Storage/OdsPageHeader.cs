using System;
using System.Buffers.Binary;

namespace WalhallaSql.Storage;

internal readonly record struct OdsPageHeader(
    int PageId,
    OdsPageType PageType,
    int ParentPageId,
    int RightSiblingPageId,
    int ItemCount)
{
    public const int SizeInBytes = sizeof(int) + sizeof(byte) + sizeof(int) + sizeof(int) + sizeof(int);

    public static OdsPageHeader Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new ArgumentException("Buffer is too small for ODS page header.", nameof(buffer));

        var offset = 0;
        var pageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        offset += sizeof(int);
        var type = (OdsPageType)buffer[offset++];
        var parentPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        offset += sizeof(int);
        var rightSiblingPageId = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
        offset += sizeof(int);
        var itemCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);

        return new OdsPageHeader(pageId, type, parentPageId, rightSiblingPageId, itemCount);
    }

    public void Write(Span<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new ArgumentException("Buffer is too small for ODS page header.", nameof(buffer));

        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], PageId);
        offset += sizeof(int);
        buffer[offset++] = (byte)PageType;
        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], ParentPageId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], RightSiblingPageId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], ItemCount);
    }
}
