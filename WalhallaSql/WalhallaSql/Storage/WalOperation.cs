using System;

namespace WalhallaSql.Storage;

internal readonly struct WalOperation
{
    public WalOperation(WalRecordType type, byte[] key, byte[]? value)
    {
        if (type is not WalRecordType.Put and not WalRecordType.Delete)
            throw new ArgumentOutOfRangeException(nameof(type), "Operation must be Put or Delete.");

        Type = type;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value;
    }

    public WalRecordType Type { get; }
    public byte[] Key { get; }
    public byte[]? Value { get; }
}
