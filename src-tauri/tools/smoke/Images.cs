using System.Diagnostics;
using SkiaSharp;

namespace Tessera.Smoke;

/// <summary>
/// A small library of generated pictures: red, green and blue swatches at the
/// top, then PNGs and JPEGs of assorted sizes, half of them one folder down,
/// every one distinct. On macOS one more is converted to HEIC with <c>sips</c>,
/// when the machine can encode HEIC.
/// </summary>
internal static class Images
{
    public static readonly (string Name, SKColor Color)[] Swatches =
    [
        ("red.png", new SKColor(255, 0, 0)),
        ("green.png", new SKColor(0, 255, 0)),
        ("blue.png", new SKColor(0, 0, 255)),
    ];

    private static readonly (int W, int H)[] Sizes =
        [(640, 480), (480, 640), (800, 600), (1024, 768), (300, 300), (1200, 400)];

    public static int Run(string dir, int count)
    {
        var report = new Report("Generated library");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "2024"));

        foreach (var (name, color) in Swatches)
        {
            using var swatch = Solid(color, 512, 512);
            Save(swatch, Path.Combine(dir, name), SKEncodedImageFormat.Png);
        }

        int png = 0, jpeg = 0;
        for (var i = 0; i < count - Swatches.Length; i++)
        {
            var (w, h) = Sizes[i % Sizes.Length];
            using var bitmap = Picture(i, w, h);
            var sub = i % 2 == 0 ? dir : Path.Combine(dir, "2024");
            if (i % 2 == 0)
            {
                Save(bitmap, Path.Combine(sub, $"picture-{i:D3}.png"), SKEncodedImageFormat.Png);
                png++;
            }
            else
            {
                Save(bitmap, Path.Combine(sub, $"picture-{i:D3}.jpg"), SKEncodedImageFormat.Jpeg);
                jpeg++;
            }
        }

        var heic = OperatingSystem.IsMacOS() && TryHeic(Path.Combine(dir, "2024", "photo.heic"));

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        report.Number("files", files.Length.ToString());
        report.Number("swatches (PNG)", Swatches.Length.ToString());
        report.Number("PNG", png.ToString());
        report.Number("JPEG", jpeg.ToString());
        report.Number("HEIC", heic ? "1" : "0");
        report.Check(files.Length == count + (heic ? 1 : 0), $"{files.Length} files written to {dir}");
        return report.Finish();
    }

    /// <summary>A solid square, the shape the search check embeds.</summary>
    public static SKBitmap Solid(SKColor color, int w, int h)
    {
        var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>A gradient with a few shapes on it; the seed makes every one different.</summary>
    public static SKBitmap Picture(int seed, int w, int h)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var a = SKColor.FromHsv(seed * 37 % 360, 60, 90);
        var b = SKColor.FromHsv((seed * 37 + 140) % 360, 70, 40);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [a, b], SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, w, h, fill);
        }

        using var paint = new SKPaint { IsAntialias = true };
        for (var k = 0; k < 6; k++)
        {
            paint.Color = SKColor.FromHsv(random.Next(360), 80, 95, (byte)random.Next(120, 255));
            var r = random.Next(10, Math.Min(w, h) / 3);
            if (k % 2 == 0)
            {
                canvas.DrawCircle(random.Next(w), random.Next(h), r, paint);
            }
            else
            {
                canvas.DrawRect(random.Next(w), random.Next(h), r * 2, r, paint);
            }
        }

        canvas.Flush();
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 88) ?? throw new InvalidOperationException($"encode {format} failed");
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary><c>sips</c> writes HEIC only where the OS has an HEVC encoder; a VM may not.</summary>
    private static bool TryHeic(string target)
    {
        var source = Path.Combine(Path.GetTempPath(), $"tessera-smoke-{Environment.ProcessId}.png");
        using (var bitmap = Picture(999, 1280, 960))
        {
            Save(bitmap, source, SKEncodedImageFormat.Png);
        }

        try
        {
            var psi = new ProcessStartInfo("sips")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-s", "format", "heic", source, "--out", target })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var made = p.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
            if (!made)
            {
                Report.Line($"sips could not write HEIC (exit {p.ExitCode}): {stderr.Result.Trim()} {stdout.Result.Trim()}");
                File.Delete(target);
            }
            else
            {
                Report.Line($"sips wrote {target} ({new FileInfo(target).Length / 1024} KB)");
            }

            return made;
        }
        finally
        {
            File.Delete(source);
        }
    }
}
