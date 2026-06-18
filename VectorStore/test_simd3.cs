using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

class Program {
    static void Main() {
        var random = new Random(42);
        int dim = 8;
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++) {
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }
        
        Console.WriteLine($"a = [{string.Join(", ", a)}]");
        Console.WriteLine($"b = [{string.Join(", ", b)}]");
        
        // Manual calculation
        float manual = 0.0f;
        for (int i = 0; i < dim; i++) {
            var diff = a[i] - b[i];
            manual += diff * diff;
        }
        manual = MathF.Sqrt(manual);
        
        // SIMD step by step
        var sum256 = Vector256<float>.Zero;
        int idx = 0;
        for (; idx <= dim - Vector256<float>.Count; idx += Vector256<float>.Count) {
            var va = Vector256.Create(a.AsSpan(idx));
            var vb = Vector256.Create(b.AsSpan(idx));
            var diff = Avx2.Subtract(va, vb);
            sum256 = Avx2.Add(sum256, Avx2.Multiply(diff, diff));
            Console.WriteLine($"va = {va}");
            Console.WriteLine($"vb = {vb}");
            Console.WriteLine($"diff = {diff}");
        }
        
        Console.WriteLine($"sum256 = {sum256}");
        
        // Horizontal add
        var low = sum256.GetLower();
        var high = sum256.GetUpper();
        Console.WriteLine($"low = {low}");
        Console.WriteLine($"high = {high}");
        
        var sum128 = Sse.Add(low, high);
        Console.WriteLine($"sum128 after add = {sum128}");
        
        // shuffle und add
        var shuf1 = Sse.Shuffle(sum128, sum128, 0b_01_00_11_10);
        Console.WriteLine($"shuf1 = {shuf1}");
        sum128 = Sse.Add(sum128, shuf1);
        Console.WriteLine($"sum128 after first add = {sum128}");
        
        var shuf2 = Sse.Shuffle(sum128, sum128, 0b_00_01_01_00);
        Console.WriteLine($"shuf2 = {shuf2}");
        sum128 = Sse.Add(sum128, shuf2);
        Console.WriteLine($"sum128 after second add = {sum128}");
        
        var simd = sum128.ToScalar();
        
        for (; idx < dim; idx++) {
            var diff = a[idx] - b[idx];
            simd += diff * diff;
        }
        simd = MathF.Sqrt(simd);
        
        Console.WriteLine($"Manual: {manual:F10}");
        Console.WriteLine($"SIMD:   {simd:F10}");
    }
}
