using Tessera.Api.Auth;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Operations on albums that only Tessera can answer: size, the cover choice,
/// and the overlap route the web app still calls.
/// </summary>
public static class AlbumOpsEndpoints
{
    public sealed record SetCover(string AssetId);

    public static void MapAlbumOps(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/albums").WithTags("albums").RequireUser();

        // Literal segment: routing prefers it over the compat layer's /{id}.
        // Overlap was "the same album name in another account"; there is one
        // account, so the honest answer is an empty list, not a 404 the app
        // would have to special-case.
        g.MapGet("/overlap", () => Results.Ok(Array.Empty<object>()));

        g.MapGet("/{id}/stats", async (string id, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!Api.TryParseId(id, out var albumId) ||
                await albums.StatsAsync(me.AccountIdOrThrow, albumId, ct) is not { } s)
            {
                return Api.NotFound();
            }

            return Results.Ok(new { bytes = s.Bytes, total = s.Total, images = s.Images, videos = s.Videos });
        });

        // A person's choice of cover. Final until the next choice; nothing
        // automatic overwrites it.
        g.MapPut("/{id}/cover", async (
            string id, SetCover body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!Api.TryParseId(id, out var albumId))
            {
                return Api.NotFound();
            }

            if (!Api.TryParseId(body.AssetId, out var contentId))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "assetId is not an id this server issued");
            }

            if (!await albums.SetCoverAsync(me.AccountIdOrThrow, albumId, contentId, ct))
            {
                return Api.NotFound("Not your album, or that picture is not in it");
            }

            var album = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return Results.Ok(ImmichCompatEndpoints.Dto(album!));
        });
    }
}
