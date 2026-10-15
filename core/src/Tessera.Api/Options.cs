namespace Tessera.Api;

public sealed class StorageOptions
{
    /// <summary>Where derived files live: <c>&lt;data&gt;/thumbs</c>. Tessera writes here and nowhere near a library.</summary>
    public string ThumbnailRoot { get; set; } = "";

    /// <summary>Longest edge of the one derived image, the grid thumbnail.</summary>
    public int ThumbSize { get; set; } = 320;
    public int JpegQuality { get; set; } = 80;
}

public sealed class ScannerOptions
{
    /// <summary>
    /// Hashing threads. Reading is the cost, not the SHA-256, so this tracks
    /// how many concurrent reads the disk rewards: few on a spinning disk,
    /// many on NVMe. Half the cores, between 2 and 8, is a fair default for
    /// a machine whose disk is unknown.
    /// </summary>
    public int Concurrency { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Trust size+mtime to mean "unchanged" and skip re-hashing.
    /// </summary>
    /// <remarks>
    /// Off makes every scan re-read every byte, which is correct but costs
    /// hours. On means a file edited without changing its size or timestamp
    /// is missed - rare enough to accept.
    /// </remarks>
    public bool TrustMtime { get; set; } = true;
}
