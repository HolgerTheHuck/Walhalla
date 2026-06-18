using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WalhallaSql.Storage;

/// <summary>
/// Append-only sidecar file for out-of-line BLOB storage per table.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe for concurrent readers + single serialized appender.
/// Write-through durability guarantees that blob bytes hit the OS buffer
/// before the SQL engine commits the Row containing the <see cref="BlobRef"/>.
/// </para>
/// <para>
/// Compaction (H.7) reclaims orphaned regions by streaming live bytes into
/// <c>.tmp</c>, atomically swapping, and updating BlobRefs in affected rows.
/// </para>
/// </remarks>
internal sealed class BlobSidecarFile : IDisposable
{
    // Two-phase compaction sentinel file name suffix.
    private const string TmpSuffix = ".tmp";

    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly bool _inMemory;
    private readonly object _appendLock = new();

    // File-backed state
    private SafeFileHandle? _fileHandle;
    private long _appendOffset;

    // In-memory fallback
    private MemoryStream? _memStream;

    // MMAP fast-path cache (replaced atomically on growth)
    private MemoryMappedFile? _mmapFile;
    private MemoryMappedViewAccessor? _mmapAccessor;
    private long _mmapLength;

    // Telemetry counters (incremented only under _appendLock or during compaction)
    private long _totalBytesAppended;
    private long _totalBlobsAppended;
    private long _totalBytesCompacted;
    private long _compactionCount;

    public string FilePath => _filePath;

    /// <summary>Total bytes ever appended to this sidecar (monotonically increasing).</summary>
    public long TotalBytesAppended => Interlocked.Read(ref _totalBytesAppended);

    /// <summary>Total blobs ever appended to this sidecar.</summary>
    public long TotalBlobsAppended => Interlocked.Read(ref _totalBlobsAppended);

    /// <summary>Total bytes copied during compaction(s).</summary>
    public long TotalBytesCompacted => Interlocked.Read(ref _totalBytesCompacted);

    /// <summary>Number of compaction runs performed.</summary>
    public long CompactionCount => Interlocked.Read(ref _compactionCount);

    public BlobSidecarFile(string filePath, bool inMemory = false)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _tmpPath = filePath + TmpSuffix;
        _inMemory = inMemory;

        if (inMemory)
        {
            _memStream = new MemoryStream();
        }
        else
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Open existing or create new; append mode with write-through.
            _fileHandle = File.OpenHandle(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough);

            _appendOffset = RandomAccess.GetLength(_fileHandle);
            TryRemap();
        }
    }

    /// <summary>True if this sidecar manages bytes in-memory.</summary>
    public bool IsInMemory => _inMemory;

    /// <summary>Current append offset (total bytes written so far).</summary>
    public long AppendOffset
    {
        get
        {
            if (_inMemory)
                return _memStream!.Length;
            lock (_appendLock)
                return _appendOffset;
        }
    }

    /// <summary>
    /// Appends payload bytes and returns a <see cref="BlobRef"/> describing the location.
    /// Caller must ensure the returned ref is committed to a Row BEFORE the transaction commits.
    /// </summary>
    public BlobRef Append(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return new BlobRef(0, 0);

        if (_inMemory)
        {
            lock (_appendLock)
            {
                long offset = _memStream!.Length;
                _memStream.Write(payload);
                Interlocked.Add(ref _totalBytesAppended, payload.Length);
                Interlocked.Increment(ref _totalBlobsAppended);
                return new BlobRef(offset, payload.Length);
            }
        }

        lock (_appendLock)
        {
            long offset = _appendOffset;
            RandomAccess.Write(_fileHandle!, payload, offset);
            _appendOffset = offset + payload.Length;
            Interlocked.Add(ref _totalBytesAppended, payload.Length);
            Interlocked.Increment(ref _totalBlobsAppended);
            TryRemap();
            return new BlobRef(offset, payload.Length);
        }
    }

    /// <summary>
    /// Opens a read-only <see cref="Stream"/> for the given ref.
    /// Prefer this over <see cref="ReadAllBytes"/> to avoid allocating a contiguous array.
    /// </summary>
    public Stream OpenStream(BlobRef reference)
    {
        if (reference.Length == 0)
            return Stream.Null;

        if (_inMemory)
        {
            lock (_appendLock)
            {
                var buf = _memStream!.GetBuffer();
                return new MemoryStream(buf, (int)reference.Offset, reference.Length, writable: false);
            }
        }

        // MMAP fast-path: if the region is fully inside the current mmap view,
        // return a view stream. Otherwise fall back to RandomAccess.
        var mmap = _mmapAccessor;
        if (mmap != null &&
            reference.Offset + reference.Length <= _mmapLength)
        {
            // Create a sub-view stream. SafeFileHandle is required on .NET 6+.
            // We re-open the file read-only for the view stream so it doesn't
            // interfere with the write handle.
            var view = _mmapFile!.CreateViewStream(
                reference.Offset,
                reference.Length,
                MemoryMappedFileAccess.Read);
            return view;
        }

        // Fallback: allocate a buffer and read via RandomAccess.
        var bytes = ReadAllBytes(reference);
        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>Materialises the referenced bytes into a new array.</summary>
    public byte[] ReadAllBytes(BlobRef reference)
    {
        if (reference.Length == 0)
            return Array.Empty<byte>();

        if (_inMemory)
        {
            lock (_appendLock)
            {
                var result = new byte[reference.Length];
                _memStream!.GetBuffer().AsSpan((int)reference.Offset, reference.Length).CopyTo(result);
                return result;
            }
        }

        var buf = new byte[reference.Length];
        RandomAccess.Read(_fileHandle!, buf, reference.Offset);
        return buf;
    }

    /// <summary>
    /// Attempts to copy the referenced bytes directly into <paramref name="destination"/>.
    /// Returns the number of bytes copied (may be less than <paramref name="destination"/> length
    /// if the blob is smaller).
    /// </summary>
    public int TryReadBytes(BlobRef reference, Span<byte> destination)
    {
        if (reference.Length == 0)
            return 0;

        int toCopy = Math.Min(reference.Length, destination.Length);

        if (_inMemory)
        {
            lock (_appendLock)
            {
                _memStream!.GetBuffer().AsSpan((int)reference.Offset, toCopy).CopyTo(destination);
                return toCopy;
            }
        }

        return RandomAccess.Read(_fileHandle!, destination.Slice(0, toCopy), reference.Offset);
    }

    // ── Compaction support (H.7) ─────────────────────────────────────────────

    /// <summary>
    /// Compacts the sidecar by copying only live regions into a new file and
    /// atomically swapping it in.  Returns a mapping oldOffset → newOffset
    /// for every relocated BlobRef.
    /// </summary>
    internal Dictionary<long, long> Compact(IReadOnlyCollection<BlobRef> liveRefs)
    {
        if (_inMemory)
            throw new InvalidOperationException("Compaction is not supported for in-memory sidecars.");

        // Sort live refs by offset to stream sequentially.
        var sorted = liveRefs.OrderBy(r => r.Offset).ToList();
        var offsetMap = new Dictionary<long, long>(sorted.Count);

        using var tmpHandle = File.OpenHandle(
            _tmpPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough);

        long newOffset = 0;
        var buffer = new byte[64 * 1024];

        foreach (var oldRef in sorted)
        {
            long remaining = oldRef.Length;
            long readPos = oldRef.Offset;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, buffer.Length);
                int read = RandomAccess.Read(_fileHandle!, buffer.AsSpan(0, chunk), readPos);
                if (read == 0)
                    throw new InvalidDataException($"Unexpected EOF reading blob at offset {readPos}");
                RandomAccess.Write(tmpHandle, buffer.AsSpan(0, read), newOffset);
                readPos += read;
                newOffset += read;
                remaining -= read;
            }
            offsetMap[oldRef.Offset] = newOffset - oldRef.Length;
            Interlocked.Add(ref _totalBytesCompacted, oldRef.Length);
        }

        // Atomically swap temp file into place.
        lock (_appendLock)
        {
            _fileHandle?.Dispose();
            _mmapAccessor?.Dispose();
            _mmapFile?.Dispose();

            File.Move(_tmpPath, _filePath, overwrite: true);

            _fileHandle = File.OpenHandle(
                _filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough);

            _appendOffset = newOffset;
            _mmapLength = 0;
            Interlocked.Increment(ref _compactionCount);
            TryRemap();
        }

        return offsetMap;
    }

    // ── MMAP fast-path ───────────────────────────────────────────────────────

    private void TryRemap()
    {
        if (_inMemory || _fileHandle == null)
            return;

        long len = _appendOffset;
        if (len <= 0)
            return;

        // Only remap if we've grown significantly (>= 64 KB or 2× current view).
        if (_mmapLength > 0 && len < _mmapLength + 65536 && len < _mmapLength * 2)
            return;

        _mmapAccessor?.Dispose();
        _mmapFile?.Dispose();

        try
        {
            // We need a new SafeFileHandle for the mmap because the write handle
            // may not support memory-mapping on all platforms.
            using var readHandle = File.OpenHandle(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            _mmapFile = MemoryMappedFile.CreateFromFile(
                new FileStream(readHandle, FileAccess.Read),
                mapName: null,
                len,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);

            _mmapAccessor = _mmapFile.CreateViewAccessor(
                0, len, MemoryMappedFileAccess.Read);
            _mmapLength = len;
        }
        catch
        {
            // MMAP is optional; fall back to RandomAccess on failure.
            _mmapFile = null;
            _mmapAccessor = null;
            _mmapLength = 0;
        }
    }

    public void Dispose()
    {
        _mmapAccessor?.Dispose();
        _mmapFile?.Dispose();
        _fileHandle?.Dispose();
        _memStream?.Dispose();
    }
}
