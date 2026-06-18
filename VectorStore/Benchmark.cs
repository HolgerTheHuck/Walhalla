using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Collections;
using Walhalla.VectorStore.Indexes;
using Walhalla.Storage.Trees;

class Benchmark {
    static void Main() {
        Console.WriteLine("=== Walhalla.VectorStore Performance Benchmark ===\n");
        
        // 1. SIMD Distance Benchmarks
        BenchmarkDistances();
        
        // 2. HNSW Index Benchmarks
        BenchmarkHnsw();
        
        // 3. Collection Benchmarks
        BenchmarkCollection().Wait();
    }
    
    static void BenchmarkDistances() {
        Console.WriteLine("--- SIMD Distance Calculations ---");
        var dims = new[] { 128, 512, 1024, 1536 };
        var iterations = 100_000;
        
        foreach (var dim in dims) {
            var a = new float[dim];
            var b = new float[dim];
            new Random(42).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
            new Random(43).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()));
            
            // Warmup
            for (int i = 0; i < 1000; i++) {
                VectorDistance.Euclidean(a, b);
                VectorDistance.DotProduct(a, b);
                VectorDistance.Cosine(a, b);
            }
            
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                VectorDistance.Euclidean(a, b);
            sw.Stop();
            var euclideanMs = sw.Elapsed.TotalMilliseconds;
            
            sw.Restart();
            for (int i = 0; i < iterations; i++)
                VectorDistance.DotProduct(a, b);
            sw.Stop();
            var dotMs = sw.Elapsed.TotalMilliseconds;
            
            sw.Restart();
            for (int i = 0; i < iterations; i++)
                VectorDistance.Cosine(a, b);
            sw.Stop();
            var cosineMs = sw.Elapsed.TotalMilliseconds;
            
            Console.WriteLine($"Dim {dim,4}: Euclidean {iterations/euclideanMs*1000:F0} ops/s ({euclideanMs:F1}ms), DotProduct {iterations/dotMs*1000:F0} ops/s ({dotMs:F1}ms), Cosine {iterations/cosineMs*1000:F0} ops/s ({cosineMs:F1}ms)");
        }
        Console.WriteLine();
    }
    
    static void BenchmarkHnsw() {
        Console.WriteLine("--- HNSW Index ---");
        var dims = new[] { 128, 384, 768 };
        var counts = new[] { 1000, 5000, 10000 };
        
        foreach (var dim in dims) {
            foreach (var count in counts) {
                var index = new HnswIndex(new HnswOptions { M = 16, EfConstruction = 200 });
                var random = new Random(42);
                var vectors = new float[count][];
                for (int i = 0; i < count; i++) {
                    vectors[i] = new float[dim];
                    random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vectors[i].AsSpan()));
                }
                
                // Insert benchmark
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < count; i++) {
                    var v = vectors[i];
                    index.Insert((ulong)i, () => v);
                }
                sw.Stop();
                var insertMs = sw.Elapsed.TotalMilliseconds;
                
                // Search benchmark
                var query = vectors[0];
                sw.Restart();
                for (int i = 0; i < 1000; i++) {
                    index.SearchKnn(query, 10);
                }
                sw.Stop();
                var searchMs = sw.Elapsed.TotalMilliseconds;
                
                Console.WriteLine($"Dim {dim,3} x {count,5} vectors: Insert {insertMs:F1}ms ({count/insertMs*1000:F0} ops/s), Search 1000x {searchMs:F1}ms ({1000/searchMs*1000:F0} qps), Nodes: {index.NodeCount}");
            }
        }
        Console.WriteLine();
    }
    
    static async Task BenchmarkCollection() {
        Console.WriteLine("--- VectorCollection (In-Memory BlobStore) ---");
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"walhalla_bench_{Guid.NewGuid()}");
        System.IO.Directory.CreateDirectory(dbPath);
        
        try {
            var store = new BlobStore(new BlobStoreOptions(dbPath));
            var manager = new VectorCollectionManager(store);
            var collection = manager.GetOrCreateCollection("bench", 128, DistanceMetric.Cosine);
            var random = new Random(42);
            
            // Put benchmark
            int putCount = 5000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < putCount; i++) {
                var vec = new float[128];
                random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vec.AsSpan()));
                await collection.PutAsync((ulong)i, new Vector(vec));
            }
            sw.Stop();
            Console.WriteLine($"Put {putCount} vectors: {sw.Elapsed.TotalMilliseconds:F1}ms ({putCount/sw.Elapsed.TotalMilliseconds*1000:F0} ops/s)");
            
            // Get benchmark
            sw.Restart();
            for (int i = 0; i < 1000; i++) {
                await collection.GetAsync((ulong)(i % putCount));
            }
            sw.Stop();
            Console.WriteLine($"Get 1000 vectors: {sw.Elapsed.TotalMilliseconds:F1}ms ({1000/sw.Elapsed.TotalMilliseconds*1000:F0} ops/s)");
            
            // Exact search benchmark
            var query = new Vector(vectors[0]);
            sw.Restart();
            var results = await collection.SearchExactAsync(query, 10).ToListAsync();
            sw.Stop();
            Console.WriteLine($"ExactSearch top-10: {sw.Elapsed.TotalMilliseconds:F1}ms ({results.Count} results)");
            
            // HNSW search benchmark
            if (collection.HnswIndex is not null) {
                sw.Restart();
                for (int i = 0; i < 100; i++) {
                    await collection.SearchHnswAsync(query, 10).ToListAsync();
                }
                sw.Stop();
                Console.WriteLine($"HNSW Search 100x top-10: {sw.Elapsed.TotalMilliseconds:F1}ms ({100/sw.Elapsed.TotalMilliseconds*1000:F0} qps)");
            }
            
            manager.Dispose();
            store.Dispose();
        } finally {
            if (System.IO.Directory.Exists(dbPath))
                System.IO.Directory.Delete(dbPath, recursive: true);
        }
    }
}
