// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Walhalla.Storage.Trees;

/// <summary>
/// Append-only Blob-Store für out-of-line Werte (> OverflowThreshold).
/// Blobs sind immutable; Updates schreiben einen neuen Blob.
/// Freigabe erfolgt erst bei <see cref="CompactAsync(IEnumerable{OverflowPointer})"/>.
/// </summary>
internal sealed class OverflowStore : IDisposable
{
    private readonly string _filePath;
    private SafeFileHandle? _handle;
    private long _length; // guarded by _lock
    private readonly object _lock = new();
    private readonly HashSet<OverflowPointer> _pendingFree = new();
    private bool _disposed;

    public OverflowStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _handle = OpenHandle(filePath);
        _length = RandomAccess.GetLength(_handle);
    }

    public long Length => _length;

    /// <summary>
    /// Schreibt <paramref name="data"/> append-only und gibt einen Pointer zurück.
    /// Thread-safe.
    /// </summary>
    public OverflowPointer WriteBlob(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        ThrowIfDisposed();

        lock (_lock)
        {
            var offset = _length;
            RandomAccess.Write(_handle!, data, offset);
            _length += data.Length;
            return new OverflowPointer(offset, data.Length);
        }
    }

    /// <summary>Liest einen Blob anhand seines Pointers.</summary>
    public byte[] ReadBlob(OverflowPointer ptr)
    {
        ThrowIfDisposed();

        var buf = new byte[ptr.Length];
        var totalRead = 0;

        while (totalRead < ptr.Length)
        {
            var n = RandomAccess.Read(_handle!, buf.AsSpan(totalRead), ptr.Offset + totalRead);
            if (n == 0)
                throw new IOException($"Overflow blob truncated at offset {ptr.Offset}: expected {ptr.Length} B.");
            totalRead += n;
        }

        return buf;
    }

    /// <summary>
    /// Prüft, ob der Pointer innerhalb der aktuellen Dateigröße liegt.
    /// Nicht-thread-safe — nur für diagnostische Zwecke oder unter externem Lock.
    /// </summary>
    public bool IsValidPointer(OverflowPointer ptr)
    {
        if (_disposed) return false;
        return ptr.Offset >= 0 && ptr.Length > 0 && ptr.Offset + ptr.Length <= _length;
    }

    /// <summary>
    /// Markiert einen Blob zur späteren Freigabe. Wird erst bei Compaction physisch entfernt.
    /// </summary>
    public void FreeBlob(OverflowPointer ptr)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            _pendingFree.Add(ptr);
        }
    }

    /// <summary>
    /// Blobs, die seit dem letzten Compact als frei markiert wurden.
    /// </summary>
    public IReadOnlySet<OverflowPointer> PendingFree => _pendingFree;

    /// <summary>
    /// Schreibt alle live Blobs in eine neue Datei und tauscht sie atomisch.
    /// Muss nicht concurrent zu WriteBlob/ReadBlob aufgerufen werden.
    /// </summary>
    public void Compact(IEnumerable<OverflowPointer> livePointers)
    {
        ThrowIfDisposed();

        var tmpPath = _filePath + ".tmp";
        try
        {
            // Phase 1: collect live, sorted by offset for sequential read
            var live = new List<OverflowPointer>();
            foreach (var ptr in livePointers)
            {
                if (IsValidPointer(ptr))
                    live.Add(ptr);
            }
            live.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            // Phase 2: stream into tmp file
            long writeOffset = 0;
            using (var tmpHandle = OpenHandle(tmpPath))
            {
                foreach (var ptr in live)
                {
                    var data = ReadBlob(ptr);
                    RandomAccess.Write(tmpHandle, data, writeOffset);
                    writeOffset += data.Length;
                }
            }

            // Phase 3: atomic swap
            lock (_lock)
            {
                _handle?.Dispose();
                File.Move(tmpPath, _filePath, overwrite: true);
                _handle = OpenHandle(_filePath);
                _length = RandomAccess.GetLength(_handle);
                _pendingFree.Clear();
            }
        }
        finally
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle?.Dispose();
        _handle = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OverflowStore));
    }

    private static SafeFileHandle OpenHandle(string path)
    {
        return File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.None);
    }
}
