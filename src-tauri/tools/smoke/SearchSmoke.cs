using System.Diagnostics;
using System.Numerics.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using Tessera.Desktop.Search;

namespace Tessera.Smoke;

/// <summary>
/// The search library with the real model, as the core would use it: the
/// download through <see cref="ModelStore"/> from the manifest's public URLs,
/// then one embedder preferring the GPU (CoreML on macOS, DirectML on
/// Windows) and one on the CPU, each asked which of a red, a green and a blue
/// swatch "a solid red square" describes.
/// </summary>
internal static class SearchSmoke
{
    private const string Query = "a solid red square";
    private const int Batch = 8;      // what the core's embedding worker hands over
    private const int Batches = 4;

    private static readonly string[] Names = ["red", "green", "blue"];

    private static readonly string[] Sentences =
    [
        "a photo of a dog on a beach", "a city skyline at night", "a bowl of fruit on a table",
        "a screenshot of a spreadsheet", "two people hiking in the mountains", "a close-up of a flower",
        "an old car parked in the street", "a diagram with arrows", "snow on a pine forest", "a cup of coffee",
    ];

    private static ILoggerFactory Logs() => LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
        .SetMinimumLevel(LogLevel.Information));

    private static SearchOptions Options(string modelsDir, ModelManifest manifest, bool preferGpu) =>
        new() { ModelsDir = modelsDir, ModelId = manifest.ModelId, PreferGpu = preferGpu };

    /// <summary>Downloads (or verifies) the model only. CI caches the folder after this.</summary>
    public static async Task<int> ModelAsync(string modelsDir)
    {
        var report = new Report("Model download");
        var manifest = ModelManifest.Embedded();
        using var logs = Logs();
        using var store = new ModelStore(Options(modelsDir, manifest, false), null, logs.CreateLogger<ModelStore>());

        var sw = Stopwatch.StartNew();
        var cached = await store.IsCompleteAsync(manifest, CancellationToken.None);
        var verifySeconds = sw.Elapsed.TotalSeconds;
        sw.Restart();
        await store.EnsureAsync(manifest, new Printer(), CancellationToken.None);
        var seconds = sw.Elapsed.TotalSeconds;

        var mb = manifest.TotalBytes / 1048576.0;
        report.Number("model", manifest.ModelId);
        report.Number("files", string.Join(", ", manifest.Files.Select(f => $"{f.Name} {f.Bytes / 1048576.0:F0} MB")));
        report.Number("total", mb, "MB");
        report.Number("from cache", cached ? "yes" : "no");
        report.Number(cached ? "verify (sha256 of every file)" : "download and verify", cached ? verifySeconds : seconds, "s");
        if (!cached)
        {
            report.Number("download rate", mb / seconds, "MB/s");
        }

        report.Check(await store.IsCompleteAsync(manifest, CancellationToken.None), "every manifest file present with its sha256");
        return report.Finish();
    }

    /// <param name="loadFrom">
    /// A folder holding vision.onnx, text.onnx and tokenizer.model to load as
    /// they are, without the manifest's hashes. Null loads through the store,
    /// as the core does.
    /// </param>
    /// <param name="requireGpu">
    /// PreferGpu=true must land on this platform's GPU provider: CoreML on
    /// macOS, DirectML on Windows. On CoreML the compiled-model cache is
    /// cleared first, so the first load is a buyer's first launch and a
    /// second embedder shows the warm start.
    /// </param>
    public static async Task<int> RunAsync(string modelsDir, bool requireGpu, string? loadFrom)
    {
        var ort = OrtEnv.Instance().GetVersionString();
        var report = new Report($"Search with the real model ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, "
            + $"{Environment.ProcessorCount} cores, ONNX Runtime {ort}{(loadFrom is null ? "" : $", model from {loadFrom}")})");
        var manifest = ModelManifest.Embedded();
        using var logs = Logs();
        using var store = new ModelStore(Options(modelsDir, manifest, false), null, logs.CreateLogger<ModelStore>());
        report.Number("model", manifest.ModelId);

        if (requireGpu && OperatingSystem.IsMacOS())
        {
            var cache = Path.Combine(loadFrom ?? store.DirectoryFor(manifest.ModelId), "coreml");
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
                Report.Line($"cleared {cache}: the first load compiles, as on a buyer's first launch");
            }
        }

        // Vectors of the 3 swatches and the sentence, per device, to compare.
        var vectors = new Dictionary<bool, float[][]>();
        foreach (var preferGpu in new[] { true, false })
        {
            var label = preferGpu ? "PreferGpu=true" : "PreferGpu=false";
            try
            {
                vectors[preferGpu] = await MeasureAsync(report, label, preferGpu, requireGpu, loadFrom,
                    new SiglipEmbedder(Options(modelsDir, manifest, preferGpu), store, logs.CreateLogger<SiglipEmbedder>(), manifest));
            }
            catch (Exception e)
            {
                report.Check(false, $"{label}: load and embed", e.ToString());
            }
        }

        if (vectors.Count == 2)
        {
            var cosines = Enumerable.Range(0, vectors[true].Length)
                .Select(i => TensorPrimitives.CosineSimilarity(vectors[true][i], vectors[false][i])).ToArray();
            report.Number("GPU vs CPU vectors, cosine (red, green, blue, sentence)", string.Join(", ", cosines.Select(c => c.ToString("F5", System.Globalization.CultureInfo.InvariantCulture))));
            report.Check(cosines.Min() >= 0.999, "GPU and CPU vectors agree (cosine >= 0.999)", $"{cosines.Min():F5}");
        }

        if (requireGpu && OperatingSystem.IsMacOS())
        {
            // The same model again, now from the cache the first load wrote.
            using var warm = new SiglipEmbedder(Options(modelsDir, manifest, true), store, logs.CreateLogger<SiglipEmbedder>(), manifest);
            var sw = Stopwatch.StartNew();
            if (loadFrom is null)
            {
                await warm.EnsureReadyAsync(null, CancellationToken.None);
            }
            else
            {
                warm.LoadFromDirectory(loadFrom);
            }

            report.Number("PreferGpu=true: second load (verify + sessions, CoreML cache warm)", sw.Elapsed.TotalSeconds, "s");
            report.Check(warm.Device == EmbedDevice.CoreML, "the second load is on CoreML too", $"{warm.Device}");
            var cache = Path.Combine(warm.ModelDirectory!, "coreml");
            var files = Directory.Exists(cache) ? Directory.GetFiles(cache, "*", SearchOption.AllDirectories) : [];
            report.Number("CoreML cache", $"{files.Length} files, {files.Sum(f => new FileInfo(f).Length) / 1048576.0:F0} MB in "
                + string.Join(", ", Directory.Exists(cache) ? Directory.GetDirectories(cache).Select(Path.GetFileName) : []));
        }

        return report.Finish();
    }

    private static async Task<float[][]> MeasureAsync(
        Report report, string label, bool preferGpu, bool requireGpu, string? loadFrom, SiglipEmbedder embedder)
    {
        using var owned = embedder;
        using var red = Images.Solid(new SKColor(255, 0, 0), 256, 256);
        using var green = Images.Solid(new SKColor(0, 255, 0), 256, 256);
        using var blue = Images.Solid(new SKColor(0, 0, 255), 256, 256);
        var batch = Enumerable.Range(0, Batch).Select(i => Images.Picture(100 + i, 640, 480)).ToList();
        try
        {
            var sw = Stopwatch.StartNew();
            if (loadFrom is null)
            {
                await embedder.EnsureReadyAsync(new Printer(), CancellationToken.None);
            }
            else
            {
                embedder.LoadFromDirectory(loadFrom);
            }

            var device = embedder.Device?.ToString() ?? "none";
            report.Number($"{label}: Device", device + (embedder.DeviceName is { } n ? $" ({n})" : "")
                + (embedder.FixedImageBatch is { } b ? $", batch fixed at {b}" : ""));
            report.Number($"{label}: load (verify + sessions{(preferGpu && embedder.Device == EmbedDevice.CoreML ? ", CoreML compile" : "")})", sw.Elapsed.TotalSeconds, "s");
            if (preferGpu && requireGpu)
            {
                var expected = OperatingSystem.IsMacOS() ? EmbedDevice.CoreML : EmbedDevice.DirectML;
                report.Check(embedder.Device == expected,
                    $"{label} chose {expected}", $"Device = {device}; the log above says why if it fell back");
            }

            // Red, green, blue and the sentence.
            sw.Restart();
            var images = embedder.EmbedImages([red, green, blue]);
            var firstImageMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var text = embedder.EmbedText(Query);
            var firstTextMs = sw.Elapsed.TotalMilliseconds;
            var scores = images.Select(v => TensorPrimitives.Dot(v, text)).ToArray();
            var ranking = string.Join(" > ", Enumerable.Range(0, 3).OrderByDescending(i => scores[i])
                .Select(i => $"{Names[i]} {scores[i]:F4}"));
            report.Number($"{label}: \"{Query}\"", ranking);
            report.Check(Array.IndexOf(scores, scores.Max()) == 0, $"{label}: the red swatch ranks first", ranking);
            report.Number($"{label}: first call (3 swatches / 1 sentence)", $"{firstImageMs:F0} ms / {firstTextMs:F0} ms");

            // Throughput, after the first call has paid for any lazy setup.
            embedder.EmbedImages(batch);
            sw.Restart();
            for (var k = 0; k < Batches; k++)
            {
                embedder.EmbedImages(batch);
            }

            report.Number($"{label}: images per second (batch {Batch}, 640x480)", Batch * Batches / sw.Elapsed.TotalSeconds, "img/s");

            embedder.EmbedText("warm up");
            var times = new List<double>();
            foreach (var s in Sentences)
            {
                sw.Restart();
                embedder.EmbedText(s);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            report.Number($"{label}: text, median of {times.Count}", times[times.Count / 2], "ms");
            return [.. images, text];
        }
        finally
        {
            foreach (var b in batch)
            {
                b.Dispose();
            }
        }
    }

    /// <summary>One line per file every 20%, synchronously (Progress&lt;T&gt; would post to the pool).</summary>
    private sealed class Printer : IProgress<ModelDownloadProgress>
    {
        private readonly Dictionary<string, int> _shown = [];

        public void Report(ModelDownloadProgress value)
        {
            if (value.TotalBytes is not > 0)
            {
                return;
            }

            var step = (int)(value.ReceivedBytes * 5 / value.TotalBytes.Value);
            if (_shown.TryGetValue(value.File, out var last) && last >= step)
            {
                return;
            }

            _shown[value.File] = step;
            Console.WriteLine($"     {value.File}: {value.ReceivedBytes / 1048576.0:F0} of {value.TotalBytes.Value / 1048576.0:F0} MB");
        }
    }
}
