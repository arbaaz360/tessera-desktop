using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Api.Workers;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

public static class ScanEndpoints
{
    /// <param name="AlbumFolder">
    /// Where this root's album folders sit, relative to the root: "" for the
    /// root's own children, a path for a subtree's children, null when
    /// folders under this root are not albums. See FolderRepository.
    /// </param>
    public sealed record NewRoot(long AccountId, string Label, string AbsPath, string[]? Exclude, string? AlbumFolder);

    public static void MapScanning(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/scan").WithTags("scan").RequireAdmin();

        g.MapPost("/roots", async (NewRoot body, Db db, CancellationToken ct) =>
        {
            var path = Core.PathRules.Canonical(body.AbsPath);
            if (!Directory.Exists(path))
            {
                // Refuse a root that is not mounted rather than accept it and
                // discover at scan time that an entire library looks empty.
                return Results.Problem(statusCode: 400,
                    title: "That path is not mounted in this container",
                    detail: $"{path} does not exist. Add the mount to compose.yaml first.");
            }

            await using var conn = await db.OpenAsync(ct);
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO scan_root (account_id, label, abs_path, exclude, album_folder)
                VALUES (@AccountId, @Label, @AbsPath, @Exclude, @AlbumFolder)
                ON CONFLICT (abs_path) DO UPDATE
                    SET label = EXCLUDED.label, account_id = EXCLUDED.account_id,
                        album_folder = EXCLUDED.album_folder
                RETURNING root_id
                """,
                new
                {
                    body.AccountId, body.Label, AbsPath = path, Exclude = body.Exclude ?? [],
                    AlbumFolder = body.AlbumFolder?.Trim('/', '\\').Replace('\\', '/'),
                },
                cancellationToken: ct));

            return Results.Ok(new { rootId = id, absPath = path });
        });

        // Deliberately a POST that returns immediately with the run id rather
        // than blocking for an hour: a scan of 479,046 files is not an HTTP
        // request. Progress is read from /api/admin/scans.
        g.MapPost("/roots/{rootId:long}/scan", (
            long rootId, string? folder, Scanner scanner, ILoggerFactory logs) =>
        {
            if (scanner.IsRunning)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "A scan is already running",
                    detail: "One heavy job at a time. Watch /api/admin/scans.");
            }

            var log = logs.CreateLogger("ScanEndpoint");

            // Detached on purpose, with its own lifetime: tying it to the
            // request would cancel the scan the moment the caller hangs up.
            _ = Task.Run(async () =>
            {
                try
                {
                    await scanner.ScanAsync(rootId, folder, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.ScanFailed(ex, rootId);
                }
            });

            return Results.Accepted($"/api/admin/scans", new { started = true, rootId, folder });
        });

        // What the watch has noticed and not yet scanned: folders still
        // settling, and folders too big to scan unasked, waiting for a person
        // to POST /roots/{rootId}/scan?folder=. Both are shown; the entry
        // count against autoScanMaxEntries says which is which.
        g.MapGet("/folders", async (FolderRepository folders, IOptions<WatchOptions> watch, CancellationToken ct) =>
        {
            var rows = await folders.WaitingAsync(ct);
            return Results.Ok(new
            {
                autoScanMaxEntries = watch.Value.AutoScanMaxEntries,
                settleSeconds = (int)watch.Value.Settle.TotalSeconds,
                folders = rows.Select(r => new
                {
                    r.RootId, r.RootLabel, r.AccountId, r.Folder, r.Entries,
                    firstSeen = r.FirstSeen, changedAt = r.ChangedAt,
                    waitingForYou = r.Entries > watch.Value.AutoScanMaxEntries,
                }),
            });
        });

        // The folder rule, applied to a whole root on request - what the
        // creators used to do every five minutes, now once, by hand, when the
        // albums and the folders have been apart (they were, from 2026-08-23
        // to 2026-09-12: the creators had died and nobody noticed).
        g.MapPost("/roots/{rootId:long}/folder-albums", async (long rootId, FolderRepository folders, CancellationToken ct) =>
        {
            var (albums, members) = await folders.ReconcileAlbumsAsync(rootId, null, ct);
            return Results.Ok(new { rootId, albumsCreated = albums, membersAdded = members });
        });

        // Queue thumbnails for everything that has none. Set-based, so asking
        // for a million is one statement.
        g.MapPost("/thumbnails/backfill", async (Db db, int? limit, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var queued = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO work_item (kind, content_id)
                SELECT 'thumbnail', c.content_id
                  FROM content c
                 WHERE c.mime LIKE 'image/%'
                   AND EXISTS (SELECT 1 FROM location l
                                WHERE l.content_id = c.content_id AND l.gone_at IS NULL)
                   AND NOT EXISTS (SELECT 1 FROM thumbnail t
                                    WHERE t.content_id = c.content_id AND t.kind = 'thumb')
                 LIMIT @Take
                ON CONFLICT DO NOTHING
                """,
                new { Take = Math.Clamp(limit ?? 100_000, 1, 5_000_000) },
                cancellationToken: ct));

            return Results.Ok(new { queued });
        });
    }
}
