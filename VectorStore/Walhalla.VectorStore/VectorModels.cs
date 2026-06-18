// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Walhalla.VectorStore;

/// <summary>Ein dichtbesiedelter Vektor fester Dimension.</summary>
public readonly struct Vector : IEquatable<Vector>
{
    private readonly float[] _data;

    public int Dimension => _data?.Length ?? 0;

    public ReadOnlySpan<float> Span => _data.AsSpan();

    /// <summary>Zugriff auf die Rohdaten (für interne Optimierungen).</summary>
    internal float[] Data => _data;

    public Vector(float[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public Vector(int dimension)
    {
        _data = new float[dimension];
    }

    /// <summary>Serialisiert zu Byte-Array (Little-Endian float32).</summary>
    public byte[] ToByteArray()
    {
        var bytes = new byte[Dimension * sizeof(float)];
        MemoryMarshal.AsBytes(_data.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// <summary>Deserialisiert aus Byte-Array.</summary>
    public static Vector FromByteArray(byte[] data, int dimension)
    {
        if (data.Length != dimension * sizeof(float))
            throw new ArgumentException($"Expected {dimension * sizeof(float)} bytes, got {data.Length}");

        var floats = new float[dimension];
        MemoryMarshal.Cast<byte, float>(data.AsSpan()).CopyTo(floats.AsSpan());
        return new Vector(floats);
    }

    public bool Equals(Vector other) => _data.AsSpan().SequenceEqual(other._data.AsSpan());
    public override bool Equals(object? obj) => obj is Vector other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var f in _data.AsSpan().Slice(0, Math.Min(4, Dimension)))
            hash.Add(f);
        return hash.ToHashCode();
    }
}

/// <summary>Metadaten zu einem Vektoreintrag.</summary>
public sealed class VectorMetadata
{
    public required ulong Id { get; set; }
    public required string Collection { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object>? Payload { get; set; }

    public byte[] ToJsonBytes() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this);
    public static VectorMetadata? FromJsonBytes(byte[] data) => System.Text.Json.JsonSerializer.Deserialize<VectorMetadata>(data);
}

/// <summary>Ergebnis einer Vektorsuche.</summary>
public readonly struct VectorSearchResult
{
    public required ulong Id { get; init; }
    public required float Score { get; init; } // je nach Metrik: kleiner = besser (L2) oder größer = besser (Cosine)
    public VectorMetadata? Metadata { get; init; }
}

/// <summary>Distanzmetriken für Vektoren.</summary>
public enum DistanceMetric
{
    Euclidean,      // L2: ||a - b||
    Cosine,         // 1 - cos(a,b), oder cos(a,b) wenn normalisiert
    DotProduct      // -a·b (negativ für Sortierung)
}
