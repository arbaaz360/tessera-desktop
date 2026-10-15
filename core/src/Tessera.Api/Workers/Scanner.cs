using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Core;
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
/// Every run writes a <c>scan_run</c> row with seen / added / moved / gone /
/// errors, so "nothing happened" and "everything worked" look different. Four
/// containers in the old system reported healthy for eighteen days while every
/// run died on startup.
/// </para>
/// </remarks>
public sealed class Scanner(
    Db db,
    ContentRepository content,
    FolderRepository folders,
    ExclusiveWork exclusive,
    Indexing indexing,
    IOptions<ScannerOptions> options,
    IOptions<RootOptions> roots,
    ILogger<Scanner> log)
{
    /// <summary>
    /// What a scan enumerates. Everything Immich accepted and the library
    /// holds: the common formats, phone RAW (.dng), camera RAW, and the older
    /// video containers. A file whose extension is not here is invisible to
    /// the scanner - and therefore must never be marked gone by it either
    /// (<see cref="ContentRepository.MarkGoneAsync"/> takes this same list):
    /// 218 .dng files were once declared missing by a scan that had simply
    /// not looked at them.
    /// </summary>
    public static readonly IReadOnlyCollection<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
        ".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v", ".wmv", ".vob", ".3gp", ".mts", ".m2ts", ".mpg", ".mpeg", ".flv",
        ".insp", ".insv",
    };

    private int _running;

    /// <summary>True while a scan is in flight. One heavy job at a time, by house rule.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <param name="folder">
    /// A folder under the root to walk instead of the whole root - what an
    /// album sync needs, since one profile's download lands in one folder and
    /// walking 500,000 files to notice 80 is the wrong shape. Everything about
    /// the scan is scoped with it: what is known, what is walked, what may be
    /// marked gone.
    /// </param>
    public async Task<ScanOutcome> ScanAsync(long rootId, string? folder, CancellationToken ct)
    {
        // A second concurrent scan would double the disk load and race on the
        // same rows. Refusing is better than queueing silently.
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            throw new InvalidOperationException("A scan is already running.");
        }

        try
        {
            return await RunAsync(rootId, folder, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ScanOutcome> RunAsync(long rootId, string? folder, CancellationToken ct)
    {
        var opts = options.Value;
        var startedAt = DateTimeOffset.UtcNow;

        var root = await LoadRootAsync(rootId, ct)
            ?? throw new InvalidOperationException($"No scan root {rootId}.");

        // The folder is spelled relative to the root, without slashes at either
        // end, so it can be compared against rel_path and joined to abs_path.
        folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim('/', '\\').Replace('\\', '/');
        var walkFrom = folder is null ? root.AbsPath : PathRules.Canonical($"{root.AbsPath}/{folder}");
        if (folder is not null && PathRules.RelativeTo(root.AbsPath, walkFrom) is null)
        {
            throw new InvalidOperationException($"{folder} is not under root {root.Label}.");
        }

        // A missing mount looks exactly like an empty folder, and treating one
        // as the other would mark an entire library gone. Refuse instead.
        if (!Directory.Exists(root.AbsPath) || !Directory.Exists(walkFrom))
        {
            throw new DirectoryNotFoundException(
                $"Root {root.Label} is not mounted at {root.AbsPath}, or {walkFrom} is absent. Refusing to scan: " +
                "an unmounted root is indistinguishable from an empty one, and would " +
                "mark every file under it as gone.");
        }

        // One heavy job on the archive at a time. A scan and a thumbnail run
        // both walk the same disk; together they halve each other rather than
        // doubling. Held for the whole scan and released by the connection
        // dying, so a killed process leaves nothing to clear up.
        // Polls for a short while: the thumbnail worker releases the disk
        // between batches and steps aside when it sees this scan was refused.
        await using var disk = await exclusive.TryTakeArchiveAsync($"scan:{root.Label}", TimeSpan.FromSeconds(20), ct)
            ?? throw new InvalidOperationException(
                "Another job is reading the archive. Scanning alongside it makes both slower, "
                + "so this scan was not started. Try again when it finishes.");

        var runId = await StartRunAsync(rootId, ct);
        log.ScanStarted(runId, root.Label, root.AbsPath);

        var known = await content.KnownUnderRootAsync(rootId, folder, ct);
        log.ScanKnown(runId, known.Count);

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
                    log.ScanFileFailed(ex, runId, file);
                }
            }
        }, ct)).ToArray();

        var cancelled = false;
        try
        {
            await producer;
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            log.ScanCancelled(runId);
        }

        long gone = 0;
        if (!cancelled)
        {
            // Two conditions, not one. The walk must have COMPLETED - marking
            // rows gone from a partial walk declares live files missing, the
            // single most damaging thing a scanner can do. And the result must
            // look like a library rather than an accident: Docker creates a
            // bind-mount source that is missing, so a renamed folder on the
            // host presents as a root that exists and is empty, which the
            // Directory.Exists check above cannot tell from a real one.
            if (ScanSafety.MayMarkGone(known.Count, outcome.Seen))
            {
                gone = await content.MarkGoneAsync(rootId, startedAt, folder, MediaExtensions, ct);
            }
            else
            {
                firstError = ScanSafety.Explain(known.Count, outcome.Seen);
                log.ScanRefusedToMarkGone(runId, known.Count, outcome.Seen);
            }
        }

        await FinishRunAsync(runId, outcome, gone, firstError, cancelled, CancellationToken.None);

        if (!cancelled)
        {
            // What the folders now imply: albums named after them, filled;
            // thumbnails for what is new. Set-based, scoped to what was
            // walked, and the reason a copied-in folder becomes an album
            // without anyone asking (docs: LibraryWatch).
            var (albums, members) = await folders.ReconcileAlbumsAsync(rootId, folder, CancellationToken.None);
            var thumbs = await folders.QueueThumbnailsUnderAsync(rootId, folder, CancellationToken.None);
            await folders.ScannedAsync(rootId, folder, CancellationToken.None);
            log.ScanFolderAlbums(runId, albums, members, thumbs);

            // And the GPU work, QUEUED: faces with FaceSearch, images with
            // Foundation - for a personal account's folder, in the host's
            // spelling. Nothing starts here; the jobs page's Run does, once,
            // for everything queued (Indexing.cs). A whole-root scan is not
            // queued: it walks half a million files to notice eighty, and the
            // folder scans that the watch runs already cover what is new.
            if (outcome.Added > 0 && folder is not null && !await IsCorpusAsync(root.AccountId, CancellationToken.None) &&
                roots.Value.ToHostPath(PathRules.Canonical($"{root.AbsPath}/{folder}")) is { } hostFolder)
            {
                var request = await indexing.RequestFacesAsync(hostFolder, "tessera-scan", CancellationToken.None);
                var registered = await indexing.RegisterAsync(hostFolder, CancellationToken.None);
                log.ScanQueuedIndexing(runId, folder, request, registered);
            }
        }

        log.ScanFinished(runId, outcome.Seen, outcome.Added, outcome.Moved,
                        outcome.Unchanged, gone, outcome.Errors, cancelled);

        return new ScanOutcome(runId, outcome.Seen, outcome.Added, outcome.Moved,
                               outcome.Unchanged, gone, outcome.Errors, cancelled);
    }

    private async Task<Reconciled> ProcessAsync(
        ScanRootRow root, string absPath,
        Dictionary<string, (long ContentId, long Size, DateTimeOffset? Mtime)> known,
        ScannerOptions opts, CancellationToken ct)
    {
        var rel = PathRules.RelativeTo(root.AbsPath, absPath)
            ?? throw new InvalidOperationException($"{absPath} is not under {root.AbsPath}");

        var info = new FileInfo(absPath);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        // Unchanged size AND timestamp means unchanged bytes, near enough: the
        // alternative is re-reading 900 GB to learn nothing. A forced rescan
        // exists for when that assumption needs testing.
        if (opts.TrustMtime &&
            known.TryGetValue(rel, out var prev) &&
            prev.Size == info.Length &&
            prev.Mtime is { } m && Math.Abs((m - mtime).TotalSeconds) < 2)
        {
            await TouchAsync(root.RootId, rel, ct);
            return Reconciled.Unchanged;
        }

        var sha = await Hashing.Sha256FileAsync(absPath, ct);
        var file = new ScannedFile(
            Sha256: sha,
            ByteSize: info.Length,
            Mime: MimeFor(absPath),
            RelPath: rel,
            FileMtime: mtime,
            TakenAt: mtime,          // metadata extraction refines this later
            TakenSrc: "mtime",
            Width: null,
            Height: null);

        var contentId = await content.UpsertAsync(file, ct);
        return await content.RecordLocationAsync(contentId, root.AccountId, root.RootId, file, ct);
    }

    private async Task TouchAsync(long rootId, string relPath, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE location SET last_seen = now() WHERE root_id = @RootId AND rel_path = @RelPath",
            new { RootId = rootId, RelPath = relPath },
            cancellationToken: ct));
    }

    private static IEnumerable<string> EnumerateMedia(string root, string[] exclude)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // A Windows junction becomes a broken symlink inside a container,
            // and following one can walk in a circle. Never follow them.
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

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".heic" or ".heif" => "image/heic",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream",
    };

    // Init-property, not positional: Npgsql reports a text[] column's field
    // type as System.Array, and Dapper then finds no constructor to match. A
    // settable property takes the string[] value as it is. This is the
    // reason no scan had ever completed in production before 2026-09-12.
    private sealed record ScanRootRow
    {
        public long RootId { get; init; }
        public long AccountId { get; init; }
        public string Label { get; init; } = "";
        public string AbsPath { get; init; } = "";
        public string[] Exclude { get; init; } = [];
    }

    private async Task<bool> IsCorpusAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT kind = 'corpus' FROM account WHERE account_id = @AccountId",
            new { AccountId = accountId }, cancellationToken: ct));
    }

    private async Task<ScanRootRow?> LoadRootAsync(long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ScanRootRow>(new CommandDefinition(
            """
            SELECT root_id AS RootId, account_id AS AccountId, label AS Label,
                   abs_path AS AbsPath, exclude AS Exclude
              FROM scan_root WHERE root_id = @RootId AND enabled
            """,
            new { RootId = rootId },
            cancellationToken: ct));
    }

    private async Task<long> StartRunAsync(long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO scan_run (root_id) VALUES (@RootId) RETURNING run_id",
            new { RootId = rootId },
            cancellationToken: ct));
    }

    private async Task FinishRunAsync(
        long runId, Counters c, long gone, string? error, bool cancelled, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE scan_run
               SET finished_at = now(), files_seen = @Seen, added = @Added, moved = @Moved,
                   unchanged = @Same, marked_gone = @Gone, errors = @Errors,
                   error_sample = @Error, cancelled = @Cancelled
             WHERE run_id = @RunId
            """,
            new
            {
                RunId = runId, Seen = c.Seen, Added = c.Added, Moved = c.Moved,
                Same = c.Unchanged, Gone = gone, Errors = c.Errors,
                Error = error, Cancelled = cancelled,
            },
            cancellationToken: ct));
    }

    private sealed class Counters
    {
        public long Seen;
        public long Added;
        public long Moved;
        public long Unchanged;
        public long Errors;

        public void Add(Reconciled r)
        {
            Interlocked.Increment(ref Seen);
            switch (r)
            {
                case Reconciled.Added: Interlocked.Increment(ref Added); break;
                case Reconciled.Moved: Interlocked.Increment(ref Moved); break;
                default: Interlocked.Increment(ref Unchanged); break;
            }
        }
    }
}

public sealed record ScanOutcome(
    long RunId, long Seen, long Added, long Moved, long Unchanged,
    long MarkedGone, long Errors, bool Cancelled);
