namespace WalhallaSql.Core;

public enum MemTableMode
{
    InMemory = 0,
    OnDiskBPlusTree = 1,
    Hybrid = 2
}
