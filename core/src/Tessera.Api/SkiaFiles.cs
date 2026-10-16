using SkiaSharp;

namespace Tessera.Api;

/// <summary>
/// Open image files for Skia through .NET, never through Skia's own
/// <c>fopen</c>.
/// </summary>
/// <remarks>
/// <c>SKBitmap.Decode(string)</c> hands the path to the native library,
/// which opens it with the C runtime: ANSI, and bound by MAX_PATH. A
/// thumbnail under a data dir 190 characters deep came out at 259 characters
/// and every embedding failed with "could not be opened"; a library under a
/// folder with a non-ASCII name would fail the same way. A .NET
/// <see cref="FileStream"/> has neither limit, and Skia reads from it
/// happily.
/// </remarks>
public static class SkiaFiles
{
    /// <summary>Decode a whole image, or null when Skia cannot read it. Pixels as stored, not turned.</summary>
    public static SKBitmap? Decode(string path) => DecodeWithOrigin(path).Bitmap;

    /// <summary>
    /// Decode a whole image and say how it is meant to be turned (its EXIF
    /// orientation). Skia's decoder hands back the pixels as stored; a phone
    /// held upright stores them sideways and sets this.
    /// </summary>
    public static (SKBitmap? Bitmap, SKEncodedOrigin Origin) DecodeWithOrigin(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        return codec is null ? (null, SKEncodedOrigin.TopLeft) : (SKBitmap.Decode(codec), codec.EncodedOrigin);
    }

    /// <summary>The header only: dimensions without decoding pixels. Null when Skia cannot read it.</summary>
    public static SKImageInfo? Info(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        return codec?.Info;
    }

    /// <summary>EXIF orientations 5–8: shown with width and height exchanged.</summary>
    public static bool IsQuarterTurn(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// The bitmap as it is meant to be seen. Returns the same instance for
    /// an upright image, a new one otherwise (the caller disposes both).
    /// Done on the thumbnail, not the original: turning 320 px costs nothing,
    /// turning 24 megapixels does.
    /// </summary>
    public static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
        }

        float w = source.Width, h = source.Height;
        // Where a stored pixel (x, y) lands: x' = a*x + b*y + c, y' = d*x + e*y + f.
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),      // 2: mirrored
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),  // 3: upside down
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),    // 4: mirrored, upside down
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),        // 5: transposed
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),      // 6: turn a quarter clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),  // 7: transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),    // 8: turn a quarter anticlockwise
            _ => SKMatrix.Identity,
        };

        var info = IsQuarterTurn(origin)
            ? new SKImageInfo(source.Height, source.Width, source.ColorType, source.AlphaType, source.ColorSpace)
            : source.Info;
        var turned = new SKBitmap(info);
        using var canvas = new SKCanvas(turned);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0);
        return turned;
    }
}
