using System.Globalization;
using Tessera.Api.Auth;
using Tessera.Api.Search;
using Tessera.Api.Workers;
using Tessera.Data;
using Tessera.Data.Repositories;
using Tessera.Desktop.Search;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Search, answered locally (CONTRACT §4).
/// </summary>
/// <remarks>
/// The query parameter names are the ones the web app sends today
/// (<c>web/src/features/search/searchParams.ts</c>): <c>q</c>,
/// <c>albumId</c>, <c>mode=metadata</c>, <c>type</c>, <c>takenAfter</c>,
/// <c>takenBefore</c>, <c>page</c>, <c>size</c>; plus <c>limit</c>. The
/// answer is <c>{"items": [asset + score], "model"}</c>, where each item is
/// exactly a timeline bucket entry so the grid renders it unchanged.
/// </remarks>
public static class SearchEndpoints
{
    public sealed record SearchSettingsBody(bool? Enabled);

    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/search").WithTags("search").RequireUser();

        g.MapGet("/", async (
            string? q, string? albumId, string? mode, string? type, string? takenAfter, string? takenBefore,
            int? page, int? size, int? limit,
            CurrentUser me, SearchService search, IImageEmbedder embedder, SettingsRepository settings,
            Db db, CancellationToken ct) =>
        {
            if (!TryQuery(q, albumId, type, takenAfter, takenBefore, page, size, limit, out var query, out var problem))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, problem!);
            }

            var metadata = string.Equals(mode, "metadata", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<SearchHit> hits;
            string model;
            if (metadata)
            {
                hits = await search.MetadataAsync(me.AccountIdOrThrow, query, ct);
                model = "metadata";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(query.Text))
                {
                    return Api.Fail(StatusCodes.Status400BadRequest, "q is required unless mode=metadata");
                }

                if (!(await settings.ReadAsync(ct)).SearchEnabled)
                {
                    return Api.Fail(StatusCodes.Status503ServiceUnavailable, "search is switched off");
                }

                if (!embedder.IsReady)
                {
                    return Api.Fail(StatusCodes.Status503ServiceUnavailable, "model not ready");
                }

                hits = await search.TextAsync(me.AccountIdOrThrow, query, ct);
                model = embedder.ModelId;
            }

            return Results.Ok(new { items = await ItemsAsync(db, me.AccountIdOrThrow, hits, ct), model });
        });

        g.MapGet("/similar/{contentId:long}", async (
            long contentId, string? albumId, string? type, string? takenAfter, string? takenBefore,
            int? page, int? size, int? limit,
            CurrentUser me, SearchService search, IImageEmbedder embedder, SettingsRepository settings,
            Db db, CancellationToken ct) =>
        {
            if (!TryQuery(null, albumId, type, takenAfter, takenBefore, page, size, limit, out var query, out var problem))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, problem!);
            }

            if (!(await settings.ReadAsync(ct)).SearchEnabled)
            {
                return Api.Fail(StatusCodes.Status503ServiceUnavailable, "search is switched off");
            }

            if (!embedder.IsReady)
            {
                return Api.Fail(StatusCodes.Status503ServiceUnavailable, "model not ready");
            }

            var hits = await search.SimilarAsync(me.AccountIdOrThrow, contentId, query, ct);
            if (hits is null)
            {
                return Api.NotFound("That picture has no thumbnail to compare from yet");
            }

            return Results.Ok(new { items = await ItemsAsync(db, me.AccountIdOrThrow, hits, ct), model = embedder.ModelId });
        });

        g.MapGet("/status", async (
            IImageEmbedder embedder, ModelReadiness model, JobRepository jobs, SettingsRepository settings,
            EmbeddingWorker worker, CancellationToken ct) =>
        {
            var counts = await jobs.EmbeddingCountsAsync(embedder.ModelId, ct);
            var s = await settings.ReadAsync(ct);
            return Results.Ok(StatusDto(embedder, model, counts, s.SearchEnabled, worker.Waiting));
        });

        // Idempotent: a second call joins the download in flight.
        g.MapPost("/model/download", (ModelReadiness model) =>
        {
            _ = model.EnsureAsync();
            return Results.Accepted("/api/search/status", new { started = true, downloading = model.Downloading });
        });

        g.MapPut("/settings", async (
            SearchSettingsBody body, SettingsRepository settings, IImageEmbedder embedder, ModelReadiness model,
            JobRepository jobs, EmbeddingWorker worker, CancellationToken ct) =>
        {
            if (body.Enabled is { } enabled)
            {
                await settings.SetSearchEnabledAsync(enabled, ct);
            }

            var counts = await jobs.EmbeddingCountsAsync(embedder.ModelId, ct);
            var s = await settings.ReadAsync(ct);
            return Results.Ok(StatusDto(embedder, model, counts, s.SearchEnabled, worker.Waiting));
        });
    }

    private static object StatusDto(IImageEmbedder embedder, ModelReadiness model, DerivedCounts counts, bool enabled, string? waiting) => new
    {
        model = embedder.ModelId,
        ready = embedder.IsReady,
        downloading = model.Progress is { } p
            ? new { receivedBytes = p.ReceivedBytes, totalBytes = p.TotalBytes, file = p.File }
            : null,
        embedded = counts.Done,
        total = counts.Done + counts.Pending,
        pending = counts.Pending,
        failed = counts.Failed,
        enabled,
        device = embedder.Device switch
        {
            EmbedDevice.DirectML => "directml",
            EmbedDevice.CoreML => "coreml",
            _ => "cpu",
        },
        waiting,
        error = model.LastError,
    };

    private static async Task<IEnumerable<object>> ItemsAsync(Db db, long accountId, IReadOnlyList<SearchHit> hits, CancellationToken ct)
    {
        var rows = await AssetShapes.LiveRowsAsync(db, accountId, hits.Select(h => h.ContentId).ToList(), ct);
        var items = new List<object>(hits.Count);
        foreach (var hit in hits)
        {
            if (rows.TryGetValue(hit.ContentId, out var row))
            {
                items.Add(AssetShapes.Item(row, accountId, trashed: false, hit.Score));
            }
        }

        return items;
    }

    private static bool TryQuery(
        string? q, string? albumId, string? type, string? takenAfter, string? takenBefore,
        int? page, int? size, int? limit, out SearchQuery query, out string? problem)
    {
        query = null!;
        problem = null;

        long? album = null;
        if (!string.IsNullOrEmpty(albumId))
        {
            if (!Api.TryParseId(albumId, out var id))
            {
                problem = "albumId is not an id this server issued";
                return false;
            }

            album = id;
        }

        var mime = type?.ToUpperInvariant() switch { "IMAGE" => "image/", "VIDEO" => "video/", null or "" => null, _ => "?" };
        if (mime == "?")
        {
            problem = "type must be IMAGE or VIDEO";
            return false;
        }

        if (!TryTime(takenAfter, out var after) || !TryTime(takenBefore, out var before))
        {
            problem = "takenAfter and takenBefore must be ISO 8601 times";
            return false;
        }

        var pageSize = Math.Clamp(limit ?? size ?? 100, 1, 500);
        var pageNo = Math.Max(1, page ?? 1);
        query = new SearchQuery(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), album, mime, after, before, pageSize, (pageNo - 1) * pageSize);
        return true;
    }

    private static bool TryTime(string? s, out DateTimeOffset? t)
    {
        t = null;
        if (string.IsNullOrWhiteSpace(s))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        t = parsed;
        return true;
    }
}
