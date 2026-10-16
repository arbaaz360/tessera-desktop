using Tessera.Api.Auth;
using Tessera.Api.Licence;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data.Repositories;
using Tessera.Desktop.Search;

namespace Tessera.Api.Endpoints;

/// <summary>
/// The Library page's routes (CONTRACT §4): roots, their numbers, scans,
/// duplicates. Every status answer carries counts, never a green tick: a
/// drift of 3 and a drift of 300,000 must not look the same.
/// </summary>
public static class LibraryEndpoints
{
    public sealed record NewRoot(string Path);

    public static void MapLibrary(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("library").RequireUser();

        g.MapGet("/", async (
            CurrentUser me, LibraryRepository library, JobRepository jobs, FreeTier tier,
            ScanRunner scans, LibraryWatch watch, HeaderBackfill backfill, IImageEmbedder embedder, CancellationToken ct) =>
        {
            var accountId = me.AccountIdOrThrow;
            var roots = await library.RootsAsync(accountId, ct);
            var runs = await library.LatestRunsAsync(ct);
            var thumbs = await jobs.ThumbnailCountsAsync(ct);
            var embeddings = await jobs.EmbeddingCountsAsync(embedder.ModelId, ct);
            var licence = await tier.StatusAsync(ct);
            var dates = await backfill.CountsAsync(runs.Values.Max(r => r.FinishedAt), ct);

            return Results.Ok(new
            {
                roots = roots.Select(r => RootDto(r, runs.GetValueOrDefault(r.RootId), scans.IsRootBusy(r.RootId))),
                thumbnails = new { pending = thumbs.Pending, done = thumbs.Done, failed = thumbs.Failed },
                embeddings = new
                {
                    pending = embeddings.Pending, done = embeddings.Done, failed = embeddings.Failed,
                    model = embedder.ModelId, ready = embedder.IsReady,
                },
                // Where the timeline's dates come from. pending + exif +
                // filename + mtime = contents with a live location.
                dates = new
                {
                    pending = dates.Pending, done = dates.Done,
                    exif = dates.Exif, filename = dates.FileName, mtime = dates.Mtime,
                    unreadable = backfill.Unreadable,
                },
                licence = LicenceDto(licence),
                watch = new { running = watch.Running, lastTick = Api.Iso(watch.LastTick) },
            });
        });

        g.MapPost("/roots", async (
            NewRoot body, CurrentUser me, LibraryRepository library, ScanRunner scans, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "path is required");
            }

            string canonical;
            try
            {
                canonical = PathRules.Canonical(System.IO.Path.GetFullPath(body.Path.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Api.Fail(StatusCodes.Status400BadRequest, $"Not a usable path: {ex.Message}");
            }

            if (!Directory.Exists(canonical))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, $"{canonical} is not a directory");
            }

            var (conflict, other) = await library.ConflictAsync(canonical, ct);
            if (conflict != RootConflict.None)
            {
                return Api.Fail(StatusCodes.Status409Conflict, conflict switch
                {
                    RootConflict.AlreadyARoot => $"{canonical} is already a root",
                    RootConflict.InsideARoot => $"{canonical} is inside the root {other}",
                    _ => $"{canonical} contains the root {other}; remove that one first",
                });
            }

            var label = System.IO.Path.GetFileName(canonical.TrimEnd('/'));
            if (string.IsNullOrEmpty(label))
            {
                label = canonical;   // a drive root
            }

            var rootId = await library.AddRootAsync(me.AccountIdOrThrow, canonical, label, ct);

            // The buyer chose the folder: that is the human start the scan
            // waits for, so it begins now.
            await scans.EnqueueAsync(rootId, null, ct);

            var summary = (await library.RootsAsync(me.AccountIdOrThrow, ct)).Single(r => r.RootId == rootId);
            var run = (await library.LatestRunsAsync(ct)).GetValueOrDefault(rootId);
            return Results.Created($"/api/library/roots/{rootId}", RootDto(summary, run, scanning: true));
        });

        g.MapDelete("/roots/{rootId:long}", async (
            long rootId, CurrentUser me, LibraryRepository library, ScanRunner scans, CancellationToken ct) =>
        {
            if (await library.RootAsync(rootId, ct) is null)
            {
                return Api.NotFound($"No root {rootId}");
            }

            if (scans.IsRootBusy(rootId))
            {
                return Api.Fail(StatusCodes.Status409Conflict, "A scan of that root is running; try again when it finishes");
            }

            return await library.RemoveRootAsync(me.AccountIdOrThrow, rootId, ct)
                ? Results.NoContent()
                : Api.NotFound($"No root {rootId}");
        });

        g.MapPost("/roots/{rootId:long}/scan", async (
            long rootId, LibraryRepository library, ScanRunner scans, CancellationToken ct) =>
        {
            if (await library.RootAsync(rootId, ct) is null)
            {
                return Api.NotFound($"No root {rootId}");
            }

            if (scans.IsRootScanning(rootId))
            {
                return Api.Fail(StatusCodes.Status409Conflict, "A scan of that root is already queued or running");
            }

            var runId = await scans.EnqueueAsync(rootId, null, ct);
            return runId is null
                ? Api.Fail(StatusCodes.Status409Conflict, "A scan of that root is already queued or running")
                : Results.Accepted("/api/library", new { runId });
        });

        g.MapGet("/duplicates", async (int? limit, CurrentUser me, LibraryRepository library, CancellationToken ct) =>
        {
            var sets = await library.DuplicatesAsync(me.AccountIdOrThrow, limit ?? 200, ct);
            return Results.Ok(sets.Select(s => new
            {
                contentId = s.ContentId,
                sha256 = Hashing.ToHex(s.Sha256),
                byteSize = s.ByteSize,
                locations = s.Locations.Select(l => new { id = l.LocationId, rootId = l.RootId, relPath = l.RelPath }),
            }));
        });
    }

    internal static object RootDto(RootSummary r, ScanRun? last, bool scanning) => new
    {
        id = r.RootId,
        path = r.AbsPath,
        label = r.Label,
        enabled = r.Enabled,
        counts = new { files = r.Files, contents = r.Contents, trashed = r.Trashed },
        lastScan = last is null ? null : new
        {
            runId = last.RunId,
            startedAt = Api.Iso(last.StartedAt),
            finishedAt = Api.Iso(last.FinishedAt),
            seen = last.FilesSeen,
            added = last.Added,
            moved = last.Moved,
            copies = last.Copies,
            unchanged = last.Unchanged,
            gone = last.MarkedGone,
            errors = last.Errors,
            errorSample = last.ErrorSample,
            cancelled = last.Cancelled,
        },
        scanning,
    };

    internal static object LicenceDto(LicenceStatus s) => new
    {
        state = s.State,
        cap = s.Cap,
        indexed = s.Indexed,
        capReached = s.CapReached,
    };
}
