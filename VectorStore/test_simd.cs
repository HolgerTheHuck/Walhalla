using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

class Program {
    static void Main() {
        Console.WriteLine($"Avx2.IsSupported: {Avx2.IsSupported}");
        Console.WriteLine($"Avx.IsSupported: {Avx.IsSupported}");
        Console.WriteLine($"Fma.IsSupported: {Fma.IsSupported}");
        
        var a = new float[] { 1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f };
        var b = new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
        
        var va = Vector256.Create(a);
        var vb = Vector256.Create(b);
        
        try {
            var diff = Avx2.Subtract(va, vb);
            var prod = Avx2.Multiply(diff, diff);
            Console.WriteLine($"Avx2.Multiply works: {prod}");
        } catch (Exception ex) {
            Console.WriteLine($"Avx2.Multiply failed: {ex.Message}");
        }
        
        try {
            var prod2 = Avx.Multiply(va, vb);
            Console.WriteLine($"Avx.Multiply works: {prod2}");
        } catch (Exception ex) {
            Console.WriteLine($"Avx.Multiply failed: {ex.Message}");
        }
    }
}
