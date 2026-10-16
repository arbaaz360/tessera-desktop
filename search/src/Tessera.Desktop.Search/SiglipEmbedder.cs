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
/// and a hardware adapter exists; CoreML on macOS when <see cref="SearchOptions.PreferGpu"/>;
/// the CPU provider otherwise, and as the fallback when a GPU provider fails
/// to initialise. The chosen one is in <see cref="Device"/>.
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
    /// ML Program, because the graphs are float16 and ONNX Runtime's
    /// NeuralNetwork builder takes float32 only; CPU and GPU, because the
    /// Neural Engine could not be measured (GitHub's Mac runners are virtual
    /// machines without one).
    /// </summary>
    internal const string CoreMlFormat = "MLProgram";
    internal const string CoreMlComputeUnits = "CPUAndGPU";

    private readonly SearchOptions _options;
    private readonly ModelStore _store;
    private readonly ModelManifest _manifest;
    private readonly ILogger _log;
    private readonly object _loadGate = new();

    private Task? _loading;
    private InferenceSession? _vision;
    private InferenceSession? _text;
    private GemmaTextTokenizer? _tokenizer;
    private string _visionInput = "pixel_values";
    private string _textInput = "input_ids";
    private string _visionOutput = "image_embeds";
    private string _textOutput = "text_embeds";
    private bool _visionWantsHalf;
    private int _visionBatch;   // 0: any batch; otherwise the only batch the session takes
    private int _textBatch;
    private volatile bool _ready;

    public SiglipEmbedder(SearchOptions options, ModelStore store, ILogger<SiglipEmbedder>? log = null, ModelManifest? manifest = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? NullLogger<SiglipEmbedder>.Instance;
        _manifest = manifest ?? ModelManifest.Embedded();
        if (!string.Equals(_manifest.ModelId, options.ModelId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"This build ships only '{_manifest.ModelId}'; SearchOptions.ModelId is '{options.ModelId}'.", nameof(options));
    }

    public string ModelId => _manifest.ModelId;
    public int Dimensions => _manifest.Dimensions;
    public int ImageSize => _manifest.ImageSize;
    public int TextLength => _manifest.Tokenizer.MaxLength;
    public bool IsReady => _ready;
    public EmbedDevice? Device { get; private set; }

    /// <summary>The GPU adapter DirectML was asked for, when one was.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>The manifest this embedder loads from.</summary>
    public ModelManifest Manifest => _manifest;

    /// <summary>The model directory once loaded.</summary>
    public string? ModelDirectory { get; private set; }

    /// <summary>The batch the vision session was fixed to, or null when it takes any.</summary>
    public int? FixedImageBatch => _visionBatch > 0 ? _visionBatch : null;

    /// <summary>
    /// Fixes the batch dimensions on the CPU provider too, as CoreML needs
    /// them. Lets the padding path be tested on any machine.
    /// </summary>
    internal bool FixedShapesOnCpu { get; init; }

    public Task EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (_ready) return Task.CompletedTask;
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
        if (_ready) return;
        string visionPath = Path.Combine(dir, "vision.onnx");
        string textPath = Path.Combine(dir, "text.onnx");
        string tokenizerPath = Path.Combine(dir, "tokenizer.model");
        foreach (string p in new[] { visionPath, textPath, tokenizerPath })
            if (!File.Exists(p)) throw new FileNotFoundException("Model file missing.", p);

        var tokenizer = GemmaTextTokenizer.FromFile(tokenizerPath, _manifest.Tokenizer);

        Sessions s = CreateSessions(dir, visionPath, textPath, verified);
        try
        {
            NodeMetadata vin = s.Vision.InputMetadata.Values.First();
            NodeMetadata tin = s.Text.InputMetadata.Values.First();
            _visionInput = s.Vision.InputMetadata.Keys.First();
            _textInput = s.Text.InputMetadata.Keys.First();
            _visionOutput = s.Vision.OutputMetadata.Keys.First();
            _textOutput = s.Text.OutputMetadata.Keys.First();
            _visionWantsHalf = vin.ElementType == typeof(Half) || vin.ElementType == typeof(Float16);
            if (tin.ElementType != typeof(long))
                throw new InvalidDataException($"text.onnx input is {tin.ElementType.Name}, expected int64.");
            int outDims = (int)s.Vision.OutputMetadata.Values.First().Dimensions.Last();
            if (outDims > 0 && outDims != Dimensions)
                throw new InvalidDataException($"vision.onnx yields {outDims} dimensions, manifest says {Dimensions}.");
        }
        catch
        {
            s.Vision.Dispose();
            s.Text.Dispose();
            throw;
        }

        _vision = s.Vision;
        _text = s.Text;
        _visionBatch = s.VisionBatch;
        _textBatch = s.TextBatch;
        _tokenizer = tokenizer;
        Device = s.Device;
        DeviceName = s.DeviceName;
        ModelDirectory = dir;
        _ready = true;
        _log.LogInformation("SigLIP2 {Model} loaded on {Device}{Name}{Batch}", ModelId, s.Device,
            s.DeviceName is null ? "" : $" ({s.DeviceName})",
            s.VisionBatch > 0 ? $", batches fixed at {s.VisionBatch} images and {s.TextBatch} sentence" : "");
    }

    /// <summary>Both sessions and how they were opened. A batch of 0 takes any batch.</summary>
    private sealed record Sessions(InferenceSession Vision, InferenceSession Text, EmbedDevice Device, string? DeviceName, int VisionBatch, int TextBatch);

    private Sessions CreateSessions(string dir, string visionPath, string textPath, bool verified)
    {
        if (_options.PreferGpu)
        {
            if (OperatingSystem.IsWindows())
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
            else if (OperatingSystem.IsMacOS())
            {
                try
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
                catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidDataException)
                {
                    _log.LogWarning(e, "CoreML failed to initialise; using the CPU provider");
                }
            }
        }

        int visionBatch = FixedShapesOnCpu ? CoreMlImageBatch : 0;
        int textBatch = FixedShapesOnCpu ? 1 : 0;
        using SessionOptions cpuVision = BaseOptions();
        using SessionOptions cpuText = BaseOptions();
        if (FixedShapesOnCpu)
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
        InferenceSession session = Ready(_vision);
        int n = bitmaps.Count;
        if (n == 0) return Array.Empty<float[]>();
        int size = ImageSize;
        int per = ImagePreprocessor.InputLength(size);
        int batch = _visionBatch > 0 ? _visionBatch : n;
        var result = new float[n][];
        var pixels = new float[batch * per];
        long[] shape = { batch, 3, size, size };
        for (int start = 0; start < n; start += batch)
        {
            int count = Math.Min(batch, n - start);
            for (int i = 0; i < count; i++) ImagePreprocessor.Fill(bitmaps[start + i], size, pixels.AsSpan(i * per, per));
            pixels.AsSpan(count * per).Clear();
            using OrtValue input = _visionWantsHalf
                ? OrtValue.CreateTensorValueFromMemory(ToHalf(pixels), shape)
                : OrtValue.CreateTensorValueFromMemory(pixels, shape);
            Run(session, _visionInput, input, _visionOutput, count).CopyTo(result, start);
        }
        return result;
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
        InferenceSession session = Ready(_text);
        GemmaTextTokenizer tokenizer = _tokenizer!;
        int n = texts.Count;
        if (n == 0) return Array.Empty<float[]>();
        for (int i = 0; i < n; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
                throw new ArgumentException($"Text {i} to embed must not be empty.", nameof(texts));
        }
        int len = tokenizer.MaxLength;
        int batch = _textBatch > 0 ? _textBatch : n;
        var result = new float[n][];
        var ids = new long[batch * len];
        for (int start = 0; start < n; start += batch)
        {
            int count = Math.Min(batch, n - start);
            for (int i = 0; i < count; i++) tokenizer.Encode(texts[start + i], ids.AsSpan(i * len, len));
            ids.AsSpan(count * len).Clear();
            using OrtValue input = OrtValue.CreateTensorValueFromMemory(ids, new long[] { batch, len });
            Run(session, _textInput, input, _textOutput, count).CopyTo(result, start);
        }
        return result;
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

    private InferenceSession Ready(InferenceSession? session)
        => _ready && session is not null ? session : throw NotReady();

    private static InvalidOperationException NotReady()
        => new("The model is not loaded. Await EnsureReadyAsync first.");

    public void Dispose()
    {
        _ready = false;
        _vision?.Dispose();
        _text?.Dispose();
        _vision = null;
        _text = null;
    }
}
