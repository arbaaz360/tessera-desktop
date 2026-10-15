using System.Diagnostics;
using System.Numerics.Tensors;
using Xunit.Abstractions;

namespace Tessera.Desktop.Search.Tests;

public class IndexTests
{
    private readonly ITestOutputHelper _out;

    public IndexTests(ITestOutputHelper output) => _out = output;

    private static float[] RandomUnit(Random rng, int dims)
    {
        var v = new float[dims];
        for (int i = 0; i < dims; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
        float n = TensorPrimitives.Norm(v);
        TensorPrimitives.Divide(v, n, v);
        return v;
    }

    private static float Cosine(float[] a, float[] b) => TensorPrimitives.CosineSimilarity(a, b);

    [Fact]
    public void TryGetReturnsTheStoredUnitVectorAndFalseForAbsentIds()
    {
        var rng = new Random(3);
        using var index = new BruteForceVectorIndex(768);
        Assert.False(index.TryGet(1, out var missing));
        Assert.Empty(missing);

        var a = RandomUnit(rng, 768);
        index.Upsert(1, a);
        Assert.True(index.TryGet(1, out var got));
        Assert.Equal(768, got.Length);
        Assert.InRange(TensorPrimitives.Norm(got), 0.999f, 1.001f);
        Assert.InRange(Cosine(a, got), 0.999f, 1.0001f);   // int8 round trip

        // After a remove that moves the last row into the hole, ids still resolve.
        index.Upsert(2, RandomUnit(rng, 768));
        var c = RandomUnit(rng, 768);
        index.Upsert(3, c);
        Assert.True(index.Remove(1));
        Assert.False(index.TryGet(1, out _));
        Assert.True(index.TryGet(3, out var gotC));
        Assert.InRange(Cosine(c, gotC), 0.999f, 1.0001f);
    }

    [Fact]
    public void UpsertRemoveContains()
    {
        var rng = new Random(1);
        using var index = new BruteForceVectorIndex(16);
        Assert.Equal(0, index.Count);
        Assert.False(index.Contains(7));

        var a = RandomUnit(rng, 16);
        var b = RandomUnit(rng, 16);
        index.Upsert(7, a);
        index.Upsert(8, b);
        Assert.Equal(2, index.Count);
        Assert.True(index.Contains(7));
        Assert.True(index.Contains(8));

        // Upsert on an existing id replaces, count unchanged.
        var c = RandomUnit(rng, 16);
        index.Upsert(7, c);
        Assert.Equal(2, index.Count);
        var hits = index.Search(c, 1);
        Assert.Equal(7, hits[0].Id);
        Assert.InRange(hits[0].Score, 0.99f, 1.001f);

        Assert.True(index.Remove(7));
        Assert.False(index.Remove(7));
        Assert.False(index.Contains(7));
        Assert.Equal(1, index.Count);
        hits = index.Search(b, 5);
        Assert.Single(hits);
        Assert.Equal(8, hits[0].Id);

        Assert.True(index.Remove(8));
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search(b, 5));
    }

    [Fact]
    public void RemoveMiddleRowKeepsOthersSearchable()
    {
        var rng = new Random(2);
        using var index = new BruteForceVectorIndex(32);
        var vectors = new Dictionary<long, float[]>();
        for (long id = 1; id <= 50; id++)
        {
            vectors[id] = RandomUnit(rng, 32);
            index.Upsert(id, vectors[id]);
        }
        foreach (long id in new long[] { 1, 25, 50, 13 }) Assert.True(index.Remove(id));
        Assert.Equal(46, index.Count);
        foreach (var kv in vectors)
        {
            var hits = index.Search(kv.Value, 1);
            if (kv.Key is 1 or 25 or 50 or 13)
            {
                Assert.NotEqual(kv.Key, hits[0].Id);
            }
            else
            {
                Assert.Equal(kv.Key, hits[0].Id);
            }
        }
    }

    [Fact]
    public void WrongDimensionsThrow()
    {
        using var index = new BruteForceVectorIndex(8);
        Assert.Throws<ArgumentException>(() => index.Upsert(1, new float[7]));
        Assert.Throws<ArgumentException>(() => index.Search(new float[9], 1));
    }

    [Fact]
    public void TopKOrderAndK()
    {
        var rng = new Random(3);
        using var index = new BruteForceVectorIndex(64);
        var vectors = new List<float[]>();
        for (long id = 0; id < 200; id++)
        {
            var v = RandomUnit(rng, 64);
            vectors.Add(v);
            index.Upsert(id, v);
        }
        var query = vectors[42];
        var hits = index.Search(query, 10);
        Assert.Equal(10, hits.Count);
        Assert.Equal(42, hits[0].Id);
        for (int i = 1; i < hits.Count; i++) Assert.True(hits[i - 1].Score >= hits[i].Score, "scores must descend");

        // Exact order against a float reference
        var reference = Enumerable.Range(0, 200)
            .Select(i => (Id: (long)i, Score: Cosine(query, vectors[i])))
            .OrderByDescending(t => t.Score).Take(10).ToList();
        Assert.Equal(reference.Select(t => t.Id), hits.Select(h => h.Id));

        Assert.Equal(200, index.Search(query, 1000).Count);
        Assert.Empty(index.Search(query, 0));
        Assert.Empty(index.Search(query, -3));
    }

    [Fact]
    public void FilterIsAppliedBeforeTopK()
    {
        var rng = new Random(4);
        using var index = new BruteForceVectorIndex(32);
        var query = RandomUnit(rng, 32);
        // ids 0..9: near the query; ids 10..19: far from it
        for (long id = 0; id < 10; id++)
        {
            var v = (float[])query.Clone();
            for (int i = 0; i < 32; i++) v[i] += 0.05f * (float)(rng.NextDouble() - 0.5);
            index.Upsert(id, v);
        }
        for (long id = 10; id < 20; id++) index.Upsert(id, RandomUnit(rng, 32));

        var unfiltered = index.Search(query, 5);
        Assert.All(unfiltered, h => Assert.InRange(h.Id, 0, 9));

        // Reject every close one: the answer must still have five rows, all far ones.
        var filtered = index.Search(query, 5, id => id >= 10);
        Assert.Equal(5, filtered.Count);
        Assert.All(filtered, h => Assert.InRange(h.Id, 10, 19));

        var one = index.Search(query, 5, id => id == 15);
        Assert.Single(one);
        Assert.Equal(15, one[0].Id);

        Assert.Empty(index.Search(query, 5, _ => false));
    }

    [Fact]
    public void ScoresAreCosinesWithinQuantisationError()
    {
        var rng = new Random(5);
        using var index = new BruteForceVectorIndex(768);
        var vectors = new List<float[]>();
        for (long id = 0; id < 500; id++)
        {
            var v = RandomUnit(rng, 768);
            vectors.Add(v);
            index.Upsert(id, v);
        }
        float worst = 0;
        for (int q = 0; q < 20; q++)
        {
            var query = RandomUnit(rng, 768);
            foreach (var hit in index.Search(query, 500))
            {
                float exact = Cosine(query, vectors[(int)hit.Id]);
                worst = Math.Max(worst, Math.Abs(exact - hit.Score));
            }
        }
        _out.WriteLine($"max |int8 cosine - float cosine| over 10,000 pairs: {worst:F5}");
        Assert.True(worst < 0.01f, $"quantisation error {worst}");
    }

    [Fact]
    public async Task SaveLoadRoundTrip()
    {
        var rng = new Random(6);
        using var index = new BruteForceVectorIndex(48);
        var vectors = new Dictionary<long, float[]>();
        for (int i = 0; i < 9000; i++) // more than two blocks
        {
            long id = rng.NextInt64(1, long.MaxValue);
            vectors[id] = RandomUnit(rng, 48);
            index.Upsert(id, vectors[id]);
        }
        // Leave a hole-filling remove in the history too.
        long gone = vectors.Keys.First();
        index.Remove(gone);
        vectors.Remove(gone);

        using var ms = new MemoryStream();
        await index.SaveAsync(ms, CancellationToken.None);
        Assert.Equal(BruteForceVectorIndex.HeaderBytes + vectors.Count * (8 + 4 + 48), ms.Length);

        ms.Position = 0;
        using var loaded = new BruteForceVectorIndex(48);
        loaded.Upsert(123, RandomUnit(rng, 48)); // must be replaced by the load
        await loaded.LoadAsync(ms, CancellationToken.None);

        Assert.Equal(vectors.Count, loaded.Count);
        Assert.False(loaded.Contains(123));
        Assert.False(loaded.Contains(gone));
        foreach (var id in vectors.Keys.Take(200))
        {
            Assert.True(loaded.Contains(id));
            var a = index.Search(vectors[id], 5);
            var b = loaded.Search(vectors[id], 5);
            Assert.Equal(a.Select(h => h.Id), b.Select(h => h.Id));
            Assert.Equal(a.Select(h => h.Score), b.Select(h => h.Score));
        }

        // Still writable after a load
        loaded.Upsert(999, RandomUnit(rng, 48));
        Assert.Equal(vectors.Count + 1, loaded.Count);
    }

    [Fact]
    public async Task LoadRejectsForeignFiles()
    {
        using var index = new BruteForceVectorIndex(8);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            index.LoadAsync(new MemoryStream(new byte[64]), CancellationToken.None));

        using var other = new BruteForceVectorIndex(16);
        other.Upsert(1, RandomUnit(new Random(1), 16));
        using var ms = new MemoryStream();
        await other.SaveAsync(ms, CancellationToken.None);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => index.LoadAsync(ms, CancellationToken.None));

        ms.Position = 0;
        var truncated = new MemoryStream(ms.ToArray(), 0, (int)ms.Length - 3);
        using var same = new BruteForceVectorIndex(16);
        await Assert.ThrowsAsync<EndOfStreamException>(() => same.LoadAsync(truncated, CancellationToken.None));
    }

    [Fact]
    public void ConcurrentSearchesAndWritesDoNotCorrupt()
    {
        var rng = new Random(7);
        using var index = new BruteForceVectorIndex(64);
        for (long id = 0; id < 5000; id++) index.Upsert(id, RandomUnit(rng, 64));
        var query = RandomUnit(rng, 64);
        var errors = 0;
        Parallel.For(0, 64, i =>
        {
            try
            {
                var local = new Random(100 + i);
                for (int n = 0; n < 50; n++)
                {
                    if (i % 4 == 0) index.Upsert(5000 + i * 100 + n, RandomUnit(local, 64));
                    else if (i % 4 == 1) index.Remove(local.Next(0, 5000));
                    else
                    {
                        var hits = index.Search(query, 10);
                        Assert.True(hits.Count <= 10);
                        for (int h = 1; h < hits.Count; h++) Assert.True(hits[h - 1].Score >= hits[h].Score);
                    }
                }
            }
            catch (Exception) { Interlocked.Increment(ref errors); throw; }
        });
        Assert.Equal(0, errors);
    }

    [Fact]
    public void Int8DotKernelsAgree()
    {
        var rng = new Random(8);
        for (int trial = 0; trial < 50; trial++)
        {
            int n = trial < 5 ? 768 : rng.Next(1, 300);
            var q = new float[n];
            var v = new sbyte[n];
            for (int i = 0; i < n; i++)
            {
                q[i] = (float)(rng.NextDouble() * 2 - 1);
                v[i] = (sbyte)rng.Next(-127, 128);
            }
            float scalar = Int8Dot.DotScalar(q, v);
            float generic = Int8Dot.Dot(q, v);
            Assert.InRange(generic, scalar - 1e-2f, scalar + 1e-2f);
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && System.Runtime.Intrinsics.X86.Fma.IsSupported && n >= 16)
                Assert.InRange(Int8Dot.DotAvx2(q, v), scalar - 1e-2f, scalar + 1e-2f);
            if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && n >= 16)
                Assert.InRange(Int8Dot.DotVector128(q, v), scalar - 1e-2f, scalar + 1e-2f);
        }
    }

    [Fact]
    public void TopKKeepsBestWithTies()
    {
        var top = new TopK(3);
        top.TryAdd(0.5f, 1);
        top.TryAdd(0.9f, 2);
        top.TryAdd(0.1f, 3);
        top.TryAdd(0.7f, 4);
        top.TryAdd(0.9f, 5);
        top.TryAdd(0.05f, 6);
        var hits = top.ToSortedHits();
        Assert.Equal(new long[] { 2, 5, 4 }, hits.Select(h => h.Id));
    }

    [Fact]
    public async Task TwoHundredThousandVectors_LatencyAndMemory()
    {
        const int n = 200_000, dims = 768;
        var rng = new Random(9);

        long before = GC.GetTotalMemory(forceFullCollection: true);
        long wsBefore = Environment.WorkingSet;
        var index = new BruteForceVectorIndex(dims);
        var v = new float[dims];
        var sw = Stopwatch.StartNew();
        for (long id = 0; id < n; id++)
        {
            for (int i = 0; i < dims; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
            float norm = TensorPrimitives.Norm(v);
            TensorPrimitives.Divide(v, norm, v);
            index.Upsert(id + 1, v);
        }
        double upsertMs = sw.Elapsed.TotalMilliseconds;
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long wsAfter = Environment.WorkingSet;
        Assert.Equal(n, index.Count);

        double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }
        var queries = Enumerable.Range(0, 30).Select(_ => RandomUnit(rng, dims)).ToList();
        foreach (var q in queries.Take(5)) index.Search(q, 100); // warm up

        var plain = new List<double>();
        foreach (var q in queries) { sw.Restart(); index.Search(q, 100); plain.Add(sw.Elapsed.TotalMilliseconds); }
        var filtered = new List<double>();
        foreach (var q in queries) { sw.Restart(); index.Search(q, 100, id => (id & 1) == 0); filtered.Add(sw.Elapsed.TotalMilliseconds); }
        var top10 = new List<double>();
        foreach (var q in queries) { sw.Restart(); index.Search(q, 10); top10.Add(sw.Elapsed.TotalMilliseconds); }

        sw.Restart();
        using var ms = new MemoryStream();
        await index.SaveAsync(ms, CancellationToken.None);
        double saveMs = sw.Elapsed.TotalMilliseconds;
        ms.Position = 0;
        using var loaded = new BruteForceVectorIndex(dims);
        sw.Restart();
        await loaded.LoadAsync(ms, CancellationToken.None);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(n, loaded.Count);

        var numbers = new
        {
            vectors = n,
            dims,
            cores = Environment.ProcessorCount,
            upsert_total_ms = Math.Round(upsertMs),
            search_top100_median_ms = Math.Round(Median(plain), 2),
            search_top100_max_ms = Math.Round(plain.Max(), 2),
            search_top100_filtered_half_median_ms = Math.Round(Median(filtered), 2),
            search_top10_median_ms = Math.Round(Median(top10), 2),
            managed_heap_delta_mb = Math.Round((after - before) / 1e6, 1),
            working_set_delta_mb = Math.Round((wsAfter - wsBefore) / 1e6, 1),
            index_bytes_mb = Math.Round(index.ApproximateBytes / 1e6, 1),
            file_bytes_mb = Math.Round(ms.Length / 1e6, 1),
            save_ms = Math.Round(saveMs),
            load_ms = Math.Round(loadMs),
        };
        _out.WriteLine(System.Text.Json.JsonSerializer.Serialize(numbers, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        TestPaths.WriteNumbers("index-200k", numbers);
        index.Dispose();

        Assert.True(Median(plain) < 500, "a 200k search should take well under half a second");
    }
}
