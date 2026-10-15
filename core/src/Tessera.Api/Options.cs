using Tessera.Core;
namespace Tessera.Api;

public sealed class StorageOptions
{
    public const string Section = "Tessera:Storage";

    /// <summary>Where derived files live. Tessera writes here and nowhere else.</summary>
    public string ThumbnailRoot { get; set; } = "/thumbs";

    /// <summary>Longest edge of the one derived image, the grid thumbnail.</summary>
    public int ThumbSize { get; set; } = 320;
    public int JpegQuality { get; set; } = 80;
}

public sealed class ScannerOptions
{
    public const string Section = "Tessera:Scanner";

    /// <summary>
    /// Hashing threads. Reading is the cost, not the SHA-256, so this tracks
    /// how many concurrent reads the underlying disk rewards - which on the
    /// archive HDD is few, and on NVMe is many.
    /// </summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>
    /// Trust size+mtime to mean "unchanged" and skip re-hashing.
    /// </summary>
    /// <remarks>
    /// Turning this off makes every scan re-read every byte, which is correct
    /// but costs hours. On means a file edited without changing its size or
    /// timestamp would be missed - rare enough to accept, and recoverable with
    /// a forced rescan.
    /// </remarks>
    public bool TrustMtime { get; set; } = true;
}

public sealed class UpstreamOptions
{
    public const string Section = "Tessera";

    public ServiceRef Foundation { get; set; } = new();
    public ServiceRef FaceSearch { get; set; } = new();

    /// <summary>A worker behind a contract: docs/contracts/cover-picker.md.</summary>
    public ServiceRef CoverPicker { get; set; } = new();

    /// <summary>A worker behind a contract: docs/contracts/music.md.</summary>
    public ServiceRef MusicIdentifier { get; set; } = new();


    public sealed class ServiceRef
    {
        public string BaseUrl { get; set; } = "";
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
    }
}

/// <summary>
/// How each root this container mounts is spelled on the HOST.
/// </summary>
/// <remarks>
/// Set from the same registry as the mounts themselves (homelab.env), as
/// <c>/library=X:/Immich/uploads/library;/reddit=X:/Pics/n/reddit;…</c>.
/// Tessera speaks the host spelling whenever it hands a folder to another
/// service: it is the one spelling every service's mount map understands,
/// where a container path is meaningful only inside the container that
/// mounted it. This is the §5 hazard - one folder, three names - held to a
/// single translation, declared once.
/// </remarks>
public sealed class RootOptions
{
    public const string Section = "Tessera:Roots";

    public string HostPaths { get; set; } = "";

    /// <summary>A container path under one of the roots, spelled as the host spells it.</summary>
    public string? ToHostPath(string containerPath)
    {
        var canonical = PathRules.Canonical(containerPath);
        foreach (var pair in HostPaths.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var root = PathRules.Canonical(pair[..eq].Trim());
            var host = pair[(eq + 1)..].Trim().Replace('\\', '/').TrimEnd('/');
            if (PathRules.RelativeTo(root, canonical) is { } rel)
            {
                return rel.Length == 0 ? host : $"{host}/{rel}";
            }
        }

        return null;
    }
}
