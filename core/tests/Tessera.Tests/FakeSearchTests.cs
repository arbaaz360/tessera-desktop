using System.Security.Cryptography;
using SkiaSharp;
using Tessera.Api.Search;

namespace Tessera.Tests;

/// <summary>
/// The stand-ins the core ships until the search library is merged: the
/// embedder must be deterministic and tell pictures apart; the index must
/// rank by cosine, honour a filter, and round-trip through its file.
/// </summary>
public sealed class FakeSearchTests
{
    [Fact]
    public void The_same_picture_gets_the_same_vector_and_a_different_one_does_not()
    {
        var embedder = new FakeImageEmbedder();
        using var a = Bitmap(1);
        using var a2 = Bitmap(1);
        using var b = Bitmap(2);

        var va = embedder.EmbedImage(a);
        Assert.Equal(768, va.Length);
        Assert.Equal(va, embedder.EmbedImage(a2));
        Assert.NotEqual(va, embedder.EmbedImage(b));
        Assert.Equal(1.0, Norm(va), 4);
        Assert.True(Dot(va, embedder.EmbedImage(b)) < 0.5);
    }

    [Fact]
    public void A_webp_thumbnail_round_trip_still_tells_pictures_apart()
    {
        // The embedding worker reads the WebP the thumbnail worker wrote; two
        // different thumbnails must decode to two different pixel buffers.
        var embedder = new FakeImageEmbedder();
        using var a = Decode(Webp(Bitmap(1)));
        using var b = Decode(Webp(Bitmap(2)));
        Assert.True(a.GetPixelSpan().Length > 0);
        Assert.NotEqual(embedder.EmbedImage(a), embedder.EmbedImage(b));
    }

    [Fact]
    public void A_batch_answers_in_input_order_with_the_same_vectors_as_one_at_a_time()
    {
        var embedder = new FakeImageEmbedder();
        using var a = Bitmap(1);
        using var b = Bitmap(2);
        using var c = Bitmap(3);
        var batch = embedder.EmbedImages([a, b, c]);
        Assert.Equal(3, batch.Length);
        Assert.Equal(embedder.EmbedImage(a), batch[0]);
        Assert.Equal(embedder.EmbedImage(b), batch[1]);
        Assert.Equal(embedder.EmbedImage(c), batch[2]);
        Assert.Empty(embedder.EmbedImages([]));
    }

    [Fact]
    public void The_index_hands_a_stored_vector_back()
    {
        using var index = new InMemoryVectorIndex(4);
        index.Upsert(7, [0.5f, 0.5f, 0.5f, 0.5f]);
        Assert.True(index.TryGet(7, out var v));
        Assert.Equal([0.5f, 0.5f, 0.5f, 0.5f], v);
        v[0] = 9f;   // a copy, not the index's own memory
        Assert.True(index.TryGet(7, out var again));
        Assert.Equal(0.5f, again[0]);
        Assert.False(index.TryGet(8, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Text_is_hashed_by_its_words()
    {
        var embedder = new FakeImageEmbedder();
        Assert.Equal(embedder.EmbedText("Red  Saree"), embedder.EmbedText("red saree"));
        Assert.NotEqual(embedder.EmbedText("red saree"), embedder.EmbedText("saree red"));
        Assert.Throws<ArgumentException>(() => embedder.EmbedText("  "));
    }

    [Fact]
    public async Task The_index_ranks_filters_removes_and_round_trips()
    {
        using var index = new InMemoryVectorIndex(8);
        var rng = new Random(7);
        var vectors = new Dictionary<long, float[]>();
        for (long id = 1; id <= 500; id++)
        {
            var v = Unit(Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray());
            vectors[id] = v;
            index.Upsert(id, v);
        }

        Assert.Equal(500, index.Count);
        Assert.True(index.Contains(250));

        var query = vectors[42];
        var hits = index.Search(query, 5);
        Assert.Equal(5, hits.Count);
        Assert.Equal(42, hits[0].Id);
        Assert.Equal(1f, hits[0].Score, 4);
        Assert.True(hits.Zip(hits.Skip(1)).All(p => p.First.Score >= p.Second.Score), "best first");

        // The honest top-5 against a brute-force pass.
        var expected = vectors.OrderByDescending(kv => Dot(query, kv.Value)).Take(5).Select(kv => kv.Key).ToArray();
        Assert.Equal(expected, hits.Select(h => h.Id).ToArray());

        var evens = index.Search(query, 3, id => id % 2 == 0);
        Assert.Equal(3, evens.Count);
        Assert.All(evens, h => Assert.Equal(0, h.Id % 2));

        Assert.True(index.Remove(42));
        Assert.False(index.Remove(42));
        Assert.NotEqual(42, index.Search(query, 1)[0].Id);
        Assert.Equal(499, index.Count);

        using var stream = new MemoryStream();
        await index.SaveAsync(stream, CancellationToken.None);
        stream.Position = 0;

        using var loaded = new InMemoryVectorIndex(8);
        await loaded.LoadAsync(stream, CancellationToken.None);
        Assert.Equal(499, loaded.Count);
        Assert.Equal(index.Search(query, 10).Select(h => h.Id), loaded.Search(query, 10).Select(h => h.Id));

        stream.Position = 0;
        using var wrongDims = new InMemoryVectorIndex(16);
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongDims.LoadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Seeds_expand_to_unit_vectors()
    {
        var v = FakeImageEmbedder.FromSeed(SHA256.HashData("x"u8));
        Assert.Equal(768, v.Length);
        Assert.Equal(1.0, Norm(v), 4);
        Assert.Equal(v, FakeImageEmbedder.FromSeed(SHA256.HashData("x"u8)));
    }

    private static SKBitmap Bitmap(int seed)
    {
        var bitmap = new SKBitmap(32, 24);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor((byte)(seed * 40), (byte)(seed * 90), (byte)(seed * 150)));
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawCircle(16, 12, 4 + seed, paint);
        return bitmap;
    }

    private static byte[] Webp(SKBitmap bitmap)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Webp, 80))
        {
            return data.ToArray();
        }
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("decode failed");

    private static float[] Unit(float[] v)
    {
        var n = (float)Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    private static double Norm(float[] v) => Math.Sqrt(v.Sum(x => (double)x * x));

    private static float Dot(float[] a, float[] b) => a.Zip(b).Sum(p => p.First * p.Second);
}
