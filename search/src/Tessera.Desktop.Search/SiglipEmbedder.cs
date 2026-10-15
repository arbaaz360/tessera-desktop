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
/// Device choice at load: DirectML on Windows when <see cref="SearchOptions.PreferGpu"/>
/// and a hardware adapter exists; CoreML on macOS when <see cref="SearchOptions.PreferGpu"/>;
/// the CPU provider otherwise, and as the fallback when a GPU provider fails
/// to initialise. The chosen one is in <see cref="Device"/>.
/// </remarks>
public sealed class SiglipEmbedder : IImageEmbedder, IDisposable
{
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
        await Task.Run(() => LoadSessions(dir), ct).ConfigureAwait(false);
    }

    /// <summary>Loads the sessions from a directory that already holds verified files. Used by tests with a local export.</summary>
    public void LoadFromDirectory(string dir)
    {
        lock (_loadGate)
        {
            LoadSessions(dir);
            _loading = Task.CompletedTask;
        }
    }

    private void LoadSessions(string dir)
    {
        if (_ready) return;
        string visionPath = Path.Combine(dir, "vision.onnx");
        string textPath = Path.Combine(dir, "text.onnx");
        string tokenizerPath = Path.Combine(dir, "tokenizer.model");
        foreach (string p in new[] { visionPath, textPath, tokenizerPath })
            if (!File.Exists(p)) throw new FileNotFoundException("Model file missing.", p);

        var tokenizer = GemmaTextTokenizer.FromFile(tokenizerPath, _manifest.Tokenizer);

        (InferenceSession vision, InferenceSession text, EmbedDevice device, string? deviceName) = CreateSessions(visionPath, textPath);
        try
        {
            NodeMetadata vin = vision.InputMetadata.Values.First();
            NodeMetadata tin = text.InputMetadata.Values.First();
            _visionInput = vision.InputMetadata.Keys.First();
            _textInput = text.InputMetadata.Keys.First();
            _visionOutput = vision.OutputMetadata.Keys.First();
            _textOutput = text.OutputMetadata.Keys.First();
            _visionWantsHalf = vin.ElementType == typeof(Half) || vin.ElementType == typeof(Float16);
            if (tin.ElementType != typeof(long))
                throw new InvalidDataException($"text.onnx input is {tin.ElementType.Name}, expected int64.");
            int outDims = (int)vision.OutputMetadata.Values.First().Dimensions.Last();
            if (outDims > 0 && outDims != Dimensions)
                throw new InvalidDataException($"vision.onnx yields {outDims} dimensions, manifest says {Dimensions}.");
        }
        catch
        {
            vision.Dispose();
            text.Dispose();
            throw;
        }

        _vision = vision;
        _text = text;
        _tokenizer = tokenizer;
        Device = device;
        DeviceName = deviceName;
        ModelDirectory = dir;
        _ready = true;
        _log.LogInformation("SigLIP2 {Model} loaded on {Device}{Name}", ModelId, device,
            deviceName is null ? "" : $" ({deviceName})");
    }

    private (InferenceSession, InferenceSession, EmbedDevice, string?) CreateSessions(string visionPath, string textPath)
    {
        if (_options.PreferGpu)
        {
            if (OperatingSystem.IsWindows())
            {
                DirectMLDevices.Adapter? adapter = DirectMLDevices.FirstHardwareAdapter();
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
                            return (vision, text, EmbedDevice.DirectML, adapter.Name);
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
                    using SessionOptions so = BaseOptions();
                    so.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                    {
                        ["ModelFormat"] = "MLProgram",
                        ["MLComputeUnits"] = "ALL",
                    });
                    var vision = new InferenceSession(visionPath, so);
                    try
                    {
                        var text = new InferenceSession(textPath, so);
                        return (vision, text, EmbedDevice.CoreML, null);
                    }
                    catch
                    {
                        vision.Dispose();
                        throw;
                    }
                }
                catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
                {
                    _log.LogWarning(e, "CoreML failed to initialise; using the CPU provider");
                }
            }
        }

        using SessionOptions cpu = BaseOptions();
        var v = new InferenceSession(visionPath, cpu);
        try
        {
            var t = new InferenceSession(textPath, cpu);
            return (v, t, EmbedDevice.Cpu, null);
        }
        catch
        {
            v.Dispose();
            throw;
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
    /// helps a little on CPU; the core may call this from its worker.
    /// </summary>
    public float[][] EmbedImages(IReadOnlyList<SKBitmap> bitmaps)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        InferenceSession session = Ready(_vision);
        int n = bitmaps.Count;
        if (n == 0) return Array.Empty<float[]>();
        int size = ImageSize;
        int per = ImagePreprocessor.InputLength(size);
        var pixels = new float[n * per];
        for (int i = 0; i < n; i++) ImagePreprocessor.Fill(bitmaps[i], size, pixels.AsSpan(i * per, per));

        long[] shape = { n, 3, size, size };
        using OrtValue input = _visionWantsHalf
            ? OrtValue.CreateTensorValueFromMemory(ToHalf(pixels), shape)
            : OrtValue.CreateTensorValueFromMemory(pixels, shape);
        return Run(session, _visionInput, input, _visionOutput, n);
    }

    public float[] EmbedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text to embed must not be empty.", nameof(text));
        return EmbedTexts(new[] { text })[0];
    }

    /// <summary>Embeds several sentences in one run.</summary>
    public float[][] EmbedTexts(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        InferenceSession session = Ready(_text);
        GemmaTextTokenizer tokenizer = _tokenizer!;
        int n = texts.Count;
        if (n == 0) return Array.Empty<float[]>();
        int len = tokenizer.MaxLength;
        var ids = new long[n * len];
        for (int i = 0; i < n; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
                throw new ArgumentException($"Text {i} to embed must not be empty.", nameof(texts));
            tokenizer.Encode(texts[i], ids.AsSpan(i * len, len));
        }
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(ids, new long[] { n, len });
        return Run(session, _textInput, input, _textOutput, n);
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
