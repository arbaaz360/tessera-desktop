using SkiaSharp;

namespace Tessera.Desktop.Search;

/// <summary>
/// Progress of one model file download. <see cref="TotalBytes"/> is null
/// when the server did not say.
/// </summary>
public sealed record ModelDownloadProgress(string File, long ReceivedBytes, long? TotalBytes);

/// <summary>
/// Where the model runs, as the implementation chose it at load time.
/// </summary>
public enum EmbedDevice { Cpu, DirectML, CoreML }

/// <summary>
/// Options the host passes when registering the search library.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>Directory that holds <c>&lt;modelId&gt;/vision.onnx</c> and friends. Created if missing.</summary>
    public required string ModelsDir { get; set; }

    /// <summary>The model to load. The default is the only one v1 ships.</summary>
    public string ModelId { get; set; } = "siglip2-base-patch16-256";

    /// <summary>Try DirectML (Windows) or CoreML (macOS) before falling back to CPU.</summary>
    public bool PreferGpu { get; set; } = true;

    /// <summary>Threads for the CPU provider. 0 = let ONNX Runtime decide.</summary>
    public int CpuThreads { get; set; } = 0;
}

/// <summary>
/// Turns an image or a sentence into one L2-normalised vector in a shared
/// space, so that the cosine of an image vector and a text vector says how
/// well the words describe the picture.
/// </summary>
/// <remarks>
/// Thread safety: <see cref="EmbedImage"/> and <see cref="EmbedText"/> may be
/// called concurrently from several threads once <see cref="IsReady"/> is
/// true. <see cref="EnsureReadyAsync"/> is idempotent and may be called
/// repeatedly; concurrent callers share one download.
/// </remarks>
public interface IImageEmbedder
{
    /// <summary>The model's id, e.g. <c>siglip2-base-patch16-256</c>.</summary>
    string ModelId { get; }

    /// <summary>Vector length, e.g. 768.</summary>
    int Dimensions { get; }

    /// <summary>True once the model files are present, verified and loaded.</summary>
    bool IsReady { get; }

    /// <summary>The device chosen at load time; null before the model is loaded.</summary>
    EmbedDevice? Device { get; }

    /// <summary>
    /// Downloads any missing model file from the manifest, verifies every
    /// file's sha256, loads the sessions. Returns when <see cref="IsReady"/>
    /// is true or throws.
    /// </summary>
    Task EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Embeds a bitmap of any size. The implementation resizes and normalises
    /// as its model expects. The caller keeps ownership of the bitmap.
    /// </summary>
    float[] EmbedImage(SKBitmap bitmap);

    /// <summary>Embeds a sentence; empty or whitespace input throws.</summary>
    float[] EmbedText(string text);
}

/// <summary>One search hit: the content id and its cosine score.</summary>
public readonly record struct VectorHit(long Id, float Score);

/// <summary>
/// An in-memory nearest-neighbour index over L2-normalised vectors, keyed by
/// content id, persisted to one file.
/// </summary>
/// <remarks>
/// Thread safety: <see cref="Search"/> may run concurrently with other
/// searches; writes (<see cref="Upsert"/>, <see cref="Remove"/>,
/// <see cref="LoadAsync"/>) are serialised by the implementation.
/// </remarks>
public interface IVectorIndex
{
    int Count { get; }
    int Dimensions { get; }

    void Upsert(long id, ReadOnlySpan<float> vector);
    bool Remove(long id);
    bool Contains(long id);

    /// <summary>
    /// The <paramref name="k"/> best hits by cosine, best first. When
    /// <paramref name="filter"/> is given, ids it rejects are skipped before
    /// the top-k is taken, so the answer always has up to k rows that pass.
    /// </summary>
    IReadOnlyList<VectorHit> Search(ReadOnlySpan<float> query, int k, Func<long, bool>? filter = null);

    /// <summary>Writes the whole index. The format is the implementation's; it must round-trip through <see cref="LoadAsync"/>.</summary>
    Task SaveAsync(Stream stream, CancellationToken ct);

    /// <summary>Replaces the index contents with what the stream holds.</summary>
    Task LoadAsync(Stream stream, CancellationToken ct);
}
