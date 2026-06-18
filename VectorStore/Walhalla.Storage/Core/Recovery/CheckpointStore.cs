// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using Walhalla.Storage.Core;

namespace Walhalla.Storage.Core.Recovery;

/// <summary>
/// Serialises and deserialises the MemTable snapshot (checkpoint) to/from a compact binary file.
/// During a checkpoint the current MemTable is atomically written to a <c>.tmp</c> file
/// which is then renamed over the existing checkpoint file, guaranteeing that a crash
/// during serialisation cannot corrupt the last good checkpoint.
/// </summary>
/// <remarks>
/// File format: 4-byte magic <c>0x43504B31</c> ("CPK1"), 1-byte version, followed by
/// a length-prefixed sequence of key-value pairs encoded as 4-byte length + raw bytes.
/// </remarks>
internal sealed class CheckpointStore
{
    private const int Magic = 0x43504B31; // CPK1
    private const byte Version2 = 2; // raw byte[] keys

    private readonly string _path;

    public CheckpointStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public IReadOnlyDictionary<byte[], byte[]> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<byte[], byte[]>(ByteArrayContentComparer.Instance);

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadInt32();
        if (magic != Magic)
            throw new InvalidDataException("Invalid checkpoint magic.");

        var version = reader.ReadByte();
        if (version != Version2)
            throw new InvalidDataException($"Unsupported checkpoint version '{version}'.");

        return LoadV2(reader);
    }

    public void Save(IReadOnlyDictionary<byte[], byte[]> memTable)
    {
        var tempPath = _path + ".tmp";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version2);
            writer.Write(memTable.Count);

            foreach (var item in memTable)
            {
                writer.Write(item.Key.Length);
                writer.Write(item.Value.Length);
                writer.Write(item.Key);
                writer.Write(item.Value);
            }

            writer.Flush();
            stream.Flush(true);
        }

        if (File.Exists(_path))
            File.Delete(_path);

        File.Move(tempPath, _path);
    }

    private static IReadOnlyDictionary<byte[], byte[]> LoadV2(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var map = new Dictionary<byte[], byte[]>(count, ByteArrayContentComparer.Instance);
        for (var i = 0; i < count; i++)
        {
            var keyLength = reader.ReadInt32();
            var valueLength = reader.ReadInt32();
            if (keyLength <= 0 || valueLength < 0)
                throw new InvalidDataException("Checkpoint key/value lengths are invalid.");

            var key = reader.ReadBytes(keyLength);
            var value = reader.ReadBytes(valueLength);
            if (key.Length != keyLength || value.Length != valueLength)
                throw new EndOfStreamException("Checkpoint payload truncated.");

            map[key] = value;
        }

        return map;
    }

}
