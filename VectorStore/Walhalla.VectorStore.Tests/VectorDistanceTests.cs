// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using Xunit;

namespace Walhalla.VectorStore.Tests;

public class VectorDistanceTests
{
    private const float Tolerance = 1e-3f;

    private static void AssertEqualFloat(float expected, float actual, float tolerance = Tolerance)
    {
        var diff = Math.Abs(expected - actual);
        var relativeDiff = diff / Math.Max(Math.Abs(expected), 1e-10f);
        Assert.True(diff <= tolerance || relativeDiff <= tolerance,
            $"Expected: {expected}, Actual: {actual}, Diff: {diff}, RelativeDiff: {relativeDiff}");
    }

    [Fact]
    public void Euclidean_SameVector_ReturnsZero()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var result = VectorDistance.Euclidean(a.AsSpan(), a.AsSpan());

        Assert.Equal(0.0f, result, Tolerance);
    }

    [Fact]
    public void Euclidean_KnownDistance()
    {
        var a = new float[] { 0.0f, 0.0f, 0.0f };
        var b = new float[] { 3.0f, 4.0f, 0.0f };

        var result = VectorDistance.Euclidean(a.AsSpan(), b.AsSpan());

        Assert.Equal(5.0f, result, Tolerance);
    }

    [Fact]
    public void Euclidean_DifferentDimensions_ThrowsArgumentException()
    {
        var a = new float[] { 1.0f, 2.0f };
        var b = new float[] { 1.0f, 2.0f, 3.0f };

        Assert.Throws<ArgumentException>(() => VectorDistance.Euclidean(a.AsSpan(), b.AsSpan()));
    }

    [Fact]
    public void DotProduct_KnownValue()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 4.0f, 5.0f, 6.0f };

        var result = VectorDistance.DotProduct(a.AsSpan(), b.AsSpan());

        // 1*4 + 2*5 + 3*6 = 4 + 10 + 18 = 32
        Assert.Equal(32.0f, result, Tolerance);
    }

    [Fact]
    public void DotProduct_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f, 0.0f };

        var result = VectorDistance.DotProduct(a.AsSpan(), b.AsSpan());

        Assert.Equal(0.0f, result, Tolerance);
    }

    [Fact]
    public void Cosine_SameVector_ReturnsOne()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f };

        var result = VectorDistance.Cosine(a.AsSpan(), a.AsSpan());

        Assert.Equal(1.0f, result, Tolerance);
    }

    [Fact]
    public void Cosine_OppositeVector_ReturnsMinusOne()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { -1.0f, -2.0f, -3.0f };

        var result = VectorDistance.Cosine(a.AsSpan(), b.AsSpan());

        Assert.Equal(-1.0f, result, Tolerance);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f, 0.0f };

        var result = VectorDistance.Cosine(a.AsSpan(), b.AsSpan());

        Assert.Equal(0.0f, result, Tolerance);
    }

    [Fact]
    public void Cosine_NormalizedVectors_KnownValue()
    {
        // Two vectors at 60 degrees: cos(60°) = 0.5
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.5f, (float)Math.Sqrt(3) / 2 };

        var result = VectorDistance.Cosine(a.AsSpan(), b.AsSpan());

        Assert.Equal(0.5f, result, Tolerance);
    }

    [Fact]
    public void NormalizeL2_UnitVector_StaysUnit()
    {
        var a = new float[] { 1.0f, 0.0f, 0.0f };

        VectorDistance.NormalizeL2(a.AsSpan());

        Assert.Equal(1.0f, a[0], Tolerance);
        Assert.Equal(0.0f, a[1], Tolerance);
        Assert.Equal(0.0f, a[2], Tolerance);
    }

    [Fact]
    public void NormalizeL2_VectorBecomesUnitLength()
    {
        var a = new float[] { 3.0f, 4.0f, 0.0f };

        VectorDistance.NormalizeL2(a.AsSpan());

        var length = MathF.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        Assert.Equal(1.0f, length, Tolerance);
    }

    [Fact]
    public void NormalizeL2_ZeroVector_DoesNotThrow()
    {
        var a = new float[] { 0.0f, 0.0f, 0.0f };

        VectorDistance.NormalizeL2(a.AsSpan());

        // Should remain zero or handle gracefully
        Assert.True(float.IsFinite(a[0]) || a[0] == 0.0f);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(1536)]
    public void Euclidean_VariousDimensions_Correct(int dim)
    {
        var random = new Random(42);
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }

        var simdResult = VectorDistance.Euclidean(a.AsSpan(), b.AsSpan());

        // Verify with manual calculation
        float manual = 0.0f;
        for (int i = 0; i < dim; i++)
        {
            var diff = a[i] - b[i];
            manual += diff * diff;
        }
        manual = MathF.Sqrt(manual);

        AssertEqualFloat(manual, simdResult);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(1536)]
    public void DotProduct_VariousDimensions_Correct(int dim)
    {
        var random = new Random(42);
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }

        var simdResult = VectorDistance.DotProduct(a.AsSpan(), b.AsSpan());

        // Verify with manual calculation
        float manual = 0.0f;
        for (int i = 0; i < dim; i++)
            manual += a[i] * b[i];

        AssertEqualFloat(manual, simdResult);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(1536)]
    public void Cosine_VariousDimensions_Correct(int dim)
    {
        var random = new Random(42);
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }

        var simdResult = VectorDistance.Cosine(a.AsSpan(), b.AsSpan());

        // Verify with manual calculation
        float dot = 0.0f, normA = 0.0f, normB = 0.0f;
        for (int i = 0; i < dim; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var manual = dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));

        AssertEqualFloat(manual, simdResult);
    }
}
