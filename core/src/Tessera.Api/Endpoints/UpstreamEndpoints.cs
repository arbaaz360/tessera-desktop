using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Search and faces, answered by the systems that own them.
/// </summary>
/// <remarks>
/// <para>
/// Tessera holds no vectors. Foundation owns every embedding and FaceSearch
/// owns every face, and both key on the SHA-256 that is also this database's
/// identity - so the join needs nothing but the hash.
/// </para>
/// <para>
/// These endpoints translate, they do not cache. A cache here would be a
/// fourth copy of a fact that already lives in two places, which is the exact
/// failure this design exists to remove.
/// </para>
/// </remarks>
public static class UpstreamEndpoints
{
    public static void MapUpstream(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/search").WithTags("search").RequireUser();

        // One search endpoint, two modes, the same filters on both:
        //   semantic (default)  Foundation ranks the library by the words; the
        //                       hits are filtered here by type and date.
        //   metadata            no embedding: the file NAME contains the text,
        //                       and/or the filters alone ("all videos from
        //                       2016"). Tessera's own tables answer it.
        // Filters are only the ones this library actually has: type and the
        // taken date. Camera, place and person do not exist here.
        g.MapGet("/", async (
            string? q, int? page, int? size, string? albumId, string? mode,
            string? type, DateTimeOffset? takenAfter, DateTimeOffset? takenBefore,
            CurrentUser me, Db db, AlbumRepository albums, IHttpClientFactory http,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Search");
            q = q?.Trim() ?? "";
            var metadata = string.Equals(mode, "metadata", StringComparison.OrdinalIgnoreCase);
            var filter = new SearchFilter(
                type?.ToUpperInvariant() switch { "IMAGE" => "image/", "VIDEO" => "video/", _ => null },
                takenAfter, takenBefore);

            var pageNo = Math.Max(1, page ?? 1);
            var pageSize = Math.Clamp(size ?? 100, 1, 500);

            // An album scope is the album's members BY HASH - Foundation ranks
            // exactly that set. Not a path prefix: Tessera spells the library
            // root one way and Foundation another, and a scope that crosses
            // that boundary was a 400. Hashes cross every boundary here.
            long? album = albumId is null ? null
                : ImmichId.TryParseAlbum(albumId, out var a) ? a
                : long.TryParse(albumId, out var n) ? n : null;
            if (album is { } scoped && await albums.GetAsync(me.AccountIdOrThrow, scoped, ct) is null)
            {
                return Results.NotFound();
            }

            if (metadata)
            {
                if (q.Length == 0 && album is null && !filter.Any)
                {
                    return Results.Problem(statusCode: 400, title: "Nothing to search for");
                }

                var listed = await ListByMetadataAsync(db, me.AccountIdOrThrow, q, album, filter, pageNo, pageSize, ct);
                return Results.Ok(new
                {
                    query = q,
                    page = pageNo,
                    size = pageSize,
                    count = listed.Count,
                    hasMore = listed.Count == pageSize,
                    results = listed.Select(id => new { assetId = ImmichId.From(id), score = 0.0 }),
                });
            }

            if (q.Length < 2)
            {
                return Results.Problem(statusCode: 400, title: "Nothing to search for");
            }

            // Foundation ranks the whole library; only this account's share of
            // it comes back (Indian holds about half). Over-fetch enough that a
            // page is full after that filter, growing with the page number, and
            // never past Foundation's own cap.
            var take = Math.Min(5000, pageNo * pageSize * 4);

            try
            {
                var client = http.CreateClient("foundation");
                using var res = album is { } scopedAlbum
                    ? await client.PostAsJsonAsync("/api/search/within", new
                        {
                            q,
                            k = take,
                            hashes = (await albums.MemberHashesAsync(scopedAlbum, ct)).Select(m => Hashing.ToHex(m.Sha256)),
                        }, ct)
                    : await client.GetAsync($"/api/search/immich?q={Uri.EscapeDataString(q)}&k={take}", ct);
                if (!res.IsSuccessStatusCode)
                {
                    log.SearchUpstreamStatus((int)res.StatusCode, q);
                    return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                                           title: "The search service did not answer");
                }

                var body = await res.Content.ReadFromJsonAsync<FoundationResult>(ct);
                var hits = body?.Results ?? [];

                // Foundation answers by content hash. Translate to the ids this
                // server issues, so the web app never learns another system's
                // identifiers - and never needs a translation table to render
                // what it was given.
                var resolved = await ResolveAsync(db, me.AccountIdOrThrow, hits, filter, ct);
                var slice = resolved.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();
                return Results.Ok(new
                {
                    query = q,
                    page = pageNo,
                    size = pageSize,
                    count = slice.Count,
                    // More is plausible while Foundation's cap has not been reached
                    // and the last over-fetch still filled the page.
                    hasMore = resolved.Count > pageNo * pageSize || (take < 5000 && hits.Count >= take),
                    results = slice.Select(r => new { assetId = ImmichId.From(r.ContentId), score = r.Score }),
                });
            }
            catch (HttpRequestException ex)
            {
                log.SearchUpstreamDown(ex);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                                       title: "The search service is not reachable");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout,
                                       title: "The search service timed out");
            }
        });
    }

    private sealed record FoundationResult(List<FoundationHit> Results);
    private sealed record FoundationHit(string Sha256, double Score);

    /// <summary>
    /// Turn upstream hashes into this database's content ids, keeping order.
    /// </summary>
    /// <remarks>
    /// Order is the ranking and must survive the translation, so results are
    /// re-sorted by their position in the input rather than by whatever order
    /// Postgres returns. Two hits with one hash - the same bytes filed twice -
    /// become one result, at the better position.
    ///
    /// Only content the caller holds a live copy of comes back: a search is
    /// over THEIR library, and Foundation indexes everyone's.
    /// </remarks>
    /// <summary>The filters this library can honour: kind of file, and when it was taken.</summary>
    private sealed record SearchFilter(string? MimePrefix, DateTimeOffset? TakenAfter, DateTimeOffset? TakenBefore)
    {
        public bool Any => MimePrefix is not null || TakenAfter is not null || TakenBefore is not null;

        /// <summary>The clause, over aliases <c>c</c> (content) and <c>l</c> (location).</summary>
        public const string Sql = """
              AND (@MimePrefix::text IS NULL OR c.mime LIKE @MimePrefix || '%')
              AND (@TakenAfter::timestamptz IS NULL OR COALESCE(c.taken_at, l.first_seen) >= @TakenAfter)
              AND (@TakenBefore::timestamptz IS NULL OR COALESCE(c.taken_at, l.first_seen) <= @TakenBefore)
            """;
    }

    /// <summary>
    /// Metadata mode: the account's live pictures whose file name contains the
    /// text, narrowed by album and filters, newest first, paged.
    /// </summary>
    private static async Task<List<long>> ListByMetadataAsync(
        Db db, long accountId, string q, long? albumId, SearchFilter filter, int page, int size, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<long>(new CommandDefinition(
            $"""
            SELECT t.content_id
              FROM timeline_entry t
              JOIN content c ON c.content_id = t.content_id
              JOIN LATERAL (
                   SELECT l.rel_path, l.first_seen FROM location l
                    WHERE l.content_id = t.content_id AND l.account_id = t.account_id
                      AND l.gone_at IS NULL AND l.trashed_at IS NULL
                    ORDER BY l.location_id LIMIT 1
              ) l ON true
             WHERE t.account_id = @AccountId
               AND (@Name::text = '' OR l.rel_path ILIKE '%' || @Name || '%')
               AND (@Album::bigint IS NULL OR EXISTS (
                    SELECT 1 FROM album_item ai WHERE ai.album_id = @Album AND ai.content_id = t.content_id))
              {SearchFilter.Sql}
             ORDER BY t.taken_at DESC, t.content_id DESC
             LIMIT @Take OFFSET @Skip
            """,
            new
            {
                AccountId = accountId, Name = q, Album = albumId,
                filter.MimePrefix, filter.TakenAfter, filter.TakenBefore,
                Take = size, Skip = (page - 1) * size,
            },
            cancellationToken: ct))).ToList();
    }

    private static async Task<List<(long ContentId, double Score)>> ResolveAsync(
        Db db, long accountId, List<FoundationHit> hits, SearchFilter filter, CancellationToken ct)
    {
        var wanted = new List<byte[]>(hits.Count);
        var order = new Dictionary<string, (int Index, double Score)>(hits.Count, StringComparer.Ordinal);
        foreach (var (hit, i) in hits.Select((h, i) => (h, i)))
        {
            if (hit.Sha256 is { } hex && Hashing.TryParseSha256(hex, out var sha) &&
                order.TryAdd(Hashing.ToHex(sha), (i, hit.Score)))
            {
                wanted.Add(sha);
            }
        }

        if (wanted.Count == 0)
        {
            return [];
        }

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(byte[] Sha, long ContentId)>(new CommandDefinition(
            $"""
            SELECT DISTINCT c.sha256, c.content_id
              FROM content c
              JOIN location l ON l.content_id = c.content_id
                             AND l.account_id = @AccountId AND l.gone_at IS NULL AND l.trashed_at IS NULL
             WHERE c.sha256 = ANY(@Hashes)
              {SearchFilter.Sql}
            """,
            new { Hashes = wanted.ToArray(), AccountId = accountId, filter.MimePrefix, filter.TakenAfter, filter.TakenBefore },
            cancellationToken: ct));

        return rows
            .Select(r => (r.ContentId, Pos: order[Hashing.ToHex(r.Sha)]))
            .OrderBy(r => r.Pos.Index)
            .Select(r => (r.ContentId, r.Pos.Score))
            .ToList();
    }
}
