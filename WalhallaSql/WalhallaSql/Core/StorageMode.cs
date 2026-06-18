namespace WalhallaSql.Core;

public enum StorageMode
{
    BPlusTree = 0,
    InMemory = 1,
    MvccBPlusTree = 2
}
