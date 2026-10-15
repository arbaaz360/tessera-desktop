using System.Net.Http.Json;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Album covers: suggestions from the picker, and the choice a person makes.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape every worker integration takes (docs/contracts): Tessera
/// hands the worker content hashes, the worker answers in hashes, and Tessera
/// does the two things only it can - decide what the caller may see, and speak
/// the ids the web app holds. The picker knows nothing about albums, accounts
/// or this API, which is what makes it usable anywhere else.
/// </para>
/// <para>
/// The picker's answer is not stored. It is derived from evidence held
/// elsewhere and cheap to ask for again; a copy here would be a fourth place
/// the same fact could go stale. Only the CHOICE is stored, in
/// <c>album.cover_content_id</c>, by <see cref="AlbumRepository.SetCoverAsync"/>.
/// </para>
/// </remarks>
public static class CoverEndpoints
{
    public sealed record SetCover(string AssetId);

    /// <summary>Most the web app can usefully show; the picker caps at 200 anyway.</summary>
    private const int MaxTop = 200;

    public static void MapCovers(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/albums/{id}").WithTags("covers").RequireUser();

        g.MapGet("/cover-candidates", async (
            string id, int? top,
            CurrentUser me, AlbumRepository albums, IHttpClientFactory http,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is not { } album)
            {
                return Results.NotFound();
            }

            // A corpus is browsed, not curated: no thumbnails are made for it
            // and no covers are ranked for it (owner, 2026-09-11). The rule
            // lives on the account row, not here - this only asks.
            if (me.Account!.IsCorpus)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                                       title: "Cover suggestions are off for corpus accounts");
            }

            var want = Math.Clamp(top ?? 5, 1, MaxTop);
            var members = await albums.MemberHashesAsync(albumId, ct);
            var byHash = members.ToDictionary(m => Hashing.ToHex(m.Sha256), m => (m.ContentId, m.FileName), StringComparer.Ordinal);

            var log = logs.CreateLogger("Covers");
            PickerResult? picked;
            try
            {
                using var res = await http.CreateClient("cover-picker").PostAsJsonAsync(
                    "/rank", new { images = byHash.Keys, top = want }, ct);

                if (!res.IsSuccessStatusCode)
                {
                    log.CoverPickerStatus((int)res.StatusCode, albumId);
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                                           title: "The cover picker did not answer");
                }

                picked = await res.Content.ReadFromJsonAsync<PickerResult>(ct);
            }
            catch (HttpRequestException ex)
            {
                log.CoverPickerDown(ex);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                                       title: "The cover picker is not reachable");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout,
                                       title: "The cover picker timed out");
            }

            // The current cover, if it ranks at all, is shown first so the
            // person can see what they have next to what they might choose.
            // A pin, not a score: the picker never learns what is chosen.
            var ranked = (picked?.Candidates ?? [])
                .Where(c => byHash.ContainsKey(c.Id))
                .Select(c => (Candidate: c, byHash[c.Id].ContentId, byHash[c.Id].FileName))
                .OrderByDescending(c => c.ContentId == album.CoverContentId)
                .ToList();

            return Results.Ok(new
            {
                albumId = id,
                albumName = album.Name,
                currentCoverAssetId = album.CoverContentId is { } cur ? ImmichId.From(cur) : null,
                considered = picked?.Considered ?? 0,
                candidates = ranked.Select((c, i) => new
                {
                    rank = i + 1,
                    assetId = ImmichId.From(c.ContentId),
                    fileName = c.FileName,
                    faceId = c.Candidate.FaceId,
                    score = c.Candidate.Score,
                    flags = c.Candidate.Flags,
                    metrics = c.Candidate.Metrics,
                }),
            });
        });

        g.MapPut("/cover", async (
            string id, SetCover body,
            CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId))
            {
                return Results.NotFound();
            }

            if (!ImmichId.TryParse(body.AssetId, out var contentId))
            {
                return Results.Problem(statusCode: 400, title: "assetId is not an id this server issued");
            }

            if (!await albums.SetCoverAsync(me.AccountIdOrThrow, albumId, contentId, ct))
            {
                return Results.Problem(statusCode: 404,
                                       title: "Not your album, or that picture is not in it");
            }

            var album = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return Results.Ok(ImmichCompatEndpoints.Dto(album!));
        });
    }

    private sealed record PickerResult(int Considered, List<PickerCandidate> Candidates);

    // Metrics are the worker's to shape - numbers, names, nulls - and are
    // passed through to the web app untouched rather than re-typed here.
    private sealed record PickerCandidate(
        string Id, string FaceId, double Score, List<string> Flags, System.Text.Json.JsonElement Metrics);
}
