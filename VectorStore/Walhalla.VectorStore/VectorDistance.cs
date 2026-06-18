// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Walhalla.VectorStore;

/// <summary>High-Performance Distanzberechnung mit SIMD.</summary>
public static class VectorDistance
{
    /// <summary>L2-Distanz (Euklidisch) zwischen zwei Vektoren.</summary>
    public static float Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimension mismatch");

        if (a.Length == 0) return 0;

        // AVX2/Packed SIMD
        if (Avx2.IsSupported && a.Length >= Vector256<float>.Count)
        {
            return MathF.Sqrt(EuclideanSquaredAvx2(a, b));
        }

        // Fallback
        return EuclideanScalar(a, b);
    }

    /// <summary>L2-Distanz^2 (ohne Wurzel). Fuer Ranking identisch, aber schneller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EuclideanSquared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimension mismatch");

        if (a.Length == 0) return 0;

        if (Avx2.IsSupported && a.Length >= Vector256<float>.Count)
        {
            return EuclideanSquaredAvx2(a, b);
        }

        return EuclideanSquaredScalar(a, b);
    }

    /// <summary>Cosinus-Ähnlichkeit. Wenn Vektoren normalisiert: cos(a,b) = a·b</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimension mismatch");

        var dot = DotProduct(a, b);
        var normA = MathF.Sqrt(DotProduct(a, a));
        var normB = MathF.Sqrt(DotProduct(b, b));

        if (normA == 0 || normB == 0) return 0;
        return dot / (normA * normB);
    }

    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimension mismatch");

        if (Avx2.IsSupported && a.Length >= Vector256<float>.Count)
        {
            return DotProductAvx2(a, b);
        }

        return DotProductScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EuclideanScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EuclideanSquaredScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProductScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static unsafe float EuclideanSquaredAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum256 = Vector256<float>.Zero;
        int i = 0;
        int n = a.Length;
        int simdLen = n - Vector256<float>.Count;

        fixed (float* aPtr = a)
        fixed (float* bPtr = b)
        {
            for (; i <= simdLen; i += Vector256<float>.Count)
            {
                var va = Avx.LoadVector256(aPtr + i);
                var vb = Avx.LoadVector256(bPtr + i);
                var diff = Avx2.Subtract(va, vb);
                sum256 = Fma.MultiplyAdd(diff, diff, sum256);
            }
        }

        var sum = HorizontalAdd(sum256);

        for (; i < n; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }

    private static unsafe float DotProductAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum256 = Vector256<float>.Zero;
        int i = 0;
        int n = a.Length;
        int simdLen = n - Vector256<float>.Count;

        fixed (float* aPtr = a)
        fixed (float* bPtr = b)
        {
            for (; i <= simdLen; i += Vector256<float>.Count)
            {
                var va = Avx.LoadVector256(aPtr + i);
                var vb = Avx.LoadVector256(bPtr + i);
                sum256 = Fma.MultiplyAdd(va, vb, sum256);
            }
        }

        var sum = HorizontalAdd(sum256);

        for (; i < n; i++)
            sum += a[i] * b[i];

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HorizontalAdd(Vector256<float> v)
    {
        // v = [a,b,c,d,e,f,g,h]
        // low = [a,b,c,d], high = [e,f,g,h]
        var low = v.GetLower();
        var high = v.GetUpper();
        var sum128 = Sse.Add(low, high);

        // [a+e, b+f, c+g, d+h] -> [c+g, d+h, a+e, b+f]
        sum128 = Sse.Add(sum128, Sse.Shuffle(sum128, sum128, 0b_01_00_11_10));

        // [s01, s01, s23, s23] -> [s23, s01, s23, s01]
        sum128 = Sse.Add(sum128, Sse.Shuffle(sum128, sum128, 0b_10_11_00_01));

        return sum128.ToScalar();
    }

    /// <summary>Normalisiert einen Vektor L2 auf 1.0.</summary>
    public static unsafe void NormalizeL2(Span<float> vector)
    {
        var norm = MathF.Sqrt(DotProduct(vector, vector));
        if (norm > 0)
        {
            var invNorm = 1.0f / norm;
            int i = 0;
            int n = vector.Length;

            if (Avx2.IsSupported && n >= Vector256<float>.Count)
            {
                var invNormVec = Vector256.Create(invNorm);
                int simdLen = n - Vector256<float>.Count;
                fixed (float* vPtr = vector)
                {
                    for (; i <= simdLen; i += Vector256<float>.Count)
                    {
                        var va = Avx.LoadVector256(vPtr + i);
                        Avx.Store(vPtr + i, Avx2.Multiply(va, invNormVec));
                    }
                }
            }

            for (; i < n; i++)
                vector[i] *= invNorm;
        }
    }
}
