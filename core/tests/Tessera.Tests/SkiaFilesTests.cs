using SkiaSharp;
using Tessera.Api;

namespace Tessera.Tests;

/// <summary>
/// Images are opened through .NET, so a path longer than MAX_PATH or with a
/// non-ASCII folder in it still decodes. Both happened: a data dir deep in a
/// temp folder, and any buyer whose user name has an accent.
/// </summary>
public sealed class SkiaFilesTests
{
    [Fact]
    public void Decodes_from_a_path_longer_than_max_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N"));
        var dir = root;
        while (dir.Length < 270)
        {
            dir = Path.Combine(dir, "a-long-folder-name-to-push-past-the-limit");
        }

        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "picture.webp");
            Assert.True(path.Length > 260);
            WriteWebp(path);

            using var bitmap = SkiaFiles.Decode(path);
            Assert.NotNull(bitmap);
            Assert.Equal(40, bitmap.Width);
            Assert.Equal(30, SkiaFiles.Info(path)!.Value.Height);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Decodes_from_a_path_with_non_ascii_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N"), "Jürgen", "写真", "Ωmega");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "bild.webp");
            WriteWebp(path);
            using var bitmap = SkiaFiles.Decode(path);
            Assert.NotNull(bitmap);
            Assert.Equal(40, bitmap.Width);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(root))!)!, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_file_is_null_not_an_exception()
    {
        var path = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N") + ".webp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not an image");
        try
        {
            Assert.Null(SkiaFiles.Decode(path));
            Assert.Null(SkiaFiles.Info(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteWebp(string path)
    {
        using var bitmap = new SKBitmap(40, 30);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 80);
        File.WriteAllBytes(path, data.ToArray());
    }
}
