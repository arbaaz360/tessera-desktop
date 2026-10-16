using System.Security.Cryptography;

namespace Tessera.Desktop.Search.Tests;

public class ModelStoreTests : IDisposable
{
    private readonly string _serverRoot;
    private readonly string _modelsDir;
    private readonly LocalHttpServer _server;
    private readonly ModelManifest _manifest;

    public ModelStoreTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "tessera-search-tests", Guid.NewGuid().ToString("N"));
        _serverRoot = Path.Combine(root, "server");
        _modelsDir = Path.Combine(root, "models");
        Directory.CreateDirectory(_serverRoot);
        _server = new LocalHttpServer(_serverRoot);

        var rng = new Random(11);
        var files = new List<ModelFile>();
        foreach ((string name, int size) in new[] { ("vision.onnx", 3_000_000), ("text.onnx", 5_000_000 + 12345), ("tokenizer.model", 40_000) })
        {
            var data = new byte[size];
            rng.NextBytes(data);
            File.WriteAllBytes(Path.Combine(_serverRoot, name), data);
            files.Add(new ModelFile
            {
                Name = name,
                Bytes = size,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
                Url = _server.BaseUrl + name,
            });
        }
        _manifest = new ModelManifest { ModelId = "test-model", Version = 1, Dimensions = 8, Files = files };
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_serverRoot)!, recursive: true); } catch (IOException) { }
    }

    private ModelStore NewStore() => new(new SearchOptions { ModelsDir = _modelsDir, ModelId = "test-model" });

    [Fact]
    public async Task DownloadsMissingFilesVerifiesAndReportsProgress()
    {
        using var store = NewStore();
        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => { lock (reports) reports.Add(p); });

        string dir = await store.EnsureAsync(_manifest, progress, CancellationToken.None);

        Assert.Equal(Path.Combine(_modelsDir, "test-model"), dir);
        foreach (var f in _manifest.Files)
        {
            Assert.Equal(f.Bytes, new FileInfo(Path.Combine(dir, f.Name)).Length);
            Assert.False(File.Exists(Path.Combine(dir, f.Name + ".part")));
        }
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
        Assert.Equal(_manifest.ModelId, ModelManifest.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).ModelId);
        Assert.True(await store.IsCompleteAsync(_manifest, CancellationToken.None));

        await Task.Delay(50); // Progress<T> posts asynchronously
        lock (reports)
        {
            Assert.Contains(reports, r => r.File == "text.onnx" && r.ReceivedBytes == 5_012_345 && r.TotalBytes == 5_012_345);
            Assert.Contains(reports, r => r.File == "vision.onnx" && r.ReceivedBytes == 3_000_000);
        }
    }

    [Fact]
    public async Task ValidFilesAreNotFetchedAgain()
    {
        using var store = NewStore();
        await store.EnsureAsync(_manifest, null, CancellationToken.None);
        int requests = _server.Requests.Count;
        await store.EnsureAsync(_manifest, null, CancellationToken.None);
        Assert.Equal(requests, _server.Requests.Count);
    }

    [Fact]
    public async Task CorruptLocalFileIsReplaced()
    {
        using var store = NewStore();
        await store.EnsureAsync(_manifest, null, CancellationToken.None);
        string path = Path.Combine(_modelsDir, "test-model", "tokenizer.model");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[100] ^= 0xFF; // same size, wrong hash
        File.WriteAllBytes(path, bytes);
        _server.Requests.Clear();

        await store.EnsureAsync(_manifest, null, CancellationToken.None);

        Assert.Equal(new[] { "tokenizer.model" }, _server.Requests.Select(r => r.Path));
        Assert.Equal(_manifest.File("tokenizer.model").Sha256, await ModelStore.Sha256Async(path, CancellationToken.None));
    }

    [Fact]
    public async Task PartialDownloadIsResumedWithRange()
    {
        using var store = NewStore();
        string dir = Path.Combine(_modelsDir, "test-model");
        Directory.CreateDirectory(dir);
        // A .part holding the first 1,000,000 correct bytes of vision.onnx
        byte[] full = File.ReadAllBytes(Path.Combine(_serverRoot, "vision.onnx"));
        File.WriteAllBytes(Path.Combine(dir, "vision.onnx.part"), full[..1_000_000]);

        var reports = new List<ModelDownloadProgress>();
        await store.EnsureAsync(_manifest, new Progress<ModelDownloadProgress>(p => { lock (reports) reports.Add(p); }), CancellationToken.None);

        var vision = _server.Requests.Single(r => r.Path == "vision.onnx");
        Assert.Equal("bytes=1000000-", vision.Range);
        Assert.True(await store.IsCompleteAsync(_manifest, CancellationToken.None));
        await Task.Delay(50);
        lock (reports) Assert.Contains(reports, r => r.File == "vision.onnx" && r.ReceivedBytes == 1_000_000 && r.TotalBytes == 3_000_000);
    }

    [Fact]
    public async Task DroppedConnectionIsRetriedFromWhereItStopped()
    {
        using var store = NewStore();
        _server.FailAfterBytes = 1_500_000;
        var ensure = store.EnsureAsync(_manifest, null, CancellationToken.None);
        // After the first failed request, let the rest through.
        while (_server.Requests.Count == 0) await Task.Delay(10);
        await Task.Delay(200);
        _server.FailAfterBytes = null;
        await ensure;

        Assert.True(await store.IsCompleteAsync(_manifest, CancellationToken.None));
        Assert.Contains(_server.Requests, r => r.Range is not null && r.Range.StartsWith("bytes=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongBytesFromServerFailAndLeaveNoPart()
    {
        using var store = NewStore();
        _server.Corrupt = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAsync(_manifest, null, CancellationToken.None));
        string dir = Path.Combine(_modelsDir, "test-model");
        Assert.False(File.Exists(Path.Combine(dir, "vision.onnx")));
        Assert.False(File.Exists(Path.Combine(dir, "vision.onnx.part")));
        Assert.False(await store.IsCompleteAsync(_manifest, CancellationToken.None));
    }

    [Fact]
    public async Task MissingOnServerIsAnError()
    {
        using var store = NewStore();
        var manifest = new ModelManifest
        {
            ModelId = "test-model",
            Files = new[] { new ModelFile { Name = "nope.onnx", Bytes = 10, Sha256 = new string('0', 64), Url = _server.BaseUrl + "nope.onnx" } },
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => store.EnsureAsync(manifest, null, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneDownload()
    {
        using var store = NewStore();
        var tasks = Enumerable.Range(0, 4).Select(_ => store.EnsureAsync(_manifest, null, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(3, _server.Requests.Count); // one request per file, not four
    }

    [Fact]
    public async Task CancellationStopsTheDownload()
    {
        using var store = NewStore();
        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new Progress<ModelDownloadProgress>(_ => { if (Interlocked.Increment(ref reports) == 1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAsync(_manifest, progress, cts.Token));
    }

    /// <summary>
    /// The real thing: downloads the published release into a temp dir and
    /// verifies it, the way a buyer's first run does (the model repository
    /// is public, so no token). Runs only with TESSERA_REAL_DOWNLOAD=1,
    /// because it moves the whole model.
    /// </summary>
    [Fact]
    public async Task RealReleaseDownloadsAndVerifies()
    {
        if (Environment.GetEnvironmentVariable("TESSERA_REAL_DOWNLOAD") != "1")
            return;
        var manifest = ModelManifest.Embedded();
        string dir = Path.Combine(Path.GetTempPath(), "tessera-real-download");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        using var store = new ModelStore(new SearchOptions { ModelsDir = dir });
        long last = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ModelDownloadProgress>(p => Interlocked.Exchange(ref last, p.ReceivedBytes));
        string modelDir = await store.EnsureAsync(manifest, progress, CancellationToken.None);
        double seconds = sw.Elapsed.TotalSeconds;
        Assert.True(await store.IsCompleteAsync(manifest, CancellationToken.None));
        TestPaths.WriteNumbers("real-download", new
        {
            bytes = manifest.TotalBytes,
            seconds = Math.Round(seconds, 1),
            mbit_per_s = Math.Round(manifest.TotalBytes * 8 / 1e6 / seconds, 1),
            dir = modelDir,
        });
    }

    [Fact]
    public void EmbeddedManifestIsTheShippedOne()
    {
        var published = ModelManifest.EmbeddedAsPublished();
        // v2 changed only the text tower, v3 only the vision tower's padding and
        // arithmetic, and v4 lists a vision tower per platform; image vectors
        // agree to 0.99999 throughout, so stored indexes stay valid under one id.
        Assert.Equal("siglip2-base-patch16-256", published.ModelId);
        Assert.Equal(4, published.Version);
        Assert.Null(published.Platform);
        // CoreML fixes the batch dimension by the name the graphs give it.
        Assert.Equal("batch", published.Inputs.Vision.BatchDimension);
        Assert.Equal("batch", published.Inputs.Text.BatchDimension);
        Assert.Equal("pixel_values", published.Inputs.Vision.Name);
        Assert.Equal("input_ids", published.Inputs.Text.Name);
        Assert.Equal(768, published.Dimensions);
        Assert.Equal(256, published.ImageSize);
        Assert.Equal(64, published.Tokenizer.MaxLength);
        Assert.Equal(new[] { "vision.onnx", "vision.onnx", "text.onnx", "tokenizer.model" }, published.Files.Select(f => f.Name));
        Assert.All(published.Files, f =>
        {
            Assert.Equal(64, f.Sha256.Length);
            Assert.True(f.Bytes > 0);
            // The public, model-only repository: a buyer downloads with no token.
            Assert.Matches($"^https://github\\.com/arbaaz360/siglip2-base-onnx/releases/download/models-siglip2-base-v[23]/{f.Name}$", f.Url);
        });

        // Windows keeps v2's vision tower (float16 arithmetic, fast on DirectML);
        // macOS takes v3's (float32 arithmetic, which Core ML needs).
        var windows = published.ForPlatform("windows");
        var macos = published.ForPlatform("macos");
        Assert.Equal("ae478837056f3038d6793d4eb6450f4eacc8fc3b38711a4cd1d899af3f4747c7", windows.File("vision.onnx").Sha256);
        Assert.EndsWith("/models-siglip2-base-v2/vision.onnx", windows.File("vision.onnx").Url, StringComparison.Ordinal);
        Assert.Equal("25c1825ef9c20d4f3ab5d20910afacf96f40496827264c28385f45d82ce00e60", macos.File("vision.onnx").Sha256);
        Assert.Equal(windows.File("vision.onnx").Sha256, published.ForPlatform("linux").File("vision.onnx").Sha256);
        Assert.Equal(windows.File("text.onnx").Sha256, macos.File("text.onnx").Sha256);
        foreach (var m in new[] { windows, macos })
        {
            Assert.Equal(new[] { "vision.onnx", "text.onnx", "tokenizer.model" }, m.Files.Select(f => f.Name));
            // v1 was 755 MB; the int8 token table took 196 MB off the text tower.
            Assert.InRange(m.TotalBytes, 500_000_000, 600_000_000);
        }
        Assert.Equal(ModelManifest.CurrentPlatform, ModelManifest.Embedded().Platform);
    }

    [Fact]
    public void ManifestsWithoutPlatformsServeEveryPlatform()
    {
        Assert.All(new[] { "windows", "macos", "linux" }, p =>
            Assert.Equal(_manifest.Files.Select(f => f.Sha256), _manifest.ForPlatform(p).Files.Select(f => f.Sha256)));
        var twice = new ModelManifest
        {
            ModelId = "test-model",
            Files = [.. _manifest.Files, new ModelFile { Name = "vision.onnx", Sha256 = new string('0', 64), Url = "x", Platforms = ["macos"] }],
        };
        Assert.Throws<InvalidDataException>(() => twice.ForPlatform("macos"));
        Assert.Equal(3, twice.ForPlatform("windows").Files.Count);
    }

    /// <summary>A manifest listing vision.onnx per platform: the store fetches this platform's and only that.</summary>
    [Fact]
    public async Task DownloadsOnlyThisPlatformsEntry()
    {
        var other = new byte[1000];
        new Random(5).NextBytes(other);
        File.WriteAllBytes(Path.Combine(_serverRoot, "vision-other.onnx"), other);
        string here = ModelManifest.CurrentPlatform;
        string elsewhere = here == "macos" ? "windows" : "macos";
        var mine = _manifest.File("vision.onnx");
        var manifest = new ModelManifest
        {
            ModelId = "test-model",
            Files =
            [
                new ModelFile { Name = "vision.onnx", Bytes = other.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(other)), Url = _server.BaseUrl + "vision-other.onnx", Platforms = [elsewhere] },
                new ModelFile { Name = "vision.onnx", Bytes = mine.Bytes, Sha256 = mine.Sha256, Url = mine.Url, Platforms = [here] },
                _manifest.File("text.onnx"),
                _manifest.File("tokenizer.model"),
            ],
        };
        using var store = NewStore();
        string dir = await store.EnsureAsync(manifest, null, CancellationToken.None);
        Assert.Equal(mine.Sha256, await ModelStore.Sha256Async(Path.Combine(dir, "vision.onnx"), CancellationToken.None));
        Assert.DoesNotContain(_server.Requests, r => r.Path.Contains("vision-other", StringComparison.Ordinal));
        Assert.True(await store.IsCompleteAsync(manifest, CancellationToken.None));
        Assert.Equal(here, ModelManifest.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).Platform);
    }
}
