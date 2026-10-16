using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Licence;
using Tessera.Api.Media;
using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Walks a root, hashes what it finds, and reconciles it against what is recorded.
/// </summary>
/// <remarks>
/// <para>
/// RECONCILIATION, NOT EVENTS. The scanner does not need to be told what
/// changed; it compares the disk to the database and applies the difference.
/// That makes it idempotent, resumable after a crash, and self-healing after
/// anything it missed - which matters more than speed, because the system this
/// replaces failed by quietly not noticing things.
/// </para>
/// <para>
/// Every run writes a <c>scan_run</c> row with seen / added / moved / copies /
/// gone / errors, updated every two seconds while it runs, so the Library
/// page shows a scan moving and "nothing happened" looks different from
/// "everything worked".
/// </para>
/// <para>
/// A file that is hashed also has its headers read, from the few kilobytes
/// at its front: the capture date (EXIF, XMP, QuickTime), else a date in its
/// name, else its modified time; and its shape. New content is therefore on
/// the timeline at the right day from the first scan, and the grid knows a
/// picture is portrait before its thumbnail exists.
/// </para>
/// </remarks>
public sealed class Scanner(
    Db db,
    ContentRepository content,
    FolderRepository folders,
    LibraryRepository library,
    ExclusiveWork exclusive,
    FreeTier tier,
    IOptions<ScannerOptions> options,
    ILogger<Scanner> log)
{
    /// <summary>
    /// What a scan enumerates. A file whose extension is not here is invisible
    /// to the scanner - and therefore must never be marked gone by it either
    /// (<see cref="ContentRepository.MarkGoneAsync"/> takes this same list).
    /// </summary>
    public static readonly IReadOnlyCollection<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
        ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".wmv", ".vob", ".3gp", ".mts", ".m2ts", ".mpg", ".mpeg", ".flv",
        ".insp", ".insv",
    };

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(2);

    private int _running;

    /// <summary>True while a scan is in flight. One heavy job at a time, by house rule.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The root being walked right now, or null.</summary>
    public long? RunningRootId { get; private set; }

    /// <summary>Spell a folder the way rel_path is spelled: no slashes at either end, forward slashes.</summary>
    public static string? NormaliseFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder.Trim('/', '\\').Replace('\\', '/');

    /// <param name="folder">
    /// A folder under the root to walk instead of the whole root - what the
    /// watch asks for when one folder changed, since walking 200,000 files to
    /// notice 80 is the wrong shape. Everything about the scan is scoped with
    /// it: what is known, what is walked, what may be marked gone.
    /// </param>
    /// <param name="runId">A run row opened by the queue, or null to open one here.</param>
    public async Task<ScanOutcome> ScanAsync(long rootId, string? folder, long? runId, CancellationToken ct)
    {
        // A second concurrent scan would double the disk load and race on the
        // same rows. Refusing is better than queueing silently.
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            throw new InvalidOperationException("A scan is already running.");
        }

        RunningRootId = rootId;
        try
        {
            return await RunAsync(rootId, NormaliseFolder(folder), runId, ct);
        }
        finally
        {
            RunningRootId = null;
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ScanOutcome> RunAsync(long rootId, string? folder, long? runId, CancellationToken ct)
    {
        var opts = options.Value;
        var startedAt = DateTimeOffset.UtcNow;

        var root = await library.RootAsync(rootId, ct)
            ?? throw new InvalidOperationException($"No scan root {rootId}.");
        if (!root.Enabled)
        {
            throw new InvalidOperationException($"Root {root.Label} is disabled.");
        }

        var walkFrom = folder is null ? root.AbsPath : PathRules.Canonical($"{root.AbsPath}/{folder}");
        if (folder is not null && PathRules.RelativeTo(root.AbsPath, walkFrom) is null)
        {
            throw new InvalidOperationException($"{folder} is not under root {root.Label}.");
        }

        // A missing drive looks exactly like an empty folder, and treating one
        // as the other would mark an entire library gone. Refuse instead.
        if (!Directory.Exists(root.AbsPath) || !Directory.Exists(walkFrom))
        {
            throw new DirectoryNotFoundException(
                $"Root {root.Label} is not present at {root.AbsPath}, or {walkFrom} is absent. Refusing to scan: " +
                "an absent root is indistinguishable from an empty one, and would " +
                "mark every file under it as gone.");
        }

        // One heavy job on the disk at a time. Polls for a short while: the
        // thumbnail worker releases the disk between batches and steps aside
        // when it sees this scan was refused.
        await using var disk = await exclusive.TryTakeArchiveAsync($"scan:{root.Label}", TimeSpan.FromSeconds(20), ct)
            ?? throw new InvalidOperationException(
                "Another job is reading the library disk. Scanning alongside it makes both slower, "
                + "so this scan was not started. Try again when it finishes.");

        runId ??= await library.CreateRunAsync(rootId, folder, ct);
        log.ScanStarted(runId.Value, root.Label, walkFrom);

        var known = await content.KnownUnderRootAsync(rootId, folder, ct);
        log.ScanKnown(runId.Value, known.Count);

        var outcome = new Counters();
        string? firstError = null;

        // Producer walks the tree; a bounded channel feeds N hashing workers.
        // Bounded so a fast walk cannot load a million paths into memory before
        // the slow part has started.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var file in EnumerateMedia(walkFrom, root.Exclude))
                {
                    await channel.Writer.WriteAsync(file, ct);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        var workers = Enumerable.Range(0, Math.Max(1, opts.Concurrency)).Select(_ => Task.Run(async () =>
        {
            await foreach (var file in channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var result = await ProcessAsync(root, file, known, opts, ct);
                    outcome.Add(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref outcome.Errors);
                    Interlocked.CompareExchange(ref firstError, $"{file}: {ex.Message}", null);
                    log.ScanFileFailed(ex, runId.Value, file);
                }
            }
        }, ct)).ToArray();

        // Progress, visible: the run row is refreshed while the walk goes on.
        using var progressStop = new CancellationTokenSource();
        var progress = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(ProgressEvery, progressStop.Token);
                    await WriteRunAsync(runId.Value, outcome, gone: 0, error: null, cancelled: false, finished: false, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var cancelled = false;
        try
        {
            await producer;
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            log.ScanCancelled(runId.Value);
        }
        finally
        {
            await progressStop.CancelAsync();
            await progress;
        }

        long gone = 0;
        if (!cancelled)
        {
            // Two conditions, not one. The walk must have COMPLETED - marking
            // rows gone from a partial walk declares live files missing, the
            // single most damaging thing a scanner can do. And the result must
            // look like a library rather than an accident: an external drive
            // that went away between scans can present as a root that exists
            // and is nearly empty.
            if (ScanSafety.MayMarkGone(known.Count, outcome.Seen))
            {
                gone = await content.MarkGoneAsync(rootId, startedAt, folder, MediaExtensions, ct);
            }
            else
            {
                firstError = ScanSafety.Explain(known.Count, outcome.Seen);
                log.ScanRefusedToMarkGone(runId.Value, known.Count, outcome.Seen);
            }
        }

        await WriteRunAsync(runId.Value, outcome, gone, firstError, cancelled, finished: true, CancellationToken.None);

        if (!cancelled)
        {
            // What the folders now imply: albums named after them, filled;
            // thumbnails for what is new, within the free tier's cap.
            var (albums, members) = await folders.ReconcileAlbumsAsync(rootId, folder, CancellationToken.None);
            var maxId = await tier.MaxContentIdAsync(CancellationToken.None);
            var thumbs = await folders.QueueThumbnailsUnderAsync(rootId, folder, maxId, CancellationToken.None);
            await folders.ScannedAsync(rootId, folder, CancellationToken.None);
            log.ScanFolderAlbums(runId.Value, albums, members, thumbs);
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        log.ScanFinished(runId.Value, outcome.Seen, outcome.Added, outcome.Moved, outcome.Copies,
                         outcome.Unchanged, gone, outcome.Errors, cancelled,
                         (long)elapsed.TotalMilliseconds,
                         elapsed.TotalSeconds > 0 ? outcome.Seen / elapsed.TotalSeconds : 0);

        return new ScanOutcome(runId.Value, outcome.Seen, outcome.Added, outcome.Moved, outcome.Copies,
                               outcome.Unchanged, gone, outcome.Errors, cancelled);
    }

    private async Task<Reconciled> ProcessAsync(
        ScanRoot root, string absPath,
        Dictionary<string, (long ContentId, long Size, DateTimeOffset? Mtime)> known,
        ScannerOptions opts, CancellationToken ct)
    {
        var rel = PathRules.RelativeTo(root.AbsPath, absPath)
            ?? throw new InvalidOperationException($"{absPath} is not under {root.AbsPath}");

        var info = new FileInfo(absPath);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        // Unchanged size AND timestamp means unchanged bytes, near enough: the
        // alternative is re-reading the whole library to learn nothing.
        if (opts.TrustMtime &&
            known.TryGetValue(rel, out var prev) &&
            prev.Size == info.Length &&
            prev.Mtime is { } m && Math.Abs((m - mtime).TotalSeconds) < 2)
        {
            await content.TouchAsync(root.RootId, rel, DateTimeOffset.UtcNow, ct);
            return Reconciled.Unchanged;
        }

        // Headers first: a few KB and the odd seek, which leaves the front of
        // the file in the cache for the hash that follows.
        var now = DateTimeOffset.UtcNow;
        var headers = default(HeaderFacts);
        var metaVersion = CaptureDates.ReaderVersion;
        try
        {
            headers = MediaHeaders.Read(absPath, opts.LocalZone, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not open it for headers; the hash below will say whether
            // the file is readable at all. Left unread, the backfill retries.
            metaVersion = 0;
        }

        var captured = MediaHeaders.Resolve(headers, Path.GetFileName(absPath), mtime, opts.LocalZone, now);
        var sha = await Hashing.Sha256FileAsync(absPath, ct);
        var file = new ScannedFile(
            Sha256: sha,
            ByteSize: info.Length,
            Mime: MimeFor(absPath),
            RelPath: rel,
            FileMtime: mtime,
            TakenAt: captured.TakenAt,
            TakenSrc: captured.TakenSrc,
            Width: captured.Width,
            Height: captured.Height,
            DurationMs: captured.DurationMs,
            MetaVersion: metaVersion);

        var contentId = await content.UpsertAsync(file, ct);
        return await content.RecordLocationAsync(contentId, root.AccountId, root.RootId, file, o => StillThere(o, absPath), ct);
    }

    /// <summary>
    /// Is another recorded location of these bytes still on disk? Decides a
    /// copy (yes) from a move (no). Asked only for known bytes at a new path.
    /// </summary>
    /// <remarks>
    /// A root that is not mounted cannot be judged and counts as still
    /// there: its rows stay live until a scan of that root says otherwise, so
    /// the new path is a copy. A path that differs from the new one only in
    /// case is the same file on Windows and macOS, renamed: not there. A file
    /// at the old path with a different size holds other bytes now: not there.
    /// </remarks>
    internal static bool StillThere(OtherLocation other, string newAbsPath)
    {
        if (!Directory.Exists(other.RootPath))
        {
            return true;
        }

        var abs = PathRules.Canonical($"{other.RootPath}/{other.RelPath}");
        if (string.Equals(abs, newAbsPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var info = new FileInfo(abs);
        return info.Exists && info.Length == other.ByteSize;
    }

    private static IEnumerable<string> EnumerateMedia(string root, string[] exclude)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Following a junction or symlink can walk in a circle, or out of
            // the root the person chose. Never follow them.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            if (!MediaExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            if (exclude.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return PathRules.Canonical(path);
        }
    }

    public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".heic" or ".heif" => "image/heic",
        ".avif" => "image/avif",
        ".jxl" => "image/jxl",
        ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" => "image/x-raw",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".3gp" => "video/3gpp",
        ".mts" or ".m2ts" => "video/mp2t",
        ".flv" => "video/x-flv",
        ".vob" or ".insp" or ".insv" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    private async Task WriteRunAsync(
        long runId, Counters c, long gone, string? error, bool cancelled, bool finished, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE scan_run
               SET finished_at = CASE WHEN @Finished THEN {Db.NowMs} END,
                   files_seen = @Seen, added = @Added, moved = @Moved, copies = @Copies,
                   unchanged = @Same, marked_gone = @Gone, errors = @Errors,
                   error_sample = @Error, cancelled = @Cancelled
             WHERE run_id = @RunId
            """,
            new
            {
                RunId = runId, Finished = finished,
                Seen = Interlocked.Read(ref c.Seen), Added = Interlocked.Read(ref c.Added),
                Moved = Interlocked.Read(ref c.Moved), Copies = Interlocked.Read(ref c.Copies),
                Same = Interlocked.Read(ref c.Unchanged),
                Gone = gone, Errors = Interlocked.Read(ref c.Errors),
                Error = error, Cancelled = cancelled,
            },
            cancellationToken: ct));
    }

    private sealed class Counters
    {
        public long Seen;
        public long Added;
        public long Moved;
        public long Copies;
        public long Unchanged;
        public long Errors;

        public void Add(Reconciled r)
        {
            Interlocked.Increment(ref Seen);
            switch (r)
            {
                case Reconciled.Added: Interlocked.Increment(ref Added); break;
                case Reconciled.Moved: Interlocked.Increment(ref Moved); break;
                case Reconciled.Copied: Interlocked.Increment(ref Copies); break;
                default: Interlocked.Increment(ref Unchanged); break;
            }
        }
    }
}

public sealed record ScanOutcome(
    long RunId, long Seen, long Added, long Moved, long Copies, long Unchanged,
    long MarkedGone, long Errors, bool Cancelled);
