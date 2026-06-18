using System;
using System.Threading;

namespace WalhallaSql.Storage;

internal sealed class AsyncLock : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    public ReadLockToken ReadLock()
    {
        _lock.EnterReadLock();
        return new ReadLockToken(_lock);
    }

    public WriteLockToken WriteLock()
    {
        _lock.EnterWriteLock();
        return new WriteLockToken(_lock);
    }

    public void Dispose() => _lock.Dispose();

    internal readonly struct ReadLockToken : IDisposable
    {
        private readonly ReaderWriterLockSlim? _lock;
        internal ReadLockToken(ReaderWriterLockSlim l) => _lock = l;
        public void Dispose() => _lock?.ExitReadLock();
    }

    internal readonly struct WriteLockToken : IDisposable
    {
        private readonly ReaderWriterLockSlim? _lock;
        internal WriteLockToken(ReaderWriterLockSlim l) => _lock = l;
        public void Dispose() => _lock?.ExitWriteLock();
    }
}
