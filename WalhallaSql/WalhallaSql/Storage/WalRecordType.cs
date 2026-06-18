namespace WalhallaSql.Storage;

internal enum WalRecordType : byte
{
    BeginTransaction = 1,
    Put = 2,
    Delete = 3,
    CommitTransaction = 4
}
