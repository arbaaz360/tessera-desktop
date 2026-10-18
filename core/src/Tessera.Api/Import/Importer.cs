using System.Net.Http.Headers;
using Tessera.Api.Licence;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Import;

/// <summary>Who an import is for and which album, if any, it fills.</summary>
/// <param name="FolderName">The folder under the Tessera folder that copies go to.</param>
public sealed record ImportTarget(long AccountId, long? AlbumId, string FolderName);

/// <summary>The one HTTP client the import may use: a link a person dropped, fetched once.</summary>
public sealed class ImportHttp(HttpClient client)
{
    public HttpClient Client { get; } = client;
}

/// <summary>
/// What adding one dropped or picked thing means. The rules, in order:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>A FOLDER is indexed where it is, never copied: a folder inside a
/// library folder is rescanned, any other becomes a library folder. Its
/// pictures go into the album, if there is one.</item>
/// <item>A FILE INSIDE a library folder is indexed now, where it is.</item>
/// <item>A FILE OUTSIDE every library folder whose bytes the library already
/// holds is not copied: the picture already there is used.</item>
/// <item>ANY OTHER FILE is copied into the Tessera folder, under the album's
/// name or "Unsorted", verified before it gets its name
/// (<see cref="SafeCopy"/>). The original is only ever read.</item>
/// <item>BYTES (a drop the webview hands over as data) and a LINK (fetched
/// once, because a person dropped it) are treated as a file from outside.</item>
/// </list>
/// A picture that was in the trash comes back out: adding it again says the
/// person wants to see it. Every answer is an <see cref="ItemResult"/>;
/// nothing here throws for a bad item, so one unreadable file never stops a
/// drop of five hundred.
/// </remarks>
public sealed class Importer(
    LibraryRepository library,
    ContentRepository content,
    AlbumRepository albums,
    WorkItems work,
    FreeTier tier,
    Scanner scanner,
    ScanRunner scans,
    TesseraFolder tesseraFolder,
    ImportHttp http,
    ILogger<Importer> log)
{
    /// <summary>The largest download a dropped link may be.</summary>
    public const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>A path: a folder or a file.</summary>
    public async Task<ItemResult> PathAsync(string raw, ImportTarget target, CancellationToken ct)
    {
        string path;
        try
        {
            path = PathRules.Canonical(System.IO.Path.GetFullPath(raw.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Skipped("not a usable path");
        }

        return await GuardAsync(raw, async () =>
        {
            if (Directory.Exists(path))
            {
                return await FolderAsync(path, target, ct);
            }

            if (!File.Exists(path))
            {
                return Skipped("not found");
            }

            if (!Scanner.MediaExtensions.Contains(System.IO.Path.GetExtension(path)))
            {
                return Skipped("not a picture or video");
            }

            return await FileAsync(path, target, ct);
        }, ct);
    }

    /// <summary>A link a person dropped: fetched once, then treated as a file from outside.</summary>
    public async Task<ItemResult> UrlAsync(string raw, ImportTarget target, CancellationToken ct)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Skipped("only http and https links can be fetched");
        }

        return await GuardAsync(raw!, async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);

            using var response = await http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Skipped($"the server answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                return Skipped("larger than 4 GB");
            }

            var name = NameFor(response.Content.Headers, uri);
            if (!Scanner.MediaExtensions.Contains(System.IO.Path.GetExtension(name)))
            {
                var type = response.Content.Headers.ContentType?.MediaType ?? "no content type";
                return Skipped($"not a picture or video ({type})");
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await FromOutsideAsync(body, name, target, timeout.Token);
        }, ct);
    }

    /// <summary>Bytes the webview handed over for a drop, with the name they came with.</summary>
    public async Task<ItemResult> BytesAsync(Stream body, string? name, ImportTarget target, CancellationToken ct)
    {
        var safe = SafeCopy.SafeFileName(name);
        if (!Scanner.MediaExtensions.Contains(System.IO.Path.GetExtension(safe)))
        {
            return Skipped("not a picture or video");
        }

        return await GuardAsync(safe, () => FromOutsideAsync(body, safe, target, ct), ct);
    }

    // ------------------------------------------------------------ the rules

    private async Task<ItemResult> FolderAsync(string path, ImportTarget target, CancellationToken ct)
    {
        var holder = Holder(await library.AllRootsAsync(ct), path);
        long rootId;
        string? folder;
        var isNew = false;

        if (holder is not null)
        {
            rootId = holder.RootId;
            var rel = PathRules.RelativeTo(holder.AbsPath, path)!;
            folder = rel.Length == 0 ? null : rel;
        }
        else
        {
            var (conflict, other) = await library.ConflictAsync(path, ct);
            if (conflict != RootConflict.None)
            {
                return Skipped(conflict == RootConflict.ContainsARoot
                    ? $"it holds the library folder {other}; drop the folders inside it instead"
                    : $"it overlaps the library folder {other}");
            }

            var label = System.IO.Path.GetFileName(path);
            rootId = await library.AddRootAsync(target.AccountId, path, string.IsNullOrEmpty(label) ? path : label, ct);
            folder = null;
            isNew = true;
        }

        // The scan does the indexing, as for a folder added on the Library
        // page; this waits for it so the album gets what it found.
        var runId = await scans.EnqueueAsync(rootId, folder, ct) ?? await scans.InFlightRunIdAsync(rootId, folder, ct);
        if (runId is { } id)
        {
            await scans.WhenFinishedAsync(id, ct);
            if (await library.RunAsync(id, ct) is { FilesSeen: 0, Errors: > 0, ErrorSample: { } error })
            {
                return Skipped(error) with { RootId = rootId, IsFolder = true, Path = path };
            }
        }

        if (target.AlbumId is { } albumId)
        {
            await albums.AddUnderAsync(target.AccountId, albumId, rootId, folder, ct);
        }

        var pictures = await library.LiveContentsUnderAsync(rootId, folder, ct);
        return new ItemResult(isNew ? ImportOutcome.Added : ImportOutcome.Already,
            Path: path, RootId: rootId, Pictures: pictures, IsFolder: true);
    }

    private async Task<ItemResult> FileAsync(string path, ImportTarget target, CancellationToken ct)
    {
        // Inside a library folder: indexed where it is, now.
        if (Holder(await library.AllRootsAsync(ct), path) is { } root)
        {
            var (contentId, outcome) = await scanner.IndexFileAsync(root, path, ct);
            var restored = await FileIntoAsync(contentId, path, target, ct);
            return new ItemResult(
                outcome == Reconciled.Added ? ImportOutcome.Added : ImportOutcome.Already,
                Reason: restored, ContentId: contentId, Path: path, RootId: root.RootId);
        }

        // Outside: the bytes decide. Known bytes are not copied again.
        var sha = await Hashing.Sha256FileAsync(path, ct);
        if (await content.LiveByShaAsync(target.AccountId, sha, ct) is { } live)
        {
            var restored = await FileIntoAsync(live.ContentId, path, target, ct);
            return new ItemResult(ImportOutcome.Already, Reason: restored, ContentId: live.ContentId);
        }

        var app = await tesseraFolder.EnsureAsync(target.AccountId, ct);
        var committed = await SafeCopy.CopyFileAsync(path, FolderFor(app, target), System.IO.Path.GetFileName(path), sha, ct);
        return await IndexCopyAsync(app, committed, target, ct);
    }

    /// <summary>Bytes from outside every root: staged in the Tessera folder, kept only if new.</summary>
    private async Task<ItemResult> FromOutsideAsync(Stream body, string name, ImportTarget target, CancellationToken ct)
    {
        var app = await tesseraFolder.EnsureAsync(target.AccountId, ct);
        var staged = await SafeCopy.StageAsync(body, FolderFor(app, target), ct, MaxDownloadBytes);

        if (await content.LiveByShaAsync(target.AccountId, staged.Sha256, ct) is { } live)
        {
            // Only the app's own temporary file is removed; the library already
            // has these bytes, so nothing is copied.
            SafeCopy.Discard(staged);
            var restored = await FileIntoAsync(live.ContentId, name, target, ct);
            return new ItemResult(ImportOutcome.Already, Reason: restored, ContentId: live.ContentId);
        }

        Committed committed;
        try
        {
            committed = SafeCopy.Commit(staged, name);
        }
        catch
        {
            SafeCopy.Discard(staged);
            throw;
        }

        return await IndexCopyAsync(app, committed, target, ct);
    }

    private async Task<ItemResult> IndexCopyAsync(ScanRoot app, Committed committed, ImportTarget target, CancellationToken ct)
    {
        var (contentId, _) = await scanner.IndexFileAsync(app, committed.Path, ct);
        var restored = await FileIntoAsync(contentId, committed.Path, target, ct);
        return committed.Existing
            ? new ItemResult(ImportOutcome.Already, restored ?? "the same file was in the Tessera folder", contentId, PathRules.Canonical(committed.Path), app.RootId)
            : new ItemResult(ImportOutcome.Copied, restored, contentId, PathRules.Canonical(committed.Path), app.RootId);
    }

    /// <summary>
    /// The picture is in the library now: out of the trash, into the album,
    /// and a thumbnail queued if it has none (within the free tier's cap).
    /// </summary>
    /// <returns>A reason to show when it came out of the trash; else null.</returns>
    private async Task<string?> FileIntoAsync(long contentId, string nameOrPath, ImportTarget target, CancellationToken ct)
    {
        var restored = await content.RestoreIfAllTrashedAsync(target.AccountId, contentId, ct);

        if (target.AlbumId is { } albumId)
        {
            await albums.AddAsync(target.AccountId, albumId, [contentId], ct);
        }

        if (Scanner.MimeFor(nameOrPath).StartsWith("image/", StringComparison.Ordinal) &&
            (await tier.MaxContentIdAsync(ct) is not { } max || contentId <= max))
        {
            await work.EnqueueThumbnailIfMissingAsync(contentId, ct);
        }

        return restored ? "it was in the trash; restored" : null;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// The root a path is in: the deepest one, because the app's own folder
    /// may sit inside a person's root and owns what is in it.
    /// </summary>
    public static ScanRoot? Holder(IEnumerable<ScanRoot> roots, string canonicalPath) =>
        roots.Where(r => PathRules.RelativeTo(r.AbsPath, canonicalPath) is not null)
             .OrderByDescending(r => r.AbsPath.Length)
             .FirstOrDefault();

    private static string FolderFor(ScanRoot app, ImportTarget target) =>
        PathRules.Canonical($"{app.AbsPath}/{SafeCopy.SafeFolderName(target.FolderName)}");

    private static ItemResult Skipped(string reason) => new(ImportOutcome.Skipped, reason);

    /// <summary>One bad item is a skipped item with its reason, never a failed import.</summary>
    private async Task<ItemResult> GuardAsync(string source, Func<Task<ItemResult>> work, CancellationToken ct)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TesseraFolderException ex)
        {
            return Skipped(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Skipped("timed out");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                                       or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.ImportItemFailed(ex, source);
            return Skipped(ex.Message);
        }
    }

    /// <summary>A name for a download: the server's, else the link's last segment, with an extension from the type when it has none.</summary>
    public static string NameFor(HttpContentHeaders headers, Uri uri)
    {
        var name = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
        name = name?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            var last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1]).Trim('/') : "";
            name = last.Length > 0 ? last : "download";
        }

        name = SafeCopy.SafeFileName(name);
        if (!Scanner.MediaExtensions.Contains(System.IO.Path.GetExtension(name)) &&
            ExtensionFor(headers.ContentType?.MediaType) is { } ext)
        {
            name += ext;
        }

        return name;
    }

    private static string? ExtensionFor(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/heic" or "image/heif" => ".heic",
        "image/avif" => ".avif",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        _ => null,
    };
}
