using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Indexing an album's folder: Tessera's own scan, then faces and images.
/// </summary>
/// <remarks>
/// <para>
/// Two other services take part - FaceSearch indexes faces, Foundation
/// registers images - and the web app used to talk to both itself, each by
/// its own address and in its own spelling of a path. Under the house
/// pattern it talks to Tessera, and Tessera talks to them.
/// </para>
/// <para>
/// The folder is handed over in the HOST's spelling (<c>X:/Immich/…</c>),
/// which every service's own mount map translates. Tessera's container path
/// for the same folder (<c>/library/…</c>) is meaningful to nobody else - a
/// prefix in it crossed to Foundation once and came back as a 400. The host
/// spellings come from the registry that declares each root once
/// (<c>homelab.env</c>), via <see cref="RootOptions"/>.
/// </para>
/// </remarks>
public static class AlbumIndexEndpoints
{
    public static void MapAlbumIndex(this IEndpointRouteBuilder app)
    {
        // Faces into FaceSearch, images into Foundation. Pressing Index is
        // the human start for the face ingest, agreed 2026-09-04; FaceSearch
        // still refuses a second concurrent run.
        app.MapPost("/api/albums/{id}/index", async (
            string id, CurrentUser me, Db db, IOptions<RootOptions> roots,
            Scanner scanner, Indexing indexing, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await AlbumFolderAsync(db, me.AccountIdOrThrow, albumId, ct) is not { } where)
            {
                return Results.NotFound();
            }

            var containerFolder = where.Folder is null ? where.Root : $"{where.Root}/{where.Folder}";
            if (roots.Value.ToHostPath(containerFolder) is not { } hostFolder)
            {
                return Results.Problem(statusCode: 500,
                                       title: $"No host spelling is configured for the root of {containerFolder}");
            }

            // First Tessera's own view: walk just this folder so the new files
            // are enrolled, hashed and in the album. A whole-root walk would
            // read half a million files to notice eighty.
            object scan;
            try
            {
                var outcome = await scanner.ScanAsync(where.RootId, where.Folder, ct);
                scan = new { ok = true, outcome.Seen, outcome.Added, outcome.Moved, gone = outcome.MarkedGone, outcome.Errors };
            }
            catch (InvalidOperationException ex)
            {
                // Another scan holds the disk; the folder can be walked later.
                scan = new { ok = false, detail = ex.Message };
            }

            // The same two calls a completed scan makes, plus the start: pressing
            // Index IS the human start for this album's faces (decided
            // 2026-09-04). The vectors follow when the embeddings switch on the
            // jobs page is next turned on.
            var requestId = await indexing.RequestFacesAsync(hostFolder, "tessera-album-index", ct);
            var (started, queuedBehindAnother) = requestId is { } rid
                ? await indexing.StartFacesAsync(rid, ct)
                : (false, false);
            var registered = await indexing.RegisterAsync(hostFolder, ct);
            var runner = await indexing.RunnerAsync(ct);

            return Results.Ok(new
            {
                folder = hostFolder,
                scan,
                faces = new { ok = requestId is not null, requestId, started, queuedBehindAnother },
                images = new
                {
                    ok = registered is not null,
                    result = new
                    {
                        registered,
                        pending_vectors = runner?.Pending,
                        // What makes the vectors: the embeddings switch on the
                        // jobs page, not a command any more.
                        run = runner is { Pending: > 0 } ? "Jobs → Embeddings → Run missing" : null,
                    },
                },
            });
        }).WithTags("albums").RequireUser();
    }

    private readonly record struct AlbumPlace(long RootId, string Root, string? Folder);

    /// <summary>Where an album's files live: the root, and the folder under it.</summary>
    private static async Task<AlbumPlace?> AlbumFolderAsync(Db db, long accountId, long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long RootId, string Root, string Rel)?>(new CommandDefinition(
            """
            SELECT r.root_id, r.abs_path, l.rel_path
              FROM album_item i
              JOIN album a     ON a.album_id = i.album_id AND a.account_id = @AccountId
              JOIN location l  ON l.content_id = i.content_id AND l.gone_at IS NULL
              JOIN scan_root r ON r.root_id = l.root_id
             WHERE i.album_id = @AlbumId
             LIMIT 1
            """,
            new { AccountId = accountId, AlbumId = albumId }, cancellationToken: ct));
        return row is null ? null : new AlbumPlace(row.Value.RootId, row.Value.Root, PathRules.TopFolder(row.Value.Rel));
    }
}
