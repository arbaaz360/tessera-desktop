using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// The routes the web app already calls, unchanged (CONTRACT §3).
/// </summary>
/// <remarks>
/// <para>
/// The web app was generated against Immich's SDK, so these request and
/// response shapes are kept as they were: paths, verbs, query parameter
/// names, JSON. Ids on the wire are now the database's own integers as
/// decimal strings; the app treats them as opaque.
/// </para>
/// <para>
/// This is a translation layer and nothing else. It owns no data, caches
/// nothing, and every route below is a rename of something Tessera already
/// answers.
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
    }

    // ------------------------------------------------------------- timeline

    /// <summary>
    /// The grid. Buckets are months, and a bucket's contents come back as
    /// PARALLEL ARRAYS rather than objects: a bucket of 40,000 photos is
    /// fifteen arrays, not fifteen field names repeated 40,000 times.
    /// </summary>
    private static void MapTimeline(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/timeline").WithTags("timeline").RequireUser();

        // Every view of the library is the timeline with a filter: an album,
        // the trash, favourites, photos-only, oldest-first. The web app asks
        // these two endpoints for all of them.
        g.MapGet("/buckets", async (
            string? albumId, bool? isTrashed, bool? isFavorite, string? assetType, string? order,
            CurrentUser me, Db db, CancellationToken ct) =>
        {
            var f = TimelineFilter.From(albumId, isTrashed, isFavorite, assetType, order);

            await using var conn = await db.OpenAsync(ct);
            var rows = await conn.QueryAsync<(string Month, int Count)>(new CommandDefinition(
                $"""
                {f.Source}
                SELECT strftime('%Y-%m-01', t.taken_at / 1000, 'unixepoch') AS month, count(*) AS count
                  FROM src t
                  JOIN content c ON c.content_id = t.content_id
                 WHERE {TimelineFilter.Where}
                 GROUP BY 1
                 ORDER BY 1 {f.Direction}
                """,
                f.Parameters(me.AccountIdOrThrow),
                cancellationToken: ct));

            return Results.Ok(rows.Select(r => new { timeBucket = r.Month, count = r.Count }));
        });

        g.MapGet("/bucket", async (
            string timeBucket, string? albumId, bool? isTrashed, bool? isFavorite, string? assetType, string? order,
            CurrentUser me, Db db, CancellationToken ct) =>
        {
            var f = TimelineFilter.From(albumId, isTrashed, isFavorite, assetType, order);

            if (!DateTimeOffset.TryParse(
                    timeBucket, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "timeBucket must be YYYY-MM-DD");
            }

            var end = start.AddMonths(1);

            await using var conn = await db.OpenAsync(ct);
            var rows = (await conn.QueryAsync<AssetRow>(new CommandDefinition(
                $"""
                {f.Source}
                SELECT {AssetShapes.Columns}
                  FROM src t
                  JOIN content c ON c.content_id = t.content_id
                  -- The grid only needs the RATIO; the thumbnail was resized
                  -- proportionally, so its row carries the same ratio.
                  LEFT JOIN thumbnail th ON th.content_id = t.content_id AND th.kind = 'thumb'
                 WHERE {TimelineFilter.Where}
                   AND t.taken_at >= @Start AND t.taken_at < @End
                 ORDER BY t.taken_at {f.Direction}, t.content_id {f.Direction}
                """,
                f.Parameters(me.AccountIdOrThrow, start, end),
                cancellationToken: ct))).ToList();

            var owner = Api.Id(me.AccountIdOrThrow);
            var n = rows.Count;

            return Results.Ok(new
            {
                id = rows.Select(r => Api.Id(r.ContentId)).ToArray(),
                ownerId = Enumerable.Repeat(owner, n).ToArray(),
                ratio = rows.Select(AssetShapes.Ratio).ToArray(),
                isFavorite = rows.Select(r => r.IsFavorite).ToArray(),
                visibility = Enumerable.Repeat("timeline", n).ToArray(),
                isTrashed = Enumerable.Repeat(f.Trashed, n).ToArray(),
                isImage = rows.Select(AssetShapes.IsImage).ToArray(),
                thumbhash = new string?[n],
                createdAt = rows.Select(r => Api.Iso(r.FirstSeen)).ToArray(),
                fileCreatedAt = rows.Select(r => Api.Iso(r.TakenAt)).ToArray(),
                localOffsetHours = new double[n],
                duration = rows.Select(r => r.DurationMs).ToArray(),
                projectionType = new string?[n],
                livePhotoVideoId = new string?[n],
            });
        });

        // The keyset page, for anything that wants a flat list rather than
        // months - and the shape the 200,000-row latency test measures.
        g.MapGet("/", async (
            string? before, long? beforeId, int? size, CurrentUser me, TimelineRepository timeline, CancellationToken ct) =>
        {
            DateTimeOffset? cursor = null;
            if (before is not null)
            {
                if (!DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return Api.Fail(StatusCodes.Status400BadRequest, "before must be an ISO 8601 time");
                }

                cursor = parsed;
            }

            var page = await timeline.PageAsync(me.AccountIdOrThrow, cursor, beforeId, size ?? 200, ct);
            return Results.Ok(new
            {
                items = page.Items.Select(i => new
                {
                    id = Api.Id(i.ContentId),
                    takenAt = Api.Iso(i.TakenAt),
                    sha256 = i.Sha256Hex,
                    width = i.Width,
                    height = i.Height,
                    mime = i.Mime,
                }),
                nextBefore = Api.Iso(page.NextBefore),
                nextId = page.NextContentId is { } id ? Api.Id(id) : null,
            });
        });
    }

    /// <summary>
    /// The timeline's filters, turned into one SQL source and one WHERE.
    /// </summary>
    /// <remarks>
    /// Live pictures come from <c>timeline_entry</c>, which the triggers keep
    /// equal to "has a location that is neither trashed nor gone". Trashed
    /// pictures are exactly the ones that table has dropped, so the trash view
    /// reads <c>location</c> directly. An album reads its members' timeline
    /// rows. Everything else - favourite, photos or videos, direction - is
    /// the same clause on either source.
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
                  SELECT l.account_id, l.content_id, COALESCE(max(c0.taken_at), min(l.first_seen)) AS taken_at
                    FROM location l
                    JOIN content c0 ON c0.content_id = l.content_id
                   WHERE l.account_id = @Account AND l.trashed_at IS NOT NULL AND l.gone_at IS NULL
                   GROUP BY l.account_id, l.content_id
              )
              """
            : AlbumId is not null
                ? """
                  WITH src AS (
                      SELECT t.account_id, t.content_id, t.taken_at
                        FROM album_item ai
                        JOIN timeline_entry t ON t.account_id = @Account AND t.content_id = ai.content_id
                       WHERE ai.album_id = @Album
                  )
                  """
                : """
                  WITH src AS (
                      SELECT t.account_id, t.content_id, t.taken_at
                        FROM timeline_entry t
                       WHERE t.account_id = @Account
                  )
                  """;

        public static string Where => """
            t.account_id = @Account
               AND (@Album IS NULL OR EXISTS (
                    SELECT 1 FROM album_item ai
                      JOIN album al ON al.album_id = ai.album_id
                     WHERE ai.content_id = t.content_id
                       AND ai.album_id = @Album
                       AND al.account_id = @Account))
               AND (@FavoritesOnly = 0 OR EXISTS (
                    SELECT 1 FROM favorite fv
                     WHERE fv.account_id = @Account AND fv.content_id = t.content_id))
               AND (@MimePrefix IS NULL OR c.mime LIKE @MimePrefix || '%')
            """;

        public object Parameters(long account, DateTimeOffset? start = null, DateTimeOffset? end = null) => new
        {
            Account = account,
            Album = AlbumId,
            FavoritesOnly,
            MimePrefix,
            Start = Db.ToMs(start),
            End = Db.ToMs(end),
        };
    }

    // ---------------------------------------------------------------- trash

    private static void MapTrash(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/trash").WithTags("trash").RequireUser();

        g.MapPost("/restore/assets", async (
            BulkIdsBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            var ids = Api.ParseIds(body.Ids);
            await using var conn = await db.OpenWriteAsync(ct);
            var n = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE location SET trashed_at = NULL
                 WHERE account_id = @Account AND trashed_at IS NOT NULL
                   AND content_id IN (SELECT value FROM json_each(@Ids))
                """,
                new { Account = me.AccountIdOrThrow, Ids = JsonSerializer.Serialize(ids) },
                cancellationToken: ct));
            return Results.Ok(new { count = n });
        });

        g.MapPost("/restore", async (CurrentUser me, Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenWriteAsync(ct);
            var n = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE location SET trashed_at = NULL WHERE account_id = @Account AND trashed_at IS NOT NULL",
                new { Account = me.AccountIdOrThrow }, cancellationToken: ct));
            return Results.Ok(new { count = n });
        });

        // There is no emptying. The originals are the thing this whole product
        // protects; the trash is a list, and the only way out of it is back.
        g.MapPost("/empty", () => Api.Fail(
            StatusCodes.Status403Forbidden,
            "Originals are never deleted here. Restore what you want back and leave the rest hidden."));
    }

    public sealed record AssetBulkUpdateBody(string[] Ids, bool? IsFavorite);
    public sealed record AssetBulkDeleteBody(string[] Ids, bool? Force);

    // --------------------------------------------------------------- assets

    private static void MapAssets(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").WithTags("assets").RequireUser();

        // The grid asks for this once per tile, so it is the hottest route in
        // the product. There is ONE derived size; Immich's `size=` parameter
        // is accepted and ignored so old links keep working.
        g.MapGet("/{id}/thumbnail", async (
            string id, CurrentUser me, ViewableContentRepository content,
            IOptions<StorageOptions> storage, HttpContext ctx, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId ||
                await content.GetAsync(me.AccountIdOrThrow, contentId, ct) is not { } row)
            {
                return Api.NotFound();
            }

            var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, row.Sha, "thumb", "webp");
            if (!File.Exists(path))
            {
                return Api.NotFound("No thumbnail yet");
            }

            // The hash IS the version: if the bytes change, the URL changes.
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, "image/webp");
        });

        g.MapGet("/{id}/original", (
            string id, CurrentUser me, ViewableContentRepository content, HttpContext ctx, CancellationToken ct) =>
            OriginalAsync(id, me, content, ctx, inline: true, ct));

        // <video> asks here. Same bytes, same range support.
        g.MapGet("/{id}/video/playback", (
            string id, CurrentUser me, ViewableContentRepository content, HttpContext ctx, CancellationToken ct) =>
            OriginalAsync(id, me, content, ctx, inline: true, ct));

        // Favourite: the one bulk update the web app makes. Other fields of
        // Immich's AssetBulkUpdateDto have no home here and are ignored
        // rather than half-honoured.
        g.MapPut("/", async (
            AssetBulkUpdateBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            if (body.IsFavorite is not { } favorite)
            {
                return Results.NoContent();
            }

            var ids = JsonSerializer.Serialize(Api.ParseIds(body.Ids));
            await using var conn = await db.OpenWriteAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(favorite
                ? """
                  INSERT INTO favorite (account_id, content_id)
                  SELECT @Account, x.value FROM json_each(@Ids) x
                   WHERE EXISTS (SELECT 1 FROM location l
                                  WHERE l.account_id = @Account AND l.content_id = x.value AND l.gone_at IS NULL)
                  ON CONFLICT DO NOTHING
                  """
                : "DELETE FROM favorite WHERE account_id = @Account AND content_id IN (SELECT value FROM json_each(@Ids))",
                new { Account = me.AccountIdOrThrow, Ids = ids },
                cancellationToken: ct));
            return Results.NoContent();
        });

        // Trash: the picture leaves the timeline, the file stays exactly where
        // it is. Nothing here deletes an original - not with force, not ever.
        g.MapDelete("/", async (
            [Microsoft.AspNetCore.Mvc.FromBody] AssetBulkDeleteBody body, CurrentUser me, Db db, CancellationToken ct) =>
        {
            if (body.Force == true)
            {
                return Api.Fail(StatusCodes.Status403Forbidden,
                    "Originals are never deleted here. Trash hides a picture; restore brings it back.");
            }

            var ids = JsonSerializer.Serialize(Api.ParseIds(body.Ids));
            await using var conn = await db.OpenWriteAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE location SET trashed_at = {Db.NowMs}
                 WHERE account_id = @Account AND content_id IN (SELECT value FROM json_each(@Ids))
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
                return Api.NotFound();
            }

            await using var conn = await db.OpenAsync(ct);
            var a = await conn.QuerySingleOrDefaultAsync<AssetInfoRow>(new CommandDefinition(
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
                  JOIN location l ON l.location_id = (
                       SELECT l2.location_id FROM location l2
                        WHERE l2.content_id = c.content_id AND l2.account_id = @Account
                          AND l2.gone_at IS NULL
                        ORDER BY (l2.trashed_at IS NULL) DESC, l2.location_id
                        LIMIT 1)
                  LEFT JOIN scan_root r ON r.root_id = l.root_id
                 WHERE c.content_id = @Content
                """,
                new { Account = me.AccountIdOrThrow, Content = contentId },
                cancellationToken: ct));

            if (a is null)
            {
                return Api.NotFound();
            }

            var rel = a.Rel ?? "";
            var name = rel.Length == 0 ? "" : rel[(rel.LastIndexOf('/') + 1)..];

            return Results.Ok(new
            {
                id = Api.Id(a.ContentId),
                ownerId = Api.Id(me.AccountIdOrThrow),
                type = a.Mime.StartsWith("image/", StringComparison.Ordinal) ? "IMAGE" : "VIDEO",
                originalPath = a.Root is null ? "" : $"{a.Root}/{rel}",
                originalFileName = name,
                originalMimeType = a.Mime,
                createdAt = Api.Iso(a.FirstSeen),
                fileCreatedAt = Api.Iso(a.TakenAt),
                fileModifiedAt = Api.Iso(a.Mtime ?? a.TakenAt),
                updatedAt = Api.Iso(a.FirstSeen),
                localDateTime = Api.Iso(a.TakenAt),
                duration = a.DurationMs,
                isFavorite = a.IsFavorite,
                isArchived = false,
                isTrashed = a.IsTrashed,
                isOffline = false,
                isEdited = false,
                visibility = "timeline",
                // Immich documents this as base64 SHA-1. Ours is SHA-256 and
                // the field is only ever displayed, so it carries the real hash.
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
                    dateTimeOriginal = Api.Iso(a.TakenAt),
                },
            });
        });
    }

    private static async Task<IResult> OriginalAsync(
        string id, CurrentUser me, ViewableContentRepository content, HttpContext ctx, bool inline, CancellationToken ct)
    {
        if (await content.ResolveIdAsync(id, ct) is not { } cid ||
            await content.GetAsync(me.AccountIdOrThrow, cid, ct) is not { AbsolutePath: { } abs } row)
        {
            return Api.NotFound();
        }

        if (!File.Exists(abs))
        {
            // The database says it is here and the disk disagrees. That is
            // drift worth surfacing, not a 500.
            return Api.Fail(StatusCodes.Status410Gone, "The file has moved or been removed since the last scan");
        }

        if (row.Width is null && row.Mime.StartsWith("image/", StringComparison.Ordinal))
        {
            // Header only - Skia reads the dimensions without decoding pixels -
            // and only for pictures that never had them.
            try
            {
                if (SkiaFiles.Info(abs) is { Width: > 0, Height: > 0 } info)
                {
                    await content.RecordDimensionsAsync(cid, info.Width, info.Height, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // An undecodable file still streams; the thumbnail worker reports those properly.
            }
        }

        // The file's own name travels in the header - inline, so the viewer
        // can still show it, and the save dialog picks the name up from here.
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(Path.GetFileName(abs));   // RFC 5987 for non-ASCII names
        ctx.Response.Headers.ContentDisposition = disposition.ToString();
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(abs, row.Mime, enableRangeProcessing: true);
    }

    // --------------------------------------------------------------- albums

    private static void MapAlbums(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/albums").WithTags("albums").RequireUser();

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
            if (!Api.TryParseId(id, out var albumId))
            {
                return Api.NotFound();
            }

            var album = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return album is null ? Api.NotFound() : Results.Ok(Dto(album));
        });

        g.MapPost("/", async (
            CreateAlbumBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.AlbumName))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "An album needs a name");
            }

            var albumId = await albums.EnsureAsync(
                me.AccountIdOrThrow, body.AlbumName.Trim(), body.Description?.Trim() ?? "", ct);
            if (body.AssetIds is { Length: > 0 } ids)
            {
                await albums.AddAsync(me.AccountIdOrThrow, albumId, Api.ParseIds(ids), ct);
            }

            var made = await albums.GetAsync(me.AccountIdOrThrow, albumId, ct);
            return Results.Created($"/api/albums/{Api.Id(albumId)}", Dto(made!));
        });

        g.MapPatch("/{id}", async (
            string id, UpdateAlbumBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!Api.TryParseId(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is not { } current)
            {
                return Api.NotFound();
            }

            // A partial update: a field left out keeps its value.
            var name = string.IsNullOrWhiteSpace(body.AlbumName) ? current.Name : body.AlbumName.Trim();
            var description = body.Description ?? current.Description;
            if (!await albums.RenameAsync(me.AccountIdOrThrow, albumId, name, description, ct))
            {
                return Api.NotFound();
            }

            // Immich's UpdateAlbumDto carries the cover here too; the web's
            // cover picker sends it on this route. A choice is final until
            // the next choice.
            if (body.AlbumThumbnailAssetId is { } coverId)
            {
                if (!Api.TryParseId(coverId, out var contentId))
                {
                    return Api.Fail(StatusCodes.Status400BadRequest, "albumThumbnailAssetId is not an id this server issued");
                }

                if (!await albums.SetCoverAsync(me.AccountIdOrThrow, albumId, contentId, ct))
                {
                    return Api.Fail(StatusCodes.Status400BadRequest, "That picture is not in this album");
                }
            }

            return Results.Ok(Dto((await albums.GetAsync(me.AccountIdOrThrow, albumId, ct))!));
        });

        g.MapDelete("/{id}", async (string id, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
            Api.TryParseId(id, out var albumId) && await albums.DeleteAsync(me.AccountIdOrThrow, albumId, ct)
                ? Results.NoContent()
                : Api.NotFound());

        // Membership. The answer is per id, as Immich's is, so the web app can
        // tell "already there" from "not yours" without a second call.
        g.MapPut("/{id}/assets", async (
            string id, BulkIdsBody body, CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!Api.TryParseId(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is null)
            {
                return Api.NotFound();
            }

            var wanted = Api.ParseIds(body.Ids);
            var before = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();
            await albums.AddAsync(me.AccountIdOrThrow, albumId, wanted, ct);
            var after = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();

            return Results.Ok(body.Ids.Select(raw => Api.TryParseId(raw, out var cid)
                ? after.Contains(cid)
                    ? new { id = raw, success = !before.Contains(cid), error = before.Contains(cid) ? "duplicate" : null }
                    : new { id = raw, success = false, error = (string?)"not_found" }
                : new { id = raw, success = false, error = (string?)"not_found" }));
        });

        g.MapDelete("/{id}/assets", async (
            string id, [Microsoft.AspNetCore.Mvc.FromBody] BulkIdsBody body,
            CurrentUser me, AlbumRepository albums, CancellationToken ct) =>
        {
            if (!Api.TryParseId(id, out var albumId) ||
                await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is null)
            {
                return Api.NotFound();
            }

            var wanted = Api.ParseIds(body.Ids);
            var before = (await albums.MemberHashesAsync(albumId, ct)).Select(m => m.ContentId).ToHashSet();
            await albums.RemoveAsync(me.AccountIdOrThrow, albumId, wanted, ct);

            return Results.Ok(body.Ids.Select(raw => Api.TryParseId(raw, out var cid) && before.Contains(cid)
                ? new { id = raw, success = true, error = (string?)null }
                : new { id = raw, success = false, error = (string?)"not_found" }));
        });
    }

    public sealed record CreateAlbumBody(string AlbumName, string? Description, string[]? AssetIds);
    public sealed record UpdateAlbumBody(string? AlbumName, string? Description, string? AlbumThumbnailAssetId);
    public sealed record BulkIdsBody(string[] Ids);

    /// <summary>
    /// Map an album to the response the web app expects. The cover it draws is
    /// the repository's <see cref="Album.ShownCoverContentId"/>.
    /// </summary>
    internal static object Dto(Album a) => new
    {
        id = Api.Id(a.AlbumId),
        albumName = a.Name,
        description = a.Description,
        ownerId = Api.Id(a.AccountId),
        albumThumbnailAssetId = a.ShownCoverContentId is { } c ? Api.Id(c) : null,
        createdAt = Api.Iso(a.CreatedAt),
        updatedAt = Api.Iso(a.UpdatedAt),
        assetCount = a.ItemCount,
        // Additive: "folder" albums are the folder rule's and go with their folder.
        origin = a.Origin,
        shared = false,
        hasSharedLink = false,
        isActivityEnabled = false,
        albumUsers = Array.Empty<object>(),
        assets = Array.Empty<object>(),
        order = "desc",
    };

    // --------------------------------------------------------------- shared

    private sealed record AssetInfoRow
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
    /// to show the whole library.
    /// </remarks>
    private readonly record struct AlbumScope(bool Requested, long AlbumId)
    {
        public static AlbumScope None => new(false, 0);

        public static AlbumScope Parse(string? albumId) =>
            string.IsNullOrEmpty(albumId) ? None
            : Api.TryParseId(albumId, out var id) ? new AlbumScope(true, id)
            : new AlbumScope(true, Unmatchable);

        /// <summary>An album id no row can carry, so an unknown album is empty.</summary>
        private const long Unmatchable = -1;
    }
}
