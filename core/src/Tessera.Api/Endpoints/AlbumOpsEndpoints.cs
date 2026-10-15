using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Operations on albums that only Tessera can answer: size, the cross-account
/// overlap, and moving an album to another account.
/// </summary>
/// <remarks>
/// These lived in the cover-picker sidecar for one reason: it held a database
/// connection that saw every account, which Immich's own API never offered.
/// They are questions about Tessera's own tables and nothing else - no
/// evidence from any other system, nothing to compute - so they are plain
/// endpoints here, not a worker.
/// </remarks>
public static class AlbumOpsEndpoints
{
    public sealed record MoveRequest(string OwnerId);

    public static void MapAlbumOps(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/albums").WithTags("albums").RequireUser();

        // Literal segment: routing prefers it over the compat layer's /{id}.
        g.MapGet("/overlap", async (CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            var rows = await albums.OverlapAsync(me.AccountIdOrThrow, ct);
            return Results.Ok(rows.Select(r => new
            {
                name = r.Name,
                mine = new { albumId = ImmichId.Album(r.MyAlbumId), images = r.MyImages, videos = r.MyVideos },
                other = new
                {
                    ownerId = ImmichId.Account(r.OtherAccountId),
                    albumId = ImmichId.Album(r.OtherAlbumId),
                    images = r.OtherImages,
                    videos = r.OtherVideos,
                },
            }));
        });

        g.MapGet("/{id}/stats", async (string id, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await albums.StatsAsync(me.AccountIdOrThrow, albumId, ct) is not { } s)
            {
                return Results.NotFound();
            }

            return Results.Ok(new { bytes = s.Bytes, total = s.Total, images = s.Images, videos = s.Videos });
        });

        g.MapPost("/{id}/move", async (
            string id, MoveRequest body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId))
            {
                return Results.NotFound();
            }

            if (!ImmichId.TryParseAccount(body.OwnerId, out var dest))
            {
                return Results.Problem(statusCode: 400, title: "ownerId is not an account id this server issued");
            }

            var result = await albums.MoveAsync(me.AccountIdOrThrow, albumId, dest, ct);
            if (result is null)
            {
                return Results.Problem(statusCode: 404,
                                       title: "Not your album, or the destination is not another personal account");
            }

            return Results.Ok(new
            {
                albumId = ImmichId.Album(result.AlbumId),
                merged = result.Merged,
                moved = result.Moved,
            });
        });
    }
}
