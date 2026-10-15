using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// The API the existing web app already speaks, served from Tessera's data.
/// </summary>
/// <remarks>
/// <para>
/// The web app is generated against Immich's SDK, so its request and response
/// shapes are not negotiable. Rather than edit an app another session has
/// uncommitted work in, the contract is implemented HERE - the frontend needs
/// a different base URL and not one line of code.
/// </para>
/// <para>
/// This is a translation layer and nothing else. It owns no data, caches
/// nothing, and every route below is a rename of something Tessera already
/// answers. When the frontend is eventually written against Tessera's own
/// endpoints, this file is deleted and nothing else changes.
/// </para>
/// <para>
/// Tessera's native endpoints keep working, under <c>/api/native</c>. The
/// compatible surface takes the plain <c>/api</c> paths because that is the
/// one thing the web app cannot be flexible about.
/// </para>
/// </remarks>
public static class ImmichCompatEndpoints
{
    public static void MapImmichCompat(this IEndpointRouteBuilder app)
    {
        MapTimeline(app);
        MapAssets(app);
        MapTrash(app);
        MapAlbums(app);
        MapUsers(app);
    }

    // ------------------------------------------------------------- timeline

    /// <summary>
    /// The grid. Buckets are months, and a bucket's contents come back as
    /// PARALLEL ARRAYS rather than objects.
    /// </summary>
    /// <remarks>
    /// The columnar shape is Immich's and it is the right call at this size: a
    /// bucket of 40,000 photos is ~15 field names repeated 40,000 times as
    /// objects, against fifteen arrays. It is the difference between a few
    /// hundred KB and several MB on one scroll.
    /// </remarks>
    private static void MapTimeline(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/timeline").WithTags("immich-compat").RequireUser();

        // Every view of the library is the timeline with a filter: an album,
        // the trash, favourites, photos-only, oldest-first. The web app asks
        // these two endpoints for all of them; a filter this layer ignored
        // showed the whole library under a heading that said otherwise.
        g.MapGet("/buckets", async (
            string? albumId, bool? isTrashed, bool? isFavorite, string? assetType, string? order,
            CurrentUser me, Db db, CancellationToken ct) =>
        {
            var f = TimelineFilter.From(albumId, isTrashed, isFavorite, assetType, order);

            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<(DateTimeOffset Month, int Count)>(new CommandDefinition(
                $"""
                {f.Source}
                SELECT date_trunc('month', t.taken_at) AS month, count(*)::int AS count
                  FROM src t
                  JOIN content c ON c.content_id = t.content_id
                 WHERE {TimelineFilter.Where}
                 GROUP BY 1
                 ORDER BY 1 {f.Direction}
                """,
                f.Parameters(me.AccountIdOrThrow),
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new
            {
                timeBucket = r.Month.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                count = r.Count,
            }));
        });

        g.MapGet("/bucket", async (
            string timeBucket, string? albumId, bool? isTrashed, bool? isFavorite, string? assetType, string? order,
            CurrentUser me, Db db, CancellationToken ct) =>
        {
            var f = TimelineFilter.From(albumId, isTrashed, isFavorite, assetType, order);

            if (!DateTimeOffset.TryParse(
                    timeBucket, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
            {
                return Results.Problem(statusCode: 400, title: "timeBucket must be YYYY-MM-DD");
            }

            var end = start.AddMonths(1);

            await using var conn = await db.OpenAsync(ct);
            var rows = (await conn.QueryAsync<BucketRow>(new CommandDefinition(
                $"""
                {f.Source}
                SELECT t.content_id AS ContentId,
                       COALESCE(c.width, th.width) AS Width, COALESCE(c.height, th.height) AS Height,
                       c.mime AS Mime, c.duration_ms AS DurationMs,
                       t.taken_at AS TakenAt, c.first_seen AS FirstSeen,
                       EXISTS (SELECT 1 FROM favorite fv
                                WHERE fv.account_id = @Account AND fv.content_id = t.content_id) AS IsFavorite
                  FROM src t
                  JOIN content c ON c.content_id = t.content_id
                  -- The grid only needs the RATIO. Most content rows came
                  -- from Immich without dimensions; the thumbnail was resized
                  -- proportionally, so its row carries the same ratio.
                  LEFT JOIN thumbnail th ON th.content_id = t.content_id AND th.kind = 'thumb'
                 WHERE {TimelineFilter.Where}
                   AND t.taken_at >= @Start AND t.taken_at < @End
                 ORDER BY t.taken_at {f.Direction}, t.content_id {f.Direction}
                """,
                f.Parameters(me.AccountIdOrThrow, start, end),
                cancellationToken: ct))).ToList();

            var owner = ImmichId.Account(me.AccountIdOrThrow);
            var n = rows.Count;

            return Results.Ok(new
            {
                id = rows.Select(r => ImmichId.From(r.ContentId)).ToArray(),
                ownerId = Enumerable.Repeat(owner, n).ToArray(),
                ratio = rows.Select(Ratio).ToArray(),
                isFavorite = rows.Select(r => r.IsFavorite).ToArray(),
                visibility = Enumerable.Repeat("timeline", n).ToArray(),
                isTrashed = Enumerable.Repeat(f.Trashed, n).ToArray(),
                isImage = rows.Select(r => r.Mime.StartsWith("image/", StringComparison.Ordinal)).ToArray(),
                thumbhash = new string?[n],
                createdAt = rows.Select(r => Iso(r.FirstSeen)).ToArray(),
                fileCreatedAt = rows.Select(r => Iso(r.TakenAt)).ToArray(),
                localOffsetHours = new double[n],
                duration = rows.Select(r => r.DurationMs).ToArray(),
                projectionType = new string?[n],
                livePhotoVideoId = new string?[n],
            });
        });
    }

    /// <summary>
    /// The timeline's filters, turned into one SQL source and one WHERE.
    /// </summary>
    /// <remarks>
    /// Live pictures come from <c>timeline_entry</c>, which the trigger keeps
    /// equal to "has a location that is neither trashed nor gone". Trashed
    /// pictures are exactly the ones that table has dropped, so the trash view
    /// reads <c>location</c> directly. Everything else - album, favourite,
    /// photos or videos, direction - is the same clause on either source.
    /// </remarks>
    private sealed record TimelineFilter(
        bool Trashed, bool FavoritesOnly, string? MimePrefix, long? AlbumId, bool Ascending)
    {
        public static TimelineFilter From(string? albumId, bool? isTrashed, bool? isFavorite, string? assetType, string? order)
        {
            var scope = AlbumScope.Parse(albumId);
            return new TimelineFilter(
                Trashed: isTrashed == true,
                FavoritesOnly: isFavorite == true,
                MimePrefix: assetType?.ToUpperInvariant() switch { "IMAGE" => "image/", "VIDEO" => "video/", _ => null },
                AlbumId: scope.Requested ? scope.AlbumId : null,
                Ascending: string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase));
        }

        public string Direction => Ascending ? "ASC" : "DESC";

        public string Source => Trashed
            ? """
              WITH src AS (
                  SELECT l.account_id, l.content_id, COALESCE(c0.taken_at, l.first_seen) AS taken_at
                    FROM location l
                    JOIN content c0 ON c0.content_id = l.content_id
                   WHERE l.account_id = @Account AND l.trashed_at IS NOT NULL AND l.gone_at IS NULL
                   GROUP BY l.account_id, l.content_id, c0.taken_at, l.first_seen
              )
              """
            : AlbumId is not null
                ? AlbumSource
                : """
                  WITH src AS (
                      SELECT t.account_id, t.content_id, t.taken_at
                        FROM timeline_entry t
                       WHERE t.account_id = @Account
                  )
                  """;

        /// <summary>
        /// An album's pictures: every member the viewer may see, wherever its file lives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The album view used to read the viewer's own timeline and keep the album's
        /// members found there. timeline_entry only holds pictures whose files sit
        /// under that account's roots, so an album moved here from another account
        /// (a move changes the owner and leaves the files where they are), or a
        /// picture pulled into a personal album from a corpus, came back empty - while
        /// <see cref="ViewableContentRepository"/> already let the owner open every one
        /// of those pictures. The grid and the permission rule now agree: owning the
        /// album is enough.
        /// </para>
        /// <para>
        /// Two branches. Members live in the viewer's own library come from their
        /// timeline row, exactly as before. The rest come from any account that still
        /// has a live copy - unless the viewer holds a non-gone copy themselves, which
        /// means they trashed it, and trash keeps hiding it. A copy of theirs that is
        /// gone falls through to whoever still has the file. The NOT EXISTS on the
        /// viewer's timeline goes first so the second branch only does work for the
        /// few members that need it (152k-item album: 360 ms warm, against 195 ms
        /// for the old view that hid the moved members).
        /// </para>
        /// <para>
        /// Album ownership is still enforced by <see cref="Where"/>; this source only
        /// decides which members appear.
        /// </para>
        /// </remarks>
        private const string AlbumSource = """
            WITH src AS (
                SELECT t.account_id, t.content_id, t.taken_at
                  FROM album_item ai
                  JOIN timeline_entry t ON t.account_id = @Account AND t.content_id = ai.content_id
                 WHERE ai.album_id = @Album
                UNION ALL
                SELECT @Account::bigint, ai.content_id, COALESCE(c0.taken_at, live.first_seen)
                  FROM album_item ai
                  JOIN content c0 ON c0.content_id = ai.content_id
                  JOIN LATERAL (
                       SELECT min(l.first_seen) AS first_seen
                         FROM location l
                        WHERE l.content_id = ai.content_id AND l.gone_at IS NULL AND l.trashed_at IS NULL
                  ) live ON live.first_seen IS NOT NULL
                 WHERE ai.album_id = @Album
                   AND NOT EXISTS (SELECT 1 FROM timeline_entry tv
                                    WHERE tv.account_id = @Account AND tv.content_id = ai.content_id)
                   AND NOT EXISTS (SELECT 1 FROM location lv
                                    WHERE lv.account_id = @Account AND lv.content_id = ai.content_id
                                      AND lv.gone_at IS NULL)
            )
            """;

        public static string Where => """
            t.account_id = @Account
               AND (@Album::bigint IS NULL OR EXISTS (
                    SELECT 1 FROM album_item ai
                      JOIN album al ON al.album_id = ai.album_id
                     WHERE ai.content_id = t.content_id
                       AND ai.album_id = @Album
                       AND al.account_id = @Account))
               AND (NOT @FavoritesOnly OR EXISTS (
                    SELECT 1 FROM favorite fv
                     WHERE fv.account_id = @Account AND fv.content_id = t.content_id))
               AND (@MimePrefix::text IS NULL OR c.mime LIKE @MimePrefix || '%')
            """;

        public object Parameters(long account, DateTimeOffset? start = null, DateTimeOffset? end = null) => new
        {
            Account = account,
            Album = AlbumId,
            FavoritesOnly,
            MimePrefix,
            Start = start,
            End = end,
        };
    }

    // ---------------------------------------------------------------- trash

    private static void MapTrash(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/trash").WithTags("immich-compat").RequireUser();

        g.MapPost("/restore/assets", async (
            BulkIdsBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            var ids = ParseContentIds(body.Ids).ToArray();
            await using var conn = await db.OpenAsync(ct);
            var n = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE location SET trashed_at = NULL
                 WHERE account_id = @Account AND content_id = ANY(@Ids::bigint[]) AND trashed_at IS NOT NULL
                """,
                new { Account = me.AccountIdOrThrow, Ids = ids },
                cancellationToken: ct));
            return Results.Ok(new { count = n });
        });

        g.MapPost("/restore", async (CurrentUser me, Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var n = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE location SET trashed_at = NULL WHERE account_id = @Account AND trashed_at IS NOT NULL",
                new { Account = me.AccountIdOrThrow }, cancellationToken: ct));
            return Results.Ok(new { count = n });
        });

        // There is no emptying. The originals are the thing this whole system
        // protects; the trash is a list, and the only way out of it is back.
        g.MapPost("/empty", () => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Originals are never deleted here. Restore what you want back and leave the rest hidden."));

        // What the web app reads before drawing the trash page. trashDays = 0
        // is Immich's "never purged", which is the truth here.
        app.MapGet("/api/server/config", () => Results.Ok(new
        {
            trashDays = 0,
            userDeleteDelay = 0,
            loginPageMessage = "",
            oauthButtonText = "",
            isInitialized = true,
            isOnboarded = true,
            externalDomain = "",
            mapDarkStyleUrl = "",
            mapLightStyleUrl = "",
            publicUsers = false,
        })).WithTags("immich-compat").RequireUser();
    }

    public sealed record AssetBulkUpdateBody(string[] Ids, bool? IsFavorite);
    public sealed record AssetBulkDeleteBody(string[] Ids, bool? Force);

    // --------------------------------------------------------------- assets

    private static void MapAssets(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").WithTags("immich-compat").RequireUser();

        // The grid asks for this once per tile, so it is the hottest route in
        // the product. There is ONE derived size (the owner's rule: thumbnails
        // and originals, nothing between); Immich's `size=` parameter is
        // accepted and ignored so old links keep working.
        g.MapGet("/{id}/thumbnail", async (
            string id, CurrentUser me, ViewableContentRepository content,
            IOptions<StorageOptions> storage, HttpContext ctx, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId ||
                await content.GetAsync(me.AccountIdOrThrow, contentId, ct) is not { } row)
            {
                return Results.NotFound();
            }

            var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, row.Sha, "thumb", "webp");
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, "image/webp");
        });

        g.MapGet("/{id}/original", async (
            string id, CurrentUser me, ViewableContentRepository content, HttpContext ctx, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } cid ||
                await content.GetAsync(me.AccountIdOrThrow, cid, ct) is not { AbsolutePath: { } abs } row)
            {
                return Results.NotFound();
            }

            if (!File.Exists(abs))
            {
                return Results.Problem(statusCode: StatusCodes.Status410Gone,
                                       title: "The file has moved or been removed since the last scan");
            }

            if (row.Width is null && row.Mime.StartsWith("image/", StringComparison.Ordinal))
            {
                // Header only - SkiaSharp reads the dimensions without
                // decoding pixels - and only for pictures that never had them.
                try
                {
                    using var codec = SkiaSharp.SKCodec.Create(abs);
                    if (codec is { Info: { Width: > 0, Height: > 0 } info })
                    {
                        await content.RecordDimensionsAsync(cid, info.Width, info.Height, ct);
                    }
                }
                catch (Exception)
                {
                    // An undecodable file still streams; the thumbnail worker
                    // reports those properly.
                }
            }

            // The URL ends in "original", and that is what a browser would
            // call a saved copy. The file's own name travels in the header -
            // inline, so the viewer can still show it, and the download
            // button's save dialog picks the name up from here.
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(Path.GetFileName(abs));   // RFC 5987 for non-ASCII names
            ctx.Response.Headers.ContentDisposition = disposition.ToString();
            return Results.File(abs, row.Mime, enableRangeProcessing: true);
        });

        // Favourite: the one bulk update the web app makes. Other fields of
        // Immich's AssetBulkUpdateDto (archive, dates, rating) have no home
        // here and are ignored rather than half-honoured.
        g.MapPut("/", async (
            AssetBulkUpdateBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            if (body.IsFavorite is not { } favorite)
            {
                return Results.NoContent();
            }

            var ids = ParseContentIds(body.Ids).ToArray();
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(favorite
                ? """
                  INSERT INTO favorite (account_id, content_id)
                  SELECT @Account, c.content_id FROM unnest(@Ids::bigint[]) AS c(content_id)
                   WHERE EXISTS (SELECT 1 FROM location l
                                  WHERE l.account_id = @Account AND l.content_id = c.content_id AND l.gone_at IS NULL)
                  ON CONFLICT DO NOTHING
                  """
                : "DELETE FROM favorite WHERE account_id = @Account AND content_id = ANY(@Ids::bigint[])",
                new { Account = me.AccountIdOrThrow, Ids = ids },
                cancellationToken: ct));
            return Results.NoContent();
        });

        // Trash: the picture leaves the timeline, the file stays exactly where
        // it is. Nothing here deletes an original - not with force, not ever
        // (the house rule). A trashed picture is a hidden one until restored.
        g.MapDelete("/", async (
            [Microsoft.AspNetCore.Mvc.FromBody] AssetBulkDeleteBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            if (body.Force == true)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                                       title: "Originals are never deleted here. Trash hides a picture; restore brings it back.");
            }

            var ids = ParseContentIds(body.Ids).ToArray();
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE location SET trashed_at = now()
                 WHERE account_id = @Account AND content_id = ANY(@Ids::bigint[])
                   AND trashed_at IS NULL AND gone_at IS NULL
                """,
                new { Account = me.AccountIdOrThrow, Ids = ids },
                cancellationToken: ct));
            return Results.NoContent();
        });

        g.MapGet("/{id}", async (
            string id, CurrentUser me, ViewableContentRepository content, Db db, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId)
            {
                return Results.NotFound();
            }

            await using var conn = await db.OpenAsync(ct);
            var a = await conn.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
                """
                SELECT c.content_id AS ContentId, c.sha256 AS Sha, c.byte_size AS ByteSize,
                       c.mime AS Mime, c.width AS Width, c.height AS Height,
                       c.duration_ms AS DurationMs, c.first_seen AS FirstSeen,
                       COALESCE(c.taken_at, l.first_seen) AS TakenAt,
                       r.abs_path AS Root, l.rel_path AS Rel, l.file_mtime AS Mtime,
                       l.trashed_at IS NOT NULL AS IsTrashed,
                       EXISTS (SELECT 1 FROM favorite fv
                                WHERE fv.account_id = @Account AND fv.content_id = c.content_id) AS IsFavorite
                  FROM content c
                  -- The account's own copy, a live one first; a trashed
                  -- picture keeps its page so the trash can show it.
                  JOIN LATERAL (
                       SELECT l.root_id, l.rel_path, l.file_mtime, l.first_seen, l.trashed_at FROM location l
                        WHERE l.content_id = c.content_id AND l.account_id = @Account
                          AND l.gone_at IS NULL
                        ORDER BY (l.trashed_at IS NULL) DESC, l.location_id
                        LIMIT 1
                  ) l ON true
                  LEFT JOIN scan_root r ON r.root_id = l.root_id
                 WHERE c.content_id = @Content
                """,
                new { Account = me.AccountIdOrThrow, Content = contentId },
                cancellationToken: ct));

            if (a is null)
            {
                return Results.NotFound();
            }

            var rel = a.Rel ?? "";
            var name = rel.Length == 0 ? "" : rel[(rel.LastIndexOf('/') + 1)..];

            return Results.Ok(new
            {
                id = ImmichId.From(a.ContentId),
                ownerId = ImmichId.Account(me.AccountIdOrThrow),
                type = a.Mime.StartsWith("image/", StringComparison.Ordinal) ? "IMAGE" : "VIDEO",
                originalPath = a.Root is null ? "" : $"{a.Root}/{rel}",
                originalFileName = name,
                originalMimeType = a.Mime,
                createdAt = Iso(a.FirstSeen),
                fileCreatedAt = Iso(a.TakenAt),
                fileModifiedAt = Iso(a.Mtime ?? a.TakenAt),
                updatedAt = Iso(a.FirstSeen),
                localDateTime = Iso(a.TakenAt),
                duration = a.DurationMs,
                isFavorite = a.IsFavorite,
                isArchived = false,
                isTrashed = a.IsTrashed,
                isOffline = false,
                isEdited = false,
                visibility = "timeline",
                // Immich documents this as base64 SHA-1. Ours is SHA-256 and
                // the field is only ever displayed, so it carries the real
                // hash rather than a shorter lie.
                checksum = Convert.ToBase64String(a.Sha),
                thumbhash = (string?)null,
                livePhotoVideoId = (string?)null,
                people = Array.Empty<object>(),
                tags = Array.Empty<object>(),
                exifInfo = new
                {
                    fileSizeInByte = a.ByteSize,
                    exifImageWidth = a.Width,
                    exifImageHeight = a.Height,
                    dateTimeOriginal = Iso(a.TakenAt),
                },
            });
        });
    }

    // --------------------------------------------------------------- albums

    private static void MapAlbums(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/albums").WithTags("immich-compat").RequireUser();

        // `?assetId=` is Immich's filter for "the albums this picture is in";
        // the info panel asks exactly that. Unknown id: no albums, not all.
        g.MapGet("/", async (
            string? assetId, CurrentUser me, AlbumRepository albums, ViewableContentRepository content,
            CancellationToken ct) =>
        {
            long? containing = null;
            if (assetId is not null)
            {
                if (await content.ResolveIdAsync(assetId, ct) is not { } cid)
                {
                    return Results.Ok(Array.Empty<object>());
                }

                containing = cid;
            }

            return Results.Ok((await albums.ListAsync(me.AccountIdOrThrow, ct, containing)).Select(Dto));
        });

        g.MapGet("/{id}", async (string id, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId))
            {
                return Results.NotFound();
            }

            var album = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return album is null ? Results.NotFound() : Results.Ok(Dto(album));
        });

        // The mutations, in Immich's request shapes because that is what the
        // web app's SDK sends. One surface: these call the same repository
        // methods everything else does, and nothing else answers them.
        g.MapPost("/", async (
            CreateAlbumBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AlbumName))
            {
                return Results.Problem(statusCode: 400, title: "An album needs a name");
            }

            var albumId = await albums.EnsureAsync(
                me.AccountIdOrThrow, body.AlbumName.Trim(), body.Description?.Trim() ?? "", ct);
            if (body.AssetIds is { Length: > 0 } ids)
            {
                await albums.AddAsync(me.AccountIdOrThrow, albumId, ParseContentIds(ids), ct);
            }

            var made = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return Results.Created($"/api/albums/{ImmichId.Album(albumId)}", Dto(made!));
        });

        g.MapPatch("/{id}", async (
            string id, UpdateAlbumBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is not { } current)
            {
                return Results.NotFound();
            }

            // A partial update: a field left out keeps its value.
            var name = string.IsNullOrWhiteSpace(body.AlbumName) ? current.Name : body.AlbumName.Trim();
            var description = body.Description ?? current.Description;
            if (!await albums.RenameAsync(me.AccountIdOrThrow, albumId, name, description, ct))
            {
                return Results.NotFound();
            }

            return Results.Ok(Dto((await albums.GetAsync(me.AccountIdOrThrow, albumId, ct))!));
        });

        g.MapDelete("/{id}", async (string id, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
            ImmichId.TryParseAlbum(id, out var albumId) && await albums.DeleteAsync(me.AccountIdOrThrow, albumId, ct)
                ? Results.NoContent()
                : Results.NotFound());

        // Membership. The answer is per id, as Immich's is, so the web app can
        // tell "already there" from "not yours" without a second call.
        g.MapPut("/{id}/assets", async (
            string id, BulkIdsBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is null)
            {
                return Results.NotFound();
            }

            var wanted = ParseContentIds(body.Ids);
            var before = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();
            await albums.AddAsync(me.AccountIdOrThrow, albumId, wanted, ct);
            var after = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();

            return Results.Ok(body.Ids.Select(raw => ImmichId.TryParse(raw, out var cid)
                ? after.Contains(cid)
                    ? new { id = raw, success = !before.Contains(cid), error = before.Contains(cid) ? "duplicate" : null }
                    : new { id = raw, success = false, error = (string?)"not_found" }
                : new { id = raw, success = false, error = (string?)"not_found" }));
        });

        g.MapDelete("/{id}/assets", async (
            string id, [Microsoft.AspNetCore.Mvc.FromBody] BulkIdsBody body,
            CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!ImmichId.TryParseAlbum(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is null)
            {
                return Results.NotFound();
            }

            var wanted = ParseContentIds(body.Ids);
            var before = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();
            await albums.RemoveAsync(me.AccountIdOrThrow, albumId, wanted, ct);

            return Results.Ok(body.Ids.Select(raw => ImmichId.TryParse(raw, out var cid) && before.Contains(cid)
                ? new { id = raw, success = true, error = (string?)null }
                : new { id = raw, success = false, error = (string?)"not_found" }));
        });
    }

    public sealed record CreateAlbumBody(string AlbumName, string? Description, string[]? AssetIds);
    public sealed record UpdateAlbumBody(string? AlbumName, string? Description);
    public sealed record BulkIdsBody(string[] Ids);

    private static List<long> ParseContentIds(IEnumerable<string> raw)
    {
        var ids = new List<long>();
        foreach (var r in raw)
        {
            if (ImmichId.TryParse(r, out var cid))
            {
                ids.Add(cid);
            }
        }

        return ids;
    }

    /// <summary>
    /// Map an album to the response the web app expects. The cover it draws is
    /// the repository's <see cref="Album.ShownCoverContentId"/> - see there for
    /// what that is and why.
    /// </summary>
    internal static object Dto(Album a) => new
    {
        id = ImmichId.Album(a.AlbumId),
        albumName = a.Name,
        description = a.Description,
        ownerId = ImmichId.Account(a.AccountId),
        albumThumbnailAssetId = a.ShownCoverContentId is { } c ? ImmichId.From(c) : null,
        createdAt = Iso(a.CreatedAt),
        updatedAt = Iso(a.UpdatedAt),
        assetCount = a.ItemCount,
        shared = false,
        hasSharedLink = false,
        isActivityEnabled = false,
        albumUsers = Array.Empty<object>(),
        assets = Array.Empty<object>(),
        order = "desc",
    };

    // ---------------------------------------------------------------- users

    private static void MapUsers(IEndpointRouteBuilder app)
    {
        // Immich's public users list: every account, so the web app can name the
        // other side of an overlap or offer a move destination. Everyone here is
        // the same person; nothing in it is secret. `kind` is ours - the app
        // uses it to offer only personal accounts as destinations.
        app.MapGet("/api/users", async (AccountRepository accounts, CancellationToken ct) =>
            Results.Ok((await accounts.ListAsync(ct))
                .Where(a => a.DisabledAt is null)
                .Select(a => new
                {
                    id = ImmichId.Account(a.AccountId),
                    email = a.Email,
                    name = a.DisplayName,
                    isAdmin = a.IsAdmin,
                    kind = a.Kind,
                    profileImagePath = "",
                    avatarColor = "primary",
                }))).WithTags("immich-compat").RequireUser();

        app.MapGet("/api/users/me", (CurrentUser me) =>
            me.Account is not { } a
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    id = ImmichId.Account(a.AccountId),
                    email = a.Email,
                    name = a.DisplayName,
                    isAdmin = a.IsAdmin,
                    kind = a.Kind,
                    profileImagePath = "",
                    shouldChangePassword = false,
                    avatarColor = "primary",
                })).WithTags("immich-compat").RequireUser();
    }

    // --------------------------------------------------------------- shared

    private sealed record BucketRow
    {
        public long ContentId { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public required string Mime { get; init; }
        public int? DurationMs { get; init; }
        public DateTimeOffset TakenAt { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public bool IsFavorite { get; init; }
    }

    private sealed record AssetRow
    {
        public long ContentId { get; init; }
        public required byte[] Sha { get; init; }
        public long ByteSize { get; init; }
        public required string Mime { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public int? DurationMs { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset TakenAt { get; init; }
        public string? Root { get; init; }
        public string? Rel { get; init; }
        public DateTimeOffset? Mtime { get; init; }
        public bool IsTrashed { get; init; }
        public bool IsFavorite { get; init; }
    }

    /// <summary>
    /// Whether a request is scoped to an album, and if so which.
    /// </summary>
    /// <remarks>
    /// Three outcomes, not two, and the third is the one that matters: an id
    /// this server never issued is not "no filter", it is a filter that must
    /// match nothing. Collapsing it into "no filter" is how every album came
    /// to show the whole library. A sentinel id would express that too, but
    /// only until someone reads -1 as a number and wonders which album it is.
    /// </remarks>
    private readonly record struct AlbumScope(bool Requested, long AlbumId)
    {
        public static AlbumScope None => new(false, 0);

        public static AlbumScope Parse(string? albumId) =>
            string.IsNullOrEmpty(albumId) ? None
            : ImmichId.TryParseAlbum(albumId, out var id) ? new AlbumScope(true, id)
            : new AlbumScope(true, Unmatchable);

        /// <summary>An album id no row can carry, so an unknown album is empty.</summary>
        private const long Unmatchable = -1;
    }

    /// <summary>Aspect ratio, 1 when the dimensions were never recorded.</summary>
    private static double Ratio(BucketRow r) =>
        r.Width is > 0 && r.Height is > 0 ? (double)r.Width.Value / r.Height.Value : 1.0;

    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

}
