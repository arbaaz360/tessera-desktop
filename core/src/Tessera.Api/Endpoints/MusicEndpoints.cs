using System.Net.Http.Headers;
using System.Net.Http.Json;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Music: what is playing in the caller's videos, and the queue that finds out.
/// </summary>
/// <remarks>
/// Everything here is answered from Tessera's own tables, in the ids the web
/// app holds. The worker is reached in exactly one place - identifying an
/// uploaded file, which is not in the library and so has no queue to sit in.
/// </remarks>
public static class MusicEndpoints
{
    public sealed record ScanRequest(string Mode);

    public static void MapMusic(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/music").WithTags("music").RequireUser();

        g.MapGet("/status", async (
            CurrentUser me, MusicRepository music, JobRepository jobs, IHttpClientFactory http, CancellationToken ct) =>
        {
            var s = await music.StatusAsync(me.AccountIdOrThrow, ct);
            var asked = s.Matched + s.NoMatch;
            return Results.Ok(new
            {
                videos = s.Videos,
                pending = s.Pending,
                processing = s.Processing,
                matched = s.Matched,
                noMatch = s.NoMatch,
                failed = s.Failed,
                never = s.Videos - asked - s.Pending - s.Processing - s.Failed,
                progressPercent = s.Videos == 0 ? 100 : Math.Round(100.0 * asked / s.Videos, 1),
                paused = await jobs.IsPausedAsync(JobNames.Music, ct),
                lastIdentifiedAt = s.LastIdentifiedAt,
                identifierAvailable = await WorkerUpAsync(http, ct),
            });
        });

        g.MapPost("/scan", async (ScanRequest body, CurrentUser me, MusicRepository music, CancellationToken ct) =>
        {
            var mode = (body.Mode ?? "missing").Trim().ToLowerInvariant();
            if (mode is not ("missing" or "all" or "no_match" or "failed"))
            {
                return Results.Problem(statusCode: 400, title: "mode must be missing, all, no_match or failed");
            }

            return Results.Ok(new { queued = await music.QueueAsync(me.AccountIdOrThrow, mode, ct), mode });
        });

        g.MapGet("/tracks/{key}/assets", async (
            string key, int? page, int? size, CurrentUser me, MusicRepository music, CancellationToken ct) =>
        {
            if (await music.TrackAsync(key, ct) is not { } track)
            {
                return Results.NotFound();
            }

            var p = Math.Max(1, page ?? 1);
            var n = Math.Clamp(size ?? 60, 1, 200);
            var (total, items) = await music.TrackVideosAsync(me.AccountIdOrThrow, key, p, n, ct);
            return Results.Ok(new
            {
                track = new { key = track.Key, track.Title, track.Artist, track.Album, artworkUrl = track.ArtworkUrl },
                total,
                page = p,
                size = n,
                items = items.Select(v => new
                {
                    id = ImmichId.From(v.ContentId),
                    fileName = v.FileName,
                    takenAt = v.TakenAt,
                    durationMs = v.DurationMs,
                }),
            });
        });

        // Not in the library, so not in the queue: the worker answers now.
        g.MapPost("/identify-upload", async (
            HttpRequest request, CurrentUser me, IHttpClientFactory http, MusicRepository music, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
            {
                return Results.Problem(statusCode: 400, title: "Send one file as multipart/form-data");
            }

            var file = request.Form.Files[0];
            using var form = new MultipartFormDataContent();
            await using var stream = file.OpenReadStream();
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            form.Add(part, "file", file.FileName);

            HttpResponseMessage res;
            try
            {
                res = await http.CreateClient("music-identifier").PostAsync("/identify-upload", form, ct);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                                       title: "The music identifier is not reachable");
            }

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                {
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                                           title: "The music identifier could not process the file");
                }

                var answer = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                var matched = answer.TryGetProperty("matched", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.True;
                if (!matched || !answer.TryGetProperty("track", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return Results.Ok(new { matched = false, track = (object?)null, sameTrackCount = 0L });
                }

                // The answer in the same shape as a library track, and how many
                // of the caller's videos already share it - the reason to ask.
                string? Str(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var key = Str("key") ?? "";
                var (count, _) = key.Length == 0 ? (0L, null) : await music.TrackVideosAsync(me.AccountIdOrThrow, key, 1, 1, ct);
                return Results.Ok(new
                {
                    matched = true,
                    track = new { key, title = Str("title") ?? "", artist = Str("artist") ?? "", album = Str("album"), artworkUrl = Str("artworkUrl") },
                    sameTrackCount = count,
                });
            }
        });

        // The viewer's questions, addressed by asset id like everything it holds.
        var assets = app.MapGroup("/api/assets/{id}").WithTags("music").RequireUser();

        assets.MapGet("/music", async (
            string id, CurrentUser me, ViewableContentRepository content, MusicRepository music, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId ||
                await content.GetAsync(me.AccountIdOrThrow, contentId, ct) is null ||
                await music.ForContentAsync(contentId, ct) is not { } m)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                assetId = ImmichId.From(m.ContentId),
                state = m.State,
                attempts = m.Attempts,
                error = m.Error,
                identifiedAt = m.IdentifiedAt,
                track = m.TrackKey is null ? null : new
                {
                    key = m.TrackKey, title = m.Title, artist = m.Artist, album = m.Album, artworkUrl = m.ArtworkUrl,
                },
                sameTrackCount = m.SameTrackCount,
            });
        });

        assets.MapPost("/identify", async (
            string id, CurrentUser me, ViewableContentRepository content, MusicRepository music, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId)
            {
                return Results.NotFound();
            }

            var queued = await music.QueueOneAsync(me.AccountIdOrThrow, contentId, ct);
            return Results.Ok(new { assetId = ImmichId.From(contentId), queued });
        });
    }

    private static async Task<bool> WorkerUpAsync(IHttpClientFactory http, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var res = await http.CreateClient("music-identifier").GetAsync("/health", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
