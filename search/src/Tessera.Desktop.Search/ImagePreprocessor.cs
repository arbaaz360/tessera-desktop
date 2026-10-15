using SkiaSharp;

namespace Tessera.Desktop.Search;

/// <summary>
/// Turns any bitmap into the vision tower's input: squash-resized to
/// <c>size × size</c> (no crop, as SigLIP2 does), RGB in
/// <c>(x / 255 − 0.5) / 0.5</c>, planar NCHW float32. Alpha is dropped, not
/// composited, which is what the reference processor does with RGBA input.
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>Trilinear sampling: antialiased when shrinking large originals, cheap.</summary>
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static int InputLength(int size) => 3 * size * size;

    /// <summary>Writes one image's CHW planes into <paramref name="target"/> (length <c>3·size·size</c>).</summary>
    public static void Fill(SKBitmap bitmap, int size, Span<float> target)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (target.Length != InputLength(size))
            throw new ArgumentException($"Target must hold {InputLength(size)} floats.", nameof(target));

        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bool direct = bitmap.Width == size && bitmap.Height == size
                      && bitmap.ColorType == SKColorType.Rgba8888
                      && bitmap.AlphaType != SKAlphaType.Premul;
        SKBitmap? resized = null;
        try
        {
            SKBitmap src = bitmap;
            if (!direct)
            {
                resized = new SKBitmap(info);
                if (!bitmap.ScalePixels(resized, Sampling))
                    throw new InvalidOperationException("SkiaSharp could not scale the bitmap.");
                src = resized;
            }
            ReadOnlySpan<byte> px = src.GetPixelSpan();
            int plane = size * size;
            const float scale = 1f / 127.5f;
            for (int p = 0; p < plane; p++)
            {
                int o = p * 4;
                target[p] = px[o] * scale - 1f;
                target[plane + p] = px[o + 1] * scale - 1f;
                target[2 * plane + p] = px[o + 2] * scale - 1f;
            }
        }
        finally { resized?.Dispose(); }
    }

    public static float[] ToTensor(SKBitmap bitmap, int size)
    {
        var data = new float[InputLength(size)];
        Fill(bitmap, size, data);
        return data;
    }
}
