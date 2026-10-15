using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>
/// A stand-in for the SigLIP2 embedder, so the core runs end to end before
/// the search library is merged (CONTRACT §6).
/// </summary>
/// <remarks>
/// Deterministic: the vector is derived from the SHA-256 of the pixels it is
/// shown, expanded to 768 dimensions and L2-normalised. The same picture
/// always gets the same vector, so "more like this" finds the picture's own
/// copies with a score of 1.0 and everything else lands near 0. Text is
/// hashed the same way, word by word, so a query is a vector of the right
/// shape - it just does not mean anything. It is never ready-but-wrong: its
/// model id says what it is.
/// </remarks>
public sealed class FakeImageEmbedder : IImageEmbedder
{
    public const int Dims = 768;

    public string ModelId => "fake-sha256-768";

    public int Dimensions => Dims;

    public bool IsReady => true;

    public EmbedDevice? Device => EmbedDevice.Cpu;

    public Task EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct) => Task.CompletedTask;

    public float[] EmbedImage(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var pixels = bitmap.GetPixelSpan();
        return FromSeed(SHA256.HashData(pixels));
    }

    public float[] EmbedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Nothing to embed.", nameof(text));
        }

        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return FromSeed(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', words))));
    }

    /// <summary>32 seed bytes → 768 floats: 24 counter-hashed blocks of 32, each byte centred on zero, then normalised.</summary>
    public static float[] FromSeed(ReadOnlySpan<byte> seed)
    {
        var v = new float[Dims];
        Span<byte> buffer = stackalloc byte[seed.Length + 4];
        seed.CopyTo(buffer);
        Span<byte> block = stackalloc byte[32];

        for (var i = 0; i < Dims / 32; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer[seed.Length..], i);
            SHA256.HashData(buffer, block);
            for (var j = 0; j < 32; j++)
            {
                v[i * 32 + j] = (block[j] - 127.5f) / 127.5f;
            }
        }

        var norm = 0d;
        foreach (var x in v)
        {
            norm += x * x;
        }

        var inv = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }

        return v;
    }
}
