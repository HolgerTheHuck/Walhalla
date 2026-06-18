using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.Core;

namespace WalhallaSql.Storage;

internal sealed class WalLog : IDisposable
{
    private const int Magic = 0x57414C31;
    private const byte Version = 1;

    private readonly string _path;
    private readonly WalSyncMode _syncMode;
    private FileStream? _appendStream;
    private BinaryWriter? _appendWriter;
    // Serializes all append/close/truncate operations against the WAL file.
    // Async append paths are already serialized by GroupCommitQueue; this lock provides
    // defense-in-depth against direct multi-threaded callers of the sync API.
    private readonly object _appendLock = new();

    public WalLog(string path, WalSyncMode syncMode = WalSyncMode.Fsync)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _syncMode = syncMode;
    }

    public void AppendBatch(long transactionId, IReadOnlyList<WalOperation> operations)
    {
        int totalSize = 26; // BeginTransaction header+magic+checksum (no key/value)
        foreach (var op in operations)
            totalSize += 26 + op.Key.Length + (op.Value?.Length ?? 0);
        totalSize += 26; // CommitTransaction

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            using var ms = new MemoryStream(rented, 0, totalSize, true, true);
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            AppendRecord(writer, WalRecordType.BeginTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
            foreach (var op in operations)
                AppendRecord(writer, op.Type, transactionId, op.Key, op.Value);
            AppendRecord(writer, WalRecordType.CommitTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
            writer.Flush();

            lock (_appendLock)
            {
                EnsureAppendWriter();
                _appendStream!.Write(rented, 0, (int)ms.Length);
                _appendWriter!.Flush();
                if (_syncMode == WalSyncMode.Fsync)
                    _appendStream!.Flush(true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async Task AppendBatchAsync(long transactionId, IReadOnlyList<WalOperation> operations,
                                       CancellationToken ct = default)
    {
        int totalSize = 26;
        foreach (var op in operations)
            totalSize += 26 + op.Key.Length + (op.Value?.Length ?? 0);
        totalSize += 26;

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            using var ms = new MemoryStream(rented, 0, totalSize, true, true);
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            AppendRecord(writer, WalRecordType.BeginTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
            foreach (var op in operations)
                AppendRecord(writer, op.Type, transactionId, op.Key, op.Value);
            AppendRecord(writer, WalRecordType.CommitTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
            writer.Flush();

            EnsureAppendWriter();
            _appendWriter!.Flush();
            await _appendStream!.WriteAsync(rented.AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
            if (_syncMode == WalSyncMode.Fsync)
                await _appendStream!.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async Task AppendGroupAsync(
        IReadOnlyList<(long TransactionId, IReadOnlyList<WalOperation> Operations)> group,
        CancellationToken ct = default)
    {
        if (group.Count == 0) return;

        int totalSize = 0;
        foreach (var (_, ops) in group)
        {
            totalSize += 26;
            foreach (var op in ops)
                totalSize += 26 + op.Key.Length + (op.Value?.Length ?? 0);
            totalSize += 26;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            using var ms = new MemoryStream(rented, 0, totalSize, true, true);
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

            foreach (var (txId, ops) in group)
            {
                AppendRecord(writer, WalRecordType.BeginTransaction, txId, ReadOnlySpan<byte>.Empty, null);
                foreach (var op in ops)
                    AppendRecord(writer, op.Type, txId, op.Key, op.Value);
                AppendRecord(writer, WalRecordType.CommitTransaction, txId, ReadOnlySpan<byte>.Empty, null);
            }
            writer.Flush();

            EnsureAppendWriter();
            _appendWriter!.Flush();
            await _appendStream!.WriteAsync(rented.AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
            if (_syncMode == WalSyncMode.Fsync)
                await _appendStream!.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void EnsureAppendWriter()
    {
        if (_appendStream != null) return;

        // NOTE: do NOT set FileOptions.Asynchronous unless the caller actually drives async I/O.
        // On Windows, FileOptions.Asynchronous makes synchronous Write()/Flush() significantly
        // slower (they are emulated on top of overlapped I/O). The synchronous AppendBatch is on
        // the hot insert path; only GroupCommit + WalSyncMode.Fsync uses the async path, and an
        // async wrapper over blocking I/O on the threadpool is still adequate there.
        var fileOptions = FileOptions.None;
        if (_syncMode == WalSyncMode.WriteThrough)
            fileOptions |= FileOptions.WriteThrough;

        _appendStream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                                       FileShare.ReadWrite, bufferSize: 4096, fileOptions);
        _appendWriter = new BinaryWriter(_appendStream);
    }

    private void CloseAppendWriter()
    {
        _appendWriter?.Dispose();
        _appendStream?.Dispose();
        _appendWriter = null;
        _appendStream = null;
    }

    public void Dispose()
    {
        lock (_appendLock)
            CloseAppendWriter();
    }

    internal void AppendRecord(BinaryWriter writer, WalRecordType recordType, long transactionId,
        ReadOnlySpan<byte> key, byte[]? value)
    {
        Span<byte> header = stackalloc byte[1 + 1 + sizeof(long) + sizeof(int) + sizeof(int)];
        var offset = 0;
        header[offset++] = Version;
        header[offset++] = (byte)recordType;
        BinaryPrimitives.WriteInt64LittleEndian(header[offset..], transactionId);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(header[offset..], key.Length);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32LittleEndian(header[offset..], value?.Length ?? -1);

        var checksum = Checksums.Fnva32Begin();
        checksum = Checksums.Fnva32Update(checksum, header);
        checksum = Checksums.Fnva32Update(checksum, key);
        if (value != null)
            checksum = Checksums.Fnva32Update(checksum, value);

        writer.Write(Magic);
        writer.Write(header);
        writer.Write(key);
        if (value != null)
            writer.Write(value);
        writer.Write(checksum);
    }

    public IReadOnlyList<CommittedTransaction> ReadCommittedTransactions()
    {
        if (!File.Exists(_path))
            return Array.Empty<CommittedTransaction>();

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        var pending = new Dictionary<long, List<WalOperation>>();
        var committed = new List<CommittedTransaction>();

        while (TryReadRecord(reader, out var record))
        {
            switch (record.RecordType)
            {
                case WalRecordType.BeginTransaction:
                    pending[record.TransactionId] = new List<WalOperation>();
                    break;
                case WalRecordType.Put:
                case WalRecordType.Delete:
                    if (pending.TryGetValue(record.TransactionId, out var ops))
                        ops.Add(new WalOperation(record.RecordType, record.Key!, record.Value));
                    break;
                case WalRecordType.CommitTransaction:
                    if (pending.TryGetValue(record.TransactionId, out var committedOps))
                    {
                        committed.Add(new CommittedTransaction(record.TransactionId, committedOps.ToArray()));
                        pending.Remove(record.TransactionId);
                    }
                    break;
            }
        }

        return committed;
    }

    internal long SizeBytes
    {
        get
        {
            if (_appendStream != null)
                return _appendStream.Length;
            return File.Exists(_path) ? new FileInfo(_path).Length : 0L;
        }
    }

    public void Truncate()
    {
        lock (_appendLock)
        {
            CloseAppendWriter();
            using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Flush(true);
        }
    }

    internal bool TryReadRecord(BinaryReader reader, out WalRecord record)
    {
        record = default;

        if (reader.BaseStream.Position >= reader.BaseStream.Length)
            return false;

        try
        {
            var magic = reader.ReadInt32();
            if (magic != Magic)
                throw new InvalidDataException("Invalid WAL record magic.");

            var version = reader.ReadByte();
            if (version != Version)
                throw new InvalidDataException($"Unsupported WAL version '{version}'.");

            var recordType = (WalRecordType)reader.ReadByte();
            var transactionId = reader.ReadInt64();
            var keyLength = reader.ReadInt32();
            var valueLength = reader.ReadInt32();

            var key = keyLength > 0 ? reader.ReadBytes(keyLength) : Array.Empty<byte>();
            if (key.Length != keyLength) return false;

            byte[]? value = null;
            if (valueLength >= 0)
            {
                value = reader.ReadBytes(valueLength);
                if (value.Length != valueLength) return false;
            }

            var expectedChecksum = reader.ReadUInt32();

            Span<byte> header = stackalloc byte[1 + 1 + sizeof(long) + sizeof(int) + sizeof(int)];
            var headerOffset = 0;
            header[headerOffset++] = version;
            header[headerOffset++] = (byte)recordType;
            BinaryPrimitives.WriteInt64LittleEndian(header[headerOffset..], transactionId);
            headerOffset += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(header[headerOffset..], keyLength);
            headerOffset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(header[headerOffset..], valueLength);

            var actualChecksum = Checksums.Fnva32Begin();
            actualChecksum = Checksums.Fnva32Update(actualChecksum, header);
            actualChecksum = Checksums.Fnva32Update(actualChecksum, key);
            if (value != null)
                actualChecksum = Checksums.Fnva32Update(actualChecksum, value);
            if (expectedChecksum != actualChecksum)
                throw new InvalidDataException("WAL checksum mismatch.");

            record = new WalRecord(recordType, transactionId, key, value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    internal readonly record struct WalRecord(WalRecordType RecordType, long TransactionId, byte[]? Key, byte[]? Value);

    internal readonly record struct CommittedTransaction(long TransactionId, IReadOnlyList<WalOperation> Operations);
}
