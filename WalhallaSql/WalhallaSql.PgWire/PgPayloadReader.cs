using System.Buffers.Binary;
using System.Text;

namespace WalhallaSql.PgWire;

public sealed class PgPayloadReader
{
    private readonly byte[] _payload;
    private int _offset;

    public PgPayloadReader(byte[] payload)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _offset = 0;
    }

    public byte ReadByte()
    {
        EnsureRemaining(1);
        return _payload[_offset++];
    }

    public short ReadInt16()
    {
        EnsureRemaining(2);
        var value = BinaryPrimitives.ReadInt16BigEndian(_payload.AsSpan(_offset, 2));
        _offset += 2;
        return value;
    }

    public int ReadInt32()
    {
        EnsureRemaining(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_payload.AsSpan(_offset, 4));
        _offset += 4;
        return value;
    }

    public string ReadCString()
    {
        var start = _offset;
        while (_offset < _payload.Length && _payload[_offset] != 0)
            _offset++;

        if (_offset >= _payload.Length)
            throw new InvalidOperationException("Unterminated C-string in frontend payload.");

        var text = Encoding.UTF8.GetString(_payload, start, _offset - start);
        _offset++;
        return text;
    }

    public byte[] ReadBytes(int count)
    {
        EnsureRemaining(count);
        var buffer = new byte[count];
        Buffer.BlockCopy(_payload, _offset, buffer, 0, count);
        _offset += count;
        return buffer;
    }

    private void EnsureRemaining(int count)
    {
        if (_offset + count > _payload.Length)
            throw new InvalidOperationException("Frontend payload ended unexpectedly.");
    }
}
