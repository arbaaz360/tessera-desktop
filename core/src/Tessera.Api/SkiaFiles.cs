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
    /// <summary>Decode a whole image, or null when Skia cannot read it.</summary>
    public static SKBitmap? Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    /// <summary>The header only: dimensions without decoding pixels. Null when Skia cannot read it.</summary>
    public static SKImageInfo? Info(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        return codec?.Info;
    }
}
