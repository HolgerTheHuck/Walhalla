// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class VectorModelsTests
{
    private static object? GetPayloadValue(VectorMetadata metadata, string key)
    {
        if (metadata.Payload is null || !metadata.Payload.TryGetValue(key, out var value))
            return null;

        if (value is System.Text.Json.JsonElement element)
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.TryGetInt32(out var intVal) ? intVal : element.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => element.ToString()
            };
        }
        return value;
    }

    [Fact]
    public void Vector_Create_WithData_SetsDimension()
    {
        var data = new float[] { 1.0f, 2.0f, 3.0f };
        var vector = new Vector(data);

        Assert.Equal(3, vector.Dimension);
    }

    [Fact]
    public void Vector_Create_WithDimension_InitializesToZero()
    {
        var vector = new Vector(128);

        Assert.Equal(128, vector.Dimension);
        Assert.All(vector.Span.ToArray(), v => Assert.Equal(0.0f, v));
    }

    [Fact]
    public void Vector_Create_WithNullData_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new Vector(null!));
    }

    [Fact]
    public void Vector_Span_ReturnsCorrectData()
    {
        var data = new float[] { 1.0f, 2.0f, 3.0f };
        var vector = new Vector(data);

        var span = vector.Span;
        Assert.Equal(3, span.Length);
        Assert.Equal(1.0f, span[0]);
        Assert.Equal(2.0f, span[1]);
        Assert.Equal(3.0f, span[2]);
    }

    [Fact]
    public void Vector_ToByteArray_RoundTrip()
    {
        var data = new float[] { 1.0f, 2.5f, -3.14f, 0.0f };
        var vector = new Vector(data);

        var bytes = vector.ToByteArray();
        var restored = Vector.FromByteArray(bytes, 4);

        Assert.Equal(vector.Dimension, restored.Dimension);
        Assert.True(vector.Span.SequenceEqual(restored.Span));
    }

    [Fact]
    public void Vector_FromByteArray_WrongSize_ThrowsArgumentException()
    {
        var bytes = new byte[8]; // 2 floats, but expecting 4
        Assert.Throws<ArgumentException>(() => Vector.FromByteArray(bytes, 4));
    }

    [Fact]
    public void Vector_Equals_SameData_ReturnsTrue()
    {
        var a = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var b = new Vector(new float[] { 1.0f, 2.0f, 3.0f });

        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
    }

    [Fact]
    public void Vector_Equals_DifferentData_ReturnsFalse()
    {
        var a = new Vector(new float[] { 1.0f, 2.0f, 3.0f });
        var b = new Vector(new float[] { 1.0f, 2.0f, 3.1f });

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Vector_GetHashCode_SameData_SameHash()
    {
        var a = new Vector(new float[] { 1.0f, 2.0f, 3.0f, 4.0f });
        var b = new Vector(new float[] { 1.0f, 2.0f, 3.0f, 4.0f });

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void VectorMetadata_Serialization_RoundTrip()
    {
        var meta = new VectorMetadata
        {
            Id = 1,
            Collection = "test",
            Payload = new Dictionary<string, object>
            {
                ["title"] = "Test Document",
                ["category"] = "test",
                ["score"] = 42.0
            }
        };

        var bytes = meta.ToJsonBytes();
        var restored = VectorMetadata.FromJsonBytes(bytes)!;

        Assert.Equal("Test Document", GetPayloadValue(restored, "title"));
        Assert.Equal("test", GetPayloadValue(restored, "category"));
        Assert.Equal(42.0, GetPayloadValue(restored, "score"));
    }

    [Fact]
    public void VectorMetadata_ToJsonBytes_FromJsonBytes_RoundTrip()
    {
        var meta = new VectorMetadata
        {
            Id = 2,
            Collection = "test",
            Payload = new Dictionary<string, object>
            {
                ["key"] = "value",
                ["number"] = 123
            }
        };

        var bytes = meta.ToJsonBytes();
        var restored = VectorMetadata.FromJsonBytes(bytes)!;

        Assert.Equal("value", GetPayloadValue(restored, "key"));
        Assert.Equal(123.0, GetPayloadValue(restored, "number"));
    }

    [Fact]
    public void VectorMetadata_Empty_SerializesCorrectly()
    {
        var meta = new VectorMetadata
        {
            Id = 3,
            Collection = "test"
        };
        var bytes = meta.ToJsonBytes();
        var restored = VectorMetadata.FromJsonBytes(bytes)!;

        Assert.Equal(3ul, restored.Id);
        Assert.Equal("test", restored.Collection);
    }

    [Fact]
    public void VectorSearchResult_Properties_SetCorrectly()
    {
        var result = new VectorSearchResult
        {
            Id = 42,
            Score = 0.95f
        };

        Assert.Equal(42ul, result.Id);
        Assert.Equal(0.95f, result.Score);
    }
}
