// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Walhalla.Storage.Core.Logging;

internal sealed class WalLog : IDisposable
{
    private const int Magic = 0x57414C31; // WAL1
    private const byte Version = 1;

    private readonly string _path;
    private readonly Configuration.WalSyncMode _syncMode;
    private FileStream? _appendStream;
    private BinaryWriter? _appendWriter;

    public WalLog(string path, Configuration.WalSyncMode syncMode = Configuration.WalSyncMode.Fsync)
    {
        _path     = path ?? throw new ArgumentNullException(nameof(path));
        _syncMode = syncMode;
    }

    public void AppendBatch(long transactionId, IReadOnlyList<WalOperation> operations)
    {
        EnsureAppendWriter();

        AppendRecord(_appendWriter!, WalRecordType.BeginTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);

        foreach (var operation in operations)
            AppendRecord(_appendWriter!, operation.Type, transactionId, operation.Key, operation.Value);

        AppendRecord(_appendWriter!, WalRecordType.CommitTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
        _appendWriter!.Flush();
        if (_syncMode == Configuration.WalSyncMode.Fsync)
            _appendStream!.Flush(true);
    }

    /// <summary>
    /// Serialises the batch in-memory, then flushes to disk with a single async write.
    /// </summary>
    public async Task AppendBatchAsync(long transactionId, IReadOnlyList<WalOperation> operations,
                                       CancellationToken ct = default)
    {
        // Build the entire record set synchronously into a MemoryStream (fast, no disk I/O).
        using var ms = new MemoryStream(256 + operations.Count * 64);
        using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        AppendRecord(writer, WalRecordType.BeginTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
        foreach (var op in operations)
            AppendRecord(writer, op.Type, transactionId, op.Key, op.Value);
        AppendRecord(writer, WalRecordType.CommitTransaction, transactionId, ReadOnlySpan<byte>.Empty, null);
        writer.Flush();

        var bytes = ms.GetBuffer();
        var length = (int)ms.Length;

        EnsureAppendWriter();
        _appendWriter!.Flush();   // flush any pending BinaryWriter buffer state
        await _appendStream!.WriteAsync(bytes.AsMemory(0, length), ct).ConfigureAwait(false);
        if (_syncMode == Configuration.WalSyncMode.Fsync)
            await _appendStream!.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Serialises all transactions in <paramref name="group"/> into one in-memory buffer and
    /// persists them to disk with a <b>single</b> async write + fsync.
    /// Called by the group-commit flush loop; each call covers one or more transactions, reducing
    /// the number of fsyncs proportionally to the current write concurrency.
    /// </summary>
    public async Task AppendGroupAsync(
        IReadOnlyList<(long TransactionId, IReadOnlyList<WalOperation> Operations)> group,
        CancellationToken ct = default)
    {
        if (group.Count == 0) return;

        // Estimate a reasonable initial buffer: ~300 B overhead per transaction + ~64 B per operation.
        var estimatedSize = group.Count * 300 + group.Sum(g => g.Operations.Count * 64);
        using var ms     = new MemoryStream(estimatedSize);
        using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        foreach (var (txId, ops) in group)
        {
            AppendRecord(writer, WalRecordType.BeginTransaction,  txId, ReadOnlySpan<byte>.Empty, null);
            foreach (var op in ops)
                AppendRecord(writer, op.Type, txId, op.Key, op.Value);
            AppendRecord(writer, WalRecordType.CommitTransaction, txId, ReadOnlySpan<byte>.Empty, null);
        }
        writer.Flush();

        var bytes  = ms.GetBuffer();
        var length = (int)ms.Length;

        EnsureAppendWriter();
        _appendWriter!.Flush();
        await _appendStream!.WriteAsync(bytes.AsMemory(0, length), ct).ConfigureAwait(false);
        if (_syncMode == Configuration.WalSyncMode.Fsync)
            await _appendStream!.FlushAsync(ct).ConfigureAwait(false);
    }

    private void EnsureAppendWriter()
    {
        if (_appendStream != null)
            return;

        // WriteThrough: bypass OS page cache so each write reaches the disk controller
        // without a separate fsync call.  Asynchronous: IOCP on Windows for true async I/O.
        var fileOptions = FileOptions.Asynchronous;
        if (_syncMode == Configuration.WalSyncMode.WriteThrough)
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

    public void Dispose() => CloseAppendWriter();

    internal void AppendRecord(BinaryWriter writer, WalRecordType recordType, long transactionId, ReadOnlySpan<byte> key, byte[]? value)
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

    /// <summary>Current on-disk size of the WAL file in bytes.</summary>
    internal long SizeBytes
    {
        get
        {
            // If the append stream is open its Length reflects the true file size (even in append mode).
            if (_appendStream != null)
                return _appendStream.Length;
            return File.Exists(_path) ? new FileInfo(_path).Length : 0L;
        }
    }

    public void Truncate()
    {
        CloseAppendWriter();
        using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Flush(true);
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
            if (key.Length != keyLength)
                return false;

            byte[]? value = null;
            if (valueLength >= 0)
            {
                value = reader.ReadBytes(valueLength);
                if (value.Length != valueLength)
                    return false;
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
            // Treat a corrupted tail as end-of-log so previously committed
            // transactions remain recoverable after abrupt process crashes.
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
