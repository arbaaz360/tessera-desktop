using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit.Abstractions;

namespace Tessera.Desktop.Search.Tests;

/// <summary>
/// One loaded model per test run (the text tower alone is half a gigabyte).
/// Tests that need it pass with a "skipped" line when no local export exists.
/// </summary>
public sealed class ModelFixture : IDisposable
{
    public SiglipEmbedder? Cpu { get; }
    public SiglipEmbedder? Gpu { get; }
    public string? LoadError { get; }
    public double CpuLoadMs { get; }
    public double GpuLoadMs { get; }

    public ModelFixture()
    {
        if (TestPaths.ModelDir is null) return;
        var manifest = ModelManifest.Embedded();
        var sw = Stopwatch.StartNew();
        try
        {
            // TESSERA_CPU_THREADS lets a benchmark run try a thread count; 0 is ONNX Runtime's default.
            int.TryParse(Environment.GetEnvironmentVariable("TESSERA_CPU_THREADS"), out int cpuThreads);
            var cpuOptions = new SearchOptions { ModelsDir = Path.GetDirectoryName(TestPaths.ModelDir)!, PreferGpu = false, CpuThreads = cpuThreads };
            Cpu = new SiglipEmbedder(cpuOptions, new ModelStore(cpuOptions), null, manifest);
            Cpu.LoadFromDirectory(TestPaths.ModelDir);
            CpuLoadMs = sw.Elapsed.TotalMilliseconds;

            var gpuOptions = new SearchOptions { ModelsDir = cpuOptions.ModelsDir, PreferGpu = true };
            sw.Restart();
            Gpu = new SiglipEmbedder(gpuOptions, new ModelStore(gpuOptions), null, manifest);
            Gpu.LoadFromDirectory(TestPaths.ModelDir);
            GpuLoadMs = sw.Elapsed.TotalMilliseconds;
            if (Gpu.Device == EmbedDevice.Cpu)
            {
                Gpu.Dispose();
                Gpu = null;
            }
        }
        catch (Exception e)
        {
            LoadError = e.ToString();
        }
    }

    public void Dispose()
    {
        Cpu?.Dispose();
        Gpu?.Dispose();
    }
}

public class EmbedderTests : IClassFixture<ModelFixture>
{
    private readonly ModelFixture _models;
    private readonly ITestOutputHelper _out;

    public EmbedderTests(ModelFixture models, ITestOutputHelper output)
    {
        _models = models;
        _out = output;
    }

    private SiglipEmbedder? Model()
    {
        if (_models.LoadError is not null) throw new InvalidOperationException(_models.LoadError);
        if (_models.Cpu is null) _out.WriteLine("skipped: no local model export (models/out or TESSERA_MODELS_DIR)");
        return _models.Cpu;
    }

    private static float Cosine(float[] a, float[] b) => TensorPrimitives.CosineSimilarity(a, b);

    private static SKBitmap Swatch(byte r, byte g, byte b, int w = 320, int h = 240)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(r, g, b));
        return bmp;
    }

    [Fact]
    public void LoadsAndReportsDevice()
    {
        var m = Model();
        if (m is null) return;
        Assert.True(m.IsReady);
        Assert.Equal("siglip2-base-patch16-256", m.ModelId);
        Assert.Equal(768, m.Dimensions);
        Assert.Equal(EmbedDevice.Cpu, m.Device);
        _out.WriteLine($"cpu load {_models.CpuLoadMs:F0} ms; gpu: {(_models.Gpu is null ? "none" : $"{_models.Gpu.Device} {_models.Gpu.DeviceName} load {_models.GpuLoadMs:F0} ms")}");
    }

    [Fact]
    public void SameImageTwiceGivesIdenticalVectors()
    {
        var m = Model();
        if (m is null) return;
        using var bmp = Swatch(200, 40, 40);
        float[] a = m.EmbedImage(bmp);
        float[] b = m.EmbedImage(bmp);
        Assert.Equal(a, b);
        Assert.Equal(768, a.Length);
        Assert.InRange(TensorPrimitives.Norm(a), 0.999f, 1.001f);
    }

    [Fact]
    public void EmptyTextThrows()
    {
        var m = Model();
        if (m is null) return;
        Assert.Throws<ArgumentException>(() => m.EmbedText(""));
        Assert.Throws<ArgumentException>(() => m.EmbedText("   \n"));
    }

    [Fact]
    public void NotReadyThrows()
    {
        var options = new SearchOptions { ModelsDir = Path.GetTempPath(), PreferGpu = false };
        using var fresh = new SiglipEmbedder(options, new ModelStore(options), null, ModelManifest.Embedded());
        Assert.False(fresh.IsReady);
        Assert.Null(fresh.Device);
        using var bmp = Swatch(1, 2, 3);
        Assert.Throws<InvalidOperationException>(() => fresh.EmbedImage(bmp));
        Assert.Throws<InvalidOperationException>(() => fresh.EmbedText("x"));
    }

    [Fact]
    public void ImageEmbeddingsMatchTheReferenceExport()
    {
        var m = Model();
        if (m is null) return;
        var expected = ExpectedFixtures.Load();
        var sb = new StringBuilder();
        float worstExact = 1, worstResized = 1;
        foreach (var fx in expected.Images)
        {
            using var bmp = SKBitmap.Decode(Path.Combine(TestPaths.Fixtures, fx.File));
            Assert.NotNull(bmp);
            float c = Cosine(m.EmbedImage(bmp), fx.Embedding);
            bool resized = bmp.Width != 256 || bmp.Height != 256;
            if (resized) worstResized = Math.Min(worstResized, c); else worstExact = Math.Min(worstExact, c);
            sb.AppendLine($"{fx.File,-28} {bmp.Width}x{bmp.Height}  cosine vs reference {c:F5}");
        }
        _out.WriteLine(sb.ToString());
        TestPaths.WriteNumbers("embedder-fidelity", new { image_256_min_cosine = worstExact, image_resized_min_cosine = worstResized });
        Assert.True(worstExact >= 0.999f, $"256x256 inputs should match the reference: {worstExact}");
        Assert.True(worstResized >= 0.98f, $"resized inputs drift from PIL's resampling: {worstResized}");
    }

    [Fact]
    public void TextEmbeddingsMatchTheReferenceExport()
    {
        var m = Model();
        if (m is null) return;
        var expected = ExpectedFixtures.Load();
        float worst = 1;
        foreach (var fx in expected.Texts)
        {
            float c = Cosine(m.EmbedText(fx.Text), fx.Embedding);
            worst = Math.Min(worst, c);
            _out.WriteLine($"{c:F5}  {fx.Text[..Math.Min(40, fx.Text.Length)]}");
        }
        Assert.True(worst >= 0.999f, $"text embeddings drift from the reference: {worst}");
    }

    [Fact]
    public void RedBeatsBlueForTheQueryRed()
    {
        var m = Model();
        if (m is null) return;
        using var red = Swatch(220, 30, 30);
        using var blue = Swatch(30, 60, 220);
        using var green = Swatch(30, 180, 60);
        var vectors = new Dictionary<string, float[]>
        {
            ["red"] = m.EmbedImage(red),
            ["blue"] = m.EmbedImage(blue),
            ["green"] = m.EmbedImage(green),
        };
        foreach (string colour in vectors.Keys)
        {
            float[] q = m.EmbedText($"a solid {colour} image");
            var ranked = vectors.OrderByDescending(kv => Cosine(q, kv.Value)).ToList();
            _out.WriteLine($"query '{colour}': " + string.Join(", ", ranked.Select(kv => $"{kv.Key} {Cosine(q, kv.Value):F3}")));
            Assert.Equal(colour, ranked[0].Key);
        }
    }

    /// <summary>
    /// The GPU provider runs the same graphs, including the text tower's int8
    /// token table (Gather, Cast, Mul), and must land on the same vectors.
    /// </summary>
    [Fact]
    public void GpuMatchesCpu()
    {
        var m = Model();
        if (m is null) return;
        if (_models.Gpu is null) { _out.WriteLine("skipped: no GPU provider on this machine"); return; }
        var expected = ExpectedFixtures.Load();
        float worstText = 1, worstImage = 1;
        foreach (var fx in expected.Texts)
            worstText = Math.Min(worstText, Cosine(m.EmbedText(fx.Text), _models.Gpu.EmbedText(fx.Text)));
        foreach (var fx in expected.Images)
        {
            using var bmp = SKBitmap.Decode(Path.Combine(TestPaths.Fixtures, fx.File));
            worstImage = Math.Min(worstImage, Cosine(m.EmbedImage(bmp), _models.Gpu.EmbedImage(bmp)));
        }
        _out.WriteLine($"{_models.Gpu.Device} vs CPU: text cosine min {worstText:F5}, image cosine min {worstImage:F5}");
        TestPaths.WriteNumbers("gpu-vs-cpu", new { device = _models.Gpu.Device.ToString(), text_min_cosine = worstText, image_min_cosine = worstImage });
        Assert.True(worstText >= 0.999f, $"text on {_models.Gpu.Device} drifts from CPU: {worstText}");
        Assert.True(worstImage >= 0.999f, $"images on {_models.Gpu.Device} drift from CPU: {worstImage}");
    }

    [Fact]
    public void ConcurrentEmbedsAreStable()
    {
        var m = Model();
        if (m is null) return;
        using var bmp = Swatch(90, 150, 220);
        float[] image = m.EmbedImage(bmp);
        float[] text = m.EmbedText("a photo of a mountain lake at sunrise");
        Parallel.For(0, 16, i =>
        {
            if (i % 2 == 0) Assert.Equal(image, m.EmbedImage(bmp));
            else Assert.Equal(text, m.EmbedText("a photo of a mountain lake at sunrise"));
        });
    }

    [Fact]
    public void DependencyInjectionRegistersSingletons()
    {
        var services = new ServiceCollection();
        services.AddTesseraSearch(new SearchOptions { ModelsDir = Path.Combine(Path.GetTempPath(), "tessera-di-test") });
        using var sp = services.BuildServiceProvider();
        var embedder = sp.GetRequiredService<IImageEmbedder>();
        Assert.Same(embedder, sp.GetRequiredService<IImageEmbedder>());
        Assert.IsType<SiglipEmbedder>(embedder);
        Assert.False(embedder.IsReady);
        Assert.Equal(768, embedder.Dimensions);
        var index = sp.GetRequiredService<IVectorIndex>();
        Assert.Same(index, sp.GetRequiredService<IVectorIndex>());
        Assert.Equal(768, index.Dimensions);

        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddTesseraSearch(
            new SearchOptions { ModelsDir = Path.GetTempPath(), ModelId = "some-other-model" }));
    }

    [Fact]
    public void Throughput()
    {
        var m = Model();
        if (m is null) return;
        var rng = new Random(21);
        var bitmaps = new List<SKBitmap>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var bmp = new SKBitmap(1600, 1200, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
                using var paint = new SKPaint { Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)) };
                canvas.DrawCircle(rng.Next(1600), rng.Next(1200), rng.Next(100, 500), paint);
                bitmaps.Add(bmp);
            }

            var numbers = new Dictionary<string, object> { ["cpu_threads"] = Environment.GetEnvironmentVariable("TESSERA_CPU_THREADS") ?? $"default ({Environment.ProcessorCount} cores)" };
            foreach (var (name, model) in new[] { ("cpu", _models.Cpu), ("gpu", _models.Gpu) })
            {
                if (model is null) continue;
                string device = model.Device + (model.DeviceName is null ? "" : $" ({model.DeviceName})");
                model.EmbedImages(bitmaps); // warm up
                foreach (int batch in new[] { 1, 8 })
                {
                    var sw = Stopwatch.StartNew();
                    int done = 0;
                    double budget = name == "gpu" ? 4.0 : 6.0; // the GPU belongs to someone else
                    while (sw.Elapsed.TotalSeconds < budget)
                    {
                        if (batch == 1) model.EmbedImage(bitmaps[done % 8]);
                        else model.EmbedImages(bitmaps);
                        done += batch;
                    }
                    double ips = done / sw.Elapsed.TotalSeconds;
                    numbers[$"{name}_images_per_s_batch{batch}"] = Math.Round(ips, 2);
                    _out.WriteLine($"{device}: batch {batch}: {ips:F2} img/s ({done} images, 1600x1200 sources)");
                }
                var pre = Stopwatch.StartNew();
                var buffer = new float[ImagePreprocessor.InputLength(256)];
                for (int i = 0; i < 40; i++) ImagePreprocessor.Fill(bitmaps[i % 8], 256, buffer);
                numbers[$"{name}_preprocess_ms_1600x1200"] = Math.Round(pre.Elapsed.TotalMilliseconds / 40, 2);

                model.EmbedText("warm up");
                var sw2 = Stopwatch.StartNew();
                int n = 0;
                while (sw2.Elapsed.TotalSeconds < 2.0) { model.EmbedText($"a photo of thing number {n}"); n++; }
                double ms = sw2.Elapsed.TotalMilliseconds / n;
                numbers[$"{name}_text_ms"] = Math.Round(ms, 2);
                numbers[$"{name}_device"] = device;
                _out.WriteLine($"{device}: text embed {ms:F1} ms per sentence");
            }
            numbers["model_download_mb"] = Math.Round(ModelManifest.Embedded().TotalBytes / 1e6, 1);
            TestPaths.WriteNumbers("embedder-throughput", numbers);
        }
        finally
        {
            foreach (var b in bitmaps) b.Dispose();
        }
    }

    [Fact]
    public void QualitativeSearchOverRealImages()
    {
        var m = Model();
        if (m is null) return;
        string? dir = TestPaths.QualitativeImages;
        if (dir is null) { _out.WriteLine("skipped: no folder of non-personal images (TESSERA_QUALITATIVE_DIR)"); return; }

        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        int step = Math.Max(1, files.Count / 200);
        files = files.Where((_, i) => i % step == 0).Take(200).ToList();
        if (files.Count < 20) { _out.WriteLine($"skipped: only {files.Count} images in {dir}"); return; }

        var model = _models.Gpu ?? m;
        using var index = new BruteForceVectorIndex(768);
        var names = new Dictionary<long, string>();
        var sw = Stopwatch.StartNew();
        int embedded = 0;
        const int batch = 8;
        for (int i = 0; i < files.Count; i += batch)
        {
            var bitmaps = new List<(long Id, SKBitmap Bmp)>();
            foreach (string f in files.Skip(i).Take(batch))
            {
                var bmp = SKBitmap.Decode(f);
                if (bmp is null) continue;
                long id = names.Count + 1;
                names[id] = Path.GetFileName(f);
                bitmaps.Add((id, bmp));
            }
            float[][] vectors = model.EmbedImages(bitmaps.Select(b => b.Bmp).ToList());
            for (int j = 0; j < bitmaps.Count; j++) { index.Upsert(bitmaps[j].Id, vectors[j]); bitmaps[j].Bmp.Dispose(); }
            embedded += bitmaps.Count;
        }
        double embedSeconds = sw.Elapsed.TotalSeconds;

        string[] queries =
        {
            "a terminal window with code",
            "a file explorer window listing folders",
            "a web browser showing a video",
            "a spreadsheet with numbers",
            "a chat conversation",
            "an error dialog box",
            "a code editor with a dark theme",
            "a settings or preferences dialog",
            "a download or progress bar",
            "a photo of a mountain lake at sunrise",
        };
        var md = new StringBuilder();
        md.AppendLine($"Images: {embedded} from `{Path.GetFileName(dir)}` (decode + embed on {model.Device}: {embedSeconds:F1} s, {embedded / embedSeconds:F1} img/s including decode).");
        md.AppendLine();
        md.AppendLine("| Query | 1st | 2nd | 3rd |");
        md.AppendLine("|---|---|---|---|");
        foreach (string q in queries)
        {
            var hits = index.Search(model.EmbedText(q), 3);
            md.AppendLine($"| {q} | " + string.Join(" | ", hits.Select(h => $"{Escape(names[h.Id])} ({h.Score:F2})")) + " |");
        }
        _out.WriteLine(md.ToString());
        File.WriteAllText(Path.Combine(TestPaths.NumbersDir, "qualitative.md"), md.ToString());
        Assert.Equal(embedded, index.Count);

        static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    }
}
