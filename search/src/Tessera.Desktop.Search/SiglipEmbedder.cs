using System.Diagnostics;
using System.Numerics.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Tessera.Desktop.Search;

/// <summary>
/// SigLIP2 on ONNX Runtime: one session per tower, loaded once by
/// <see cref="EnsureReadyAsync"/>, then thread-safe embedding of bitmaps and
/// sentences into the same 768-d unit sphere.
/// </summary>
/// <remarks>
/// <para>
/// Device choice at load: DirectML on Windows when <see cref="SearchOptions.PreferGpu"/>
/// and a hardware adapter exists; the CPU provider otherwise, and as the
/// fallback when a GPU provider fails to initialise. The chosen one is in
/// <see cref="Device"/>.
/// </para>
/// <para>
/// On macOS with <see cref="SearchOptions.PreferGpu"/> the CPU sessions load
/// first (seconds) and the embedder is ready on them; CoreML compiles and
/// loads in the background (minutes) and is swapped in when it is ready:
/// calls already running finish on the CPU sessions, later calls use CoreML,
/// <see cref="Device"/> turns to <see cref="EmbedDevice.CoreML"/>, and the CPU
/// sessions are disposed when the last call using them returns. If CoreML
/// fails the embedder stays on the CPU. <see cref="DeviceUpgrade"/> completes
/// when that background load is over. The two sides give the same vectors to
/// cosine 0.9999 (search/README.md), so an index mixes them safely.
/// </para>
/// <para>
/// Core ML compiles a model for fixed shapes only, so on CoreML the vision
/// session takes exactly <see cref="CoreMlImageBatch"/> images and the text
/// session one sentence: short batches are padded and the padding's outputs
/// dropped, longer ones run in chunks. The compiled models are cached under
/// <c>&lt;model dir&gt;/coreml/</c>, keyed by file hash, batch and runtime
/// version. CPU and DirectML keep dynamic batches.
/// </para>
/// </remarks>
public sealed class SiglipEmbedder : IImageEmbedder, IDisposable
{
    /// <summary>The vision batch the CoreML session is compiled for: what the core's embedding worker hands over.</summary>
    public const int CoreMlImageBatch = 8;

    /// <summary>
    /// CoreML provider options, chosen on the macOS runner (search/README.md):
    /// ML Program, because the graphs store float16 and ONNX Runtime's
    /// NeuralNetwork builder takes float32 only; CPU and GPU, because the
    /// vision tower computes in float32, which the Neural Engine does not run.
    /// </summary>
    internal const string CoreMlFormat = "MLProgram";
    internal const string CoreMlComputeUnits = "CPUAndGPU";

    private readonly SearchOptions _options;
    private readonly ModelStore _store;
    private readonly ModelManifest _manifest;
    private readonly ILogger _log;
    private readonly object _loadGate = new();

    private Task? _loading;
    private GemmaTextTokenizer? _tokenizer;
    private volatile Sessions? _active;     // what embeds use now; null before load and after Dispose
    private Task _upgrade = Task.CompletedTask;
    private bool _disposed;

    public SiglipEmbedder(SearchOptions options, ModelStore store, ILogger<SiglipEmbedder>? log = null, ModelManifest? manifest = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? NullLogger<SiglipEmbedder>.Instance;
        _manifest = (manifest ?? ModelManifest.Embedded()).ForPlatform();
        if (!string.Equals(_manifest.ModelId, options.ModelId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"This build ships only '{_manifest.ModelId}'; SearchOptions.ModelId is '{options.ModelId}'.", nameof(options));
    }

    public string ModelId => _manifest.ModelId;
    public int Dimensions => _manifest.Dimensions;
    public int ImageSize => _manifest.ImageSize;
    public int TextLength => _manifest.Tokenizer.MaxLength;
    public bool IsReady => _active is not null;
    public EmbedDevice? Device => _active?.Device;

    /// <summary>The GPU adapter DirectML was asked for, when one was.</summary>
    public string? DeviceName => _active?.DeviceName;

    /// <summary>The manifest this embedder loads from, narrowed to this platform.</summary>
    public ModelManifest Manifest => _manifest;

    /// <summary>The model directory once loaded.</summary>
    public string? ModelDirectory { get; private set; }

    /// <summary>The batch the vision session was fixed to, or null when it takes any.</summary>
    public int? FixedImageBatch => _active is { VisionBatch: > 0 } s ? s.VisionBatch : null;

    /// <summary>
    /// Completes when the background move to CoreML is over, whether it moved
    /// or stayed on the CPU; never faults. Already complete where there is no
    /// such move (Windows, Linux, <see cref="SearchOptions.PreferGpu"/> false).
    /// </summary>
    public Task DeviceUpgrade => _upgrade;

    /// <summary>From ready on the CPU to CoreML in use, once that has happened.</summary>
    public TimeSpan? DeviceUpgradeTime { get; private set; }

    /// <summary>
    /// Fixes the batch dimensions on the CPU provider too, as CoreML needs
    /// them. Lets the padding path be tested on any machine.
    /// </summary>
    internal bool FixedShapesOnCpu { get; init; }

    /// <summary>
    /// Runs the background move with a second set of CPU sessions (batches
    /// fixed) instead of CoreML, so the swap can be tested on any machine.
    /// </summary>
    internal bool UpgradeOnCpuForTests { get; init; }

    public Task EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (IsReady) return Task.CompletedTask;
        Task loading;
        lock (_loadGate)
        {
            // A failed attempt is forgotten so the next caller retries.
            if (_loading is null || _loading.IsFaulted || _loading.IsCanceled)
                _loading = LoadAsync(progress, ct);
            loading = _loading;
        }
        return loading.WaitAsync(ct);
    }

    private async Task LoadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        string dir = await _store.EnsureAsync(_manifest, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => LoadSessions(dir, verified: true), ct).ConfigureAwait(false);
    }

    /// <summary>Loads the sessions from a directory that already holds verified files. Used by tests with a local export.</summary>
    public void LoadFromDirectory(string dir)
    {
        lock (_loadGate)
        {
            LoadSessions(dir, verified: false);
            _loading = Task.CompletedTask;
        }
    }

    /// <param name="verified">
    /// True when <see cref="ModelStore"/> checked the files against the
    /// manifest, so the manifest's hashes name them (the CoreML cache key).
    /// </param>
    private void LoadSessions(string dir, bool verified)
    {
        if (IsReady) return;
        string visionPath = Path.Combine(dir, "vision.onnx");
        string textPath = Path.Combine(dir, "text.onnx");
        string tokenizerPath = Path.Combine(dir, "tokenizer.model");
        foreach (string p in new[] { visionPath, textPath, tokenizerPath })
            if (!File.Exists(p)) throw new FileNotFoundException("Model file missing.", p);

        var tokenizer = GemmaTextTokenizer.FromFile(tokenizerPath, _manifest.Tokenizer);
        bool upgrade = UpgradeOnCpuForTests || (_options.PreferGpu && OperatingSystem.IsMacOS());
        Sessions s = Checked(upgrade ? CreateCpu(visionPath, textPath, FixedShapesOnCpu) : CreateSessions(visionPath, textPath));

        lock (_loadGate)
        {
            if (_disposed)
            {
                s.Release();
                throw new ObjectDisposedException(nameof(SiglipEmbedder));
            }
            _tokenizer = tokenizer;
            ModelDirectory = dir;
            _active = s;
            _log.LogInformation("SigLIP2 {Model} loaded on {Device}{Name}{Batch}{Next}", ModelId, s.Device,
                s.DeviceName is null ? "" : $" ({s.DeviceName})",
                s.VisionBatch > 0 ? $", batches fixed at {s.VisionBatch} images and {s.TextBatch} sentence" : "",
                upgrade ? "; CoreML loads in the background" : "");
            if (upgrade)
            {
                var ready = Stopwatch.StartNew();
                _upgrade = Task.Run(() => Upgrade(dir, visionPath, textPath, verified, ready));
            }
        }
    }

    /// <summary>The background half of a macOS load: CoreML, then the swap. Never throws.</summary>
    private void Upgrade(string dir, string visionPath, string textPath, bool verified, Stopwatch sinceReady)
    {
        Sessions next;
        try
        {
            next = Checked(UpgradeOnCpuForTests ? CreateCpu(visionPath, textPath, fixedShapes: true) : CreateCoreMl(dir, visionPath, textPath, verified));
        }
#pragma warning disable CA1031 // a background load that fails for any reason leaves the CPU in use, and says why
        catch (Exception e)
#pragma warning restore CA1031
        {
            _log.LogWarning(e, "CoreML failed to initialise; staying on the CPU provider");
            return;
        }

        Sessions? old;
        lock (_loadGate)
        {
            if (_disposed)
            {
                next.Release();
                return;
            }
            old = _active;
            _active = next;
            DeviceUpgradeTime = sinceReady.Elapsed;
        }
        old?.Release();   // disposed when the calls still using it return
        _log.LogInformation("SigLIP2 {Model} moved to {Device} {Seconds:F1} s after it was ready on the CPU",
            ModelId, next.Device, sinceReady.Elapsed.TotalSeconds);
    }

    /// <summary>Reads the sessions' input and output names and checks them; disposes the set and throws when they are wrong.</summary>
    private Sessions Checked(Sessions s)
    {
        try
        {
            NodeMetadata vin = s.Vision.InputMetadata.Values.First();
            NodeMetadata tin = s.Text.InputMetadata.Values.First();
            s.VisionInput = s.Vision.InputMetadata.Keys.First();
            s.TextInput = s.Text.InputMetadata.Keys.First();
            s.VisionOutput = s.Vision.OutputMetadata.Keys.First();
            s.TextOutput = s.Text.OutputMetadata.Keys.First();
            s.VisionWantsHalf = vin.ElementType == typeof(Half) || vin.ElementType == typeof(Float16);
            if (tin.ElementType != typeof(long))
                throw new InvalidDataException($"text.onnx input is {tin.ElementType.Name}, expected int64.");
            int outDims = (int)s.Vision.OutputMetadata.Values.First().Dimensions.Last();
            if (outDims > 0 && outDims != Dimensions)
                throw new InvalidDataException($"vision.onnx yields {outDims} dimensions, manifest says {Dimensions}.");
            return s;
        }
        catch
        {
            s.Release();
            throw;
        }
    }

    /// <summary>
    /// Both sessions and how they were opened (a batch of 0 takes any batch),
    /// counted: the embedder holds one reference while the set is active and
    /// every embed call one more, so a set swapped out is disposed when its
    /// last call returns.
    /// </summary>
    private sealed class Sessions(InferenceSession vision, InferenceSession text, EmbedDevice device, string? deviceName, int visionBatch, int textBatch)
    {
        private int _uses = 1;

        public InferenceSession Vision { get; } = vision;
        public InferenceSession Text { get; } = text;
        public EmbedDevice Device { get; } = device;
        public string? DeviceName { get; } = deviceName;
        public int VisionBatch { get; } = visionBatch;
        public int TextBatch { get; } = textBatch;
        public string VisionInput { get; set; } = "pixel_values";
        public string VisionOutput { get; set; } = "image_embeds";
        public string TextInput { get; set; } = "input_ids";
        public string TextOutput { get; set; } = "text_embeds";
        public bool VisionWantsHalf { get; set; }

        /// <summary>False once the set has been released by everyone.</summary>
        public bool TryAcquire()
        {
            int n;
            do
            {
                n = Volatile.Read(ref _uses);
                if (n == 0) return false;
            }
            while (Interlocked.CompareExchange(ref _uses, n + 1, n) != n);
            return true;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _uses) != 0) return;
            Vision.Dispose();
            Text.Dispose();
        }
    }

    /// <summary>The active set, counted for the caller, who must <see cref="Sessions.Release"/> it.</summary>
    private Sessions Acquire()
    {
        while (true)
        {
            Sessions s = _active ?? throw NotReady();
            if (s.TryAcquire()) return s;
            // Swapped out and released between the read and the count: read again.
        }
    }

    /// <summary>DirectML on Windows when asked for and present, else the CPU.</summary>
    private Sessions CreateSessions(string visionPath, string textPath)
    {
        if (_options.PreferGpu && OperatingSystem.IsWindows())
        {
            DirectMLDevices.Adapter? adapter = DirectMLDevices.BestHardwareAdapter();
            if (adapter is not null)
            {
                try
                {
                    using SessionOptions so = BaseOptions();
                    // DirectML needs the whole graph on one device and no
                    // parallel executor.
                    so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    so.EnableMemoryPattern = false;
                    so.AppendExecutionProvider_DML(adapter.Index);
                    var vision = new InferenceSession(visionPath, so);
                    try
                    {
                        var text = new InferenceSession(textPath, so);
                        return new Sessions(vision, text, EmbedDevice.DirectML, adapter.Name, 0, 0);
                    }
                    catch
                    {
                        vision.Dispose();
                        throw;
                    }
                }
                catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
                {
                    _log.LogWarning(e, "DirectML on {Adapter} failed to initialise; using the CPU provider", adapter.Name);
                }
            }
            else
            {
                _log.LogInformation("No hardware GPU adapter; using the CPU provider");
            }
        }

        return CreateCpu(visionPath, textPath, FixedShapesOnCpu);
    }

    private Sessions CreateCpu(string visionPath, string textPath, bool fixedShapes)
    {
        int visionBatch = fixedShapes ? CoreMlImageBatch : 0;
        int textBatch = fixedShapes ? 1 : 0;
        using SessionOptions cpuVision = BaseOptions();
        using SessionOptions cpuText = BaseOptions();
        if (fixedShapes)
        {
            cpuVision.AddFreeDimensionOverrideByName(_manifest.Inputs.Vision.BatchDimension, visionBatch);
            cpuText.AddFreeDimensionOverrideByName(_manifest.Inputs.Text.BatchDimension, textBatch);
        }
        var v = new InferenceSession(visionPath, cpuVision);
        try
        {
            var t = new InferenceSession(textPath, cpuText);
            return new Sessions(v, t, EmbedDevice.Cpu, null, visionBatch, textBatch);
        }
        catch
        {
            v.Dispose();
            throw;
        }
    }

    private Sessions CreateCoreMl(string dir, string visionPath, string textPath, bool verified)
    {
        var vision = CoreMlSession(dir, "vision", visionPath, _manifest.Inputs.Vision.BatchDimension, CoreMlImageBatch, verified);
        try
        {
            var text = CoreMlSession(dir, "text", textPath, _manifest.Inputs.Text.BatchDimension, 1, verified);
            return new Sessions(vision, text, EmbedDevice.CoreML, null, CoreMlImageBatch, 1);
        }
        catch
        {
            vision.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One tower on CoreML with its batch dimension fixed. The compiled model
    /// is cached in its own folder; a cache that fails to load (a crash while
    /// it was written) is deleted and compiled again once.
    /// </summary>
    private InferenceSession CoreMlSession(string dir, string tower, string path, string batchDimension, int batch, bool verified)
    {
        string cache = CoreMlCache(dir, tower, path, batch, verified);
        bool warm = Directory.Exists(cache);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InferenceSession session;
        try
        {
            session = Open();
        }
        catch (OnnxRuntimeException e) when (warm)
        {
            _log.LogWarning(e, "The CoreML cache in {Cache} did not load; compiling again", cache);
            TryDeleteDirectory(cache);
            warm = false;
            session = Open();
        }

        _log.LogInformation("CoreML {Tower} session ({Batch}) loaded in {Seconds:F1} s from {Source}",
            tower, batch, sw.Elapsed.TotalSeconds, warm ? "the cache" : "a fresh compile");
        return session;

        InferenceSession Open()
        {
            using SessionOptions so = BaseOptions();
            so.AddFreeDimensionOverrideByName(batchDimension, batch);
            so.AppendExecutionProvider("CoreML", new Dictionary<string, string>
            {
                ["ModelFormat"] = CoreMlFormat,
                ["MLComputeUnits"] = CoreMlComputeUnits,
                ["ModelCacheDirectory"] = cache,
            });
            var s = new InferenceSession(path, so);
            long first = s.InputMetadata.Values.First().Dimensions[0];
            if (first > 0 && first != batch)
            {
                s.Dispose();
                throw new InvalidDataException($"{Path.GetFileName(path)}'s batch dimension is {first}, expected '{batchDimension}' fixed at {batch}.");
            }
            return s;
        }
    }

    /// <summary>
    /// <c>&lt;dir&gt;/coreml/&lt;tower&gt;-&lt;sha256&gt;-b&lt;batch&gt;-ort&lt;version&gt;-&lt;format&gt;-&lt;units&gt;</c>.
    /// ONNX Runtime keys its cache by a hash of the model's path alone and
    /// reuses whatever it finds there, so everything else that changes the
    /// compiled model is in the folder name, and other folders for the same
    /// tower are removed.
    /// </summary>
    private string CoreMlCache(string dir, string tower, string path, int batch, bool verified)
    {
        string file = (verified ? _manifest.File(Path.GetFileName(path)).Sha256 : Sha256Of(path))[..16];
        string key = $"{tower}-{file}-b{batch}-ort{OrtEnv.Instance().GetVersionString()}-{CoreMlFormat}-{CoreMlComputeUnits}";
        string root = Path.Combine(dir, "coreml");
        if (Directory.Exists(root))
        {
            foreach (string stale in Directory.EnumerateDirectories(root, tower + "-*"))
                if (!string.Equals(Path.GetFileName(stale), key, StringComparison.Ordinal)) TryDeleteDirectory(stale);
        }
        return Path.Combine(root, key);
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not remove {Path}", path);
        }
    }

    private SessionOptions BaseOptions()
    {
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (_options.CpuThreads > 0) so.IntraOpNumThreads = _options.CpuThreads;
        return so;
    }

    // ------------------------------------------------------------------ embed

    public float[] EmbedImage(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return EmbedImages(new[] { bitmap })[0];
    }

    /// <summary>
    /// Embeds several bitmaps in one run. Batching matters on DirectML and
    /// CoreML and helps a little on CPU; the core's worker hands over eight.
    /// On a fixed batch (CoreML) every run has exactly that many images: the
    /// tail is padded with zeros (mid-grey) and the padding's outputs dropped.
    /// </summary>
    public float[][] EmbedImages(IReadOnlyList<SKBitmap> bitmaps)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        int n = bitmaps.Count;
        Sessions s = Acquire();
        try
        {
            if (n == 0) return Array.Empty<float[]>();
            int size = ImageSize;
            int per = ImagePreprocessor.InputLength(size);
            int batch = s.VisionBatch > 0 ? s.VisionBatch : n;
            var result = new float[n][];
            var pixels = new float[batch * per];
            long[] shape = { batch, 3, size, size };
            for (int start = 0; start < n; start += batch)
            {
                int count = Math.Min(batch, n - start);
                for (int i = 0; i < count; i++) ImagePreprocessor.Fill(bitmaps[start + i], size, pixels.AsSpan(i * per, per));
                pixels.AsSpan(count * per).Clear();
                using OrtValue input = s.VisionWantsHalf
                    ? OrtValue.CreateTensorValueFromMemory(ToHalf(pixels), shape)
                    : OrtValue.CreateTensorValueFromMemory(pixels, shape);
                Run(s.Vision, s.VisionInput, input, s.VisionOutput, count).CopyTo(result, start);
            }
            return result;
        }
        finally
        {
            s.Release();
        }
    }

    public float[] EmbedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text to embed must not be empty.", nameof(text));
        return EmbedTexts(new[] { text })[0];
    }

    /// <summary>Embeds several sentences in one run, or one run each on CoreML (batch fixed at 1).</summary>
    public float[][] EmbedTexts(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        int n = texts.Count;
        Sessions s = Acquire();
        try
        {
            GemmaTextTokenizer tokenizer = _tokenizer!;
            if (n == 0) return Array.Empty<float[]>();
            for (int i = 0; i < n; i++)
            {
                if (string.IsNullOrWhiteSpace(texts[i]))
                    throw new ArgumentException($"Text {i} to embed must not be empty.", nameof(texts));
            }
            int len = tokenizer.MaxLength;
            int batch = s.TextBatch > 0 ? s.TextBatch : n;
            var result = new float[n][];
            var ids = new long[batch * len];
            for (int start = 0; start < n; start += batch)
            {
                int count = Math.Min(batch, n - start);
                for (int i = 0; i < count; i++) tokenizer.Encode(texts[start + i], ids.AsSpan(i * len, len));
                ids.AsSpan(count * len).Clear();
                using OrtValue input = OrtValue.CreateTensorValueFromMemory(ids, new long[] { batch, len });
                Run(s.Text, s.TextInput, input, s.TextOutput, count).CopyTo(result, start);
            }
            return result;
        }
        finally
        {
            s.Release();
        }
    }

    /// <summary>Token ids for a sentence, as the text tower sees them. For tests and diagnostics.</summary>
    public long[] Tokenize(string text) => (_tokenizer ?? throw NotReady()).Encode(text);

    private float[][] Run(InferenceSession session, string inputName, OrtValue input, string outputName, int n)
    {
        using var runOptions = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(
            runOptions, new[] { inputName }, new[] { input }, new[] { outputName });
        OrtValue output = outputs[0];
        var result = new float[n][];
        int dims = Dimensions;
        Type elem = output.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16 ? typeof(Half) : typeof(float);
        if (elem == typeof(Half))
        {
            ReadOnlySpan<Half> data = output.GetTensorDataAsSpan<Half>();
            for (int i = 0; i < n; i++)
            {
                var row = new float[dims];
                ReadOnlySpan<Half> src = data.Slice(i * dims, dims);
                for (int j = 0; j < dims; j++) row[j] = (float)src[j];
                Normalise(row);
                result[i] = row;
            }
        }
        else
        {
            ReadOnlySpan<float> data = output.GetTensorDataAsSpan<float>();
            for (int i = 0; i < n; i++)
            {
                var row = data.Slice(i * dims, dims).ToArray();
                Normalise(row);
                result[i] = row;
            }
        }
        return result;
    }

    /// <summary>The graph normalises already; this only tidies float16 rounding.</summary>
    private static void Normalise(float[] v)
    {
        float norm = TensorPrimitives.Norm(v);
        if (norm > 0 && Math.Abs(norm - 1f) > 1e-6f) TensorPrimitives.Divide(v, norm, v);
    }

    private static Half[] ToHalf(float[] values)
    {
        var h = new Half[values.Length];
        for (int i = 0; i < values.Length; i++) h[i] = (Half)values[i];
        return h;
    }

    private static InvalidOperationException NotReady()
        => new("The model is not loaded. Await EnsureReadyAsync first.");

    /// <summary>
    /// Releases the sessions; calls still running finish first. A CoreML load
    /// still running in the background disposes what it made when it ends.
    /// </summary>
    public void Dispose()
    {
        Sessions? s;
        lock (_loadGate)
        {
            if (_disposed) return;
            _disposed = true;
            s = _active;
            _active = null;
        }
        s?.Release();
    }
}
