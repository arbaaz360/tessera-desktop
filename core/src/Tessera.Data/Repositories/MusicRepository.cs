using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>What the identifier said about one video.</summary>
public sealed record Identification(
    bool Matched,
    string? TrackKey, string? Title, string? Artist, string? Album, string? ArtworkUrl,
    string Provider, string? ProviderTrackId, string RawJson,
    double? OffsetSeconds, double? Confidence, string IdentifierJson);

// Dapper-materialised rows use settable properties, not positional records:
// a timestamptz arrives as DateTime and Postgres lower-cases every alias, and
// constructor matching forgives neither.

/// <summary>The music facts about one piece of content, for the viewer.</summary>
public sealed record ContentMusic
{
    public long ContentId { get; init; }
    public required string State { get; init; }
    public int Attempts { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? IdentifiedAt { get; init; }
    public string? TrackKey { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? ArtworkUrl { get; init; }
    public long SameTrackCount { get; init; }
}

/// <summary>Counts for the caller's videos.</summary>
public sealed record MusicStatus
{
    public long Videos { get; init; }
    public long Pending { get; init; }
    public long Processing { get; init; }
    public long Matched { get; init; }
    public long NoMatch { get; init; }
    public long Failed { get; init; }
    public DateTimeOffset? LastIdentifiedAt { get; init; }
}

public sealed record TrackSummary
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public string? ArtworkUrl { get; init; }
}

public sealed record TrackVideo
{
    public long ContentId { get; init; }
    public required string FileName { get; init; }
    public DateTimeOffset? TakenAt { get; init; }
    public int? DurationMs { get; init; }
}

/// <summary>
/// The tracks in videos: what Tessera stores, and what it queues.
/// </summary>
/// <remarks>
/// <para>
/// The sidecar this replaces kept these facts in a schema inside Immich's
/// database. Here they are two tables of Tessera's own (migration 0004), and
/// the queue is the same <c>work_item</c> every other job uses, so the jobs
/// page shows music beside thumbnails with the same controls.
/// </para>
/// <para>
/// "State" for a video is derived, never stored: a row in <c>content_track</c>
/// is matched or not; a row in the queue is pending, running or failed; no row
/// anywhere is "never asked". One question, one answer, no counter to drift.
/// </para>
/// </remarks>
public sealed class MusicRepository(Db db)
{
    /// <summary>
    /// Store the answer for one video, replacing any earlier one.
    /// </summary>
    /// <remarks>
    /// The track is upserted by its key, so a song heard in a hundred videos is
    /// one row that a hundred content rows point at. A no-match is stored too:
    /// it is an answer, and the thing that stops the video being asked again.
    /// </remarks>
    public async Task RecordAsync(long contentId, Identification id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? trackId = null;
        if (id.Matched)
        {
            trackId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO track (key, title, artist, album, artwork_url, provider, provider_track_id, raw)
                VALUES (@Key, @Title, @Artist, @Album, @ArtworkUrl, @Provider, @ProviderTrackId, @Raw::jsonb)
                ON CONFLICT (key) DO UPDATE
                    SET title = EXCLUDED.title, artist = EXCLUDED.artist,
                        album = coalesce(EXCLUDED.album, track.album),
                        artwork_url = coalesce(EXCLUDED.artwork_url, track.artwork_url),
                        raw = EXCLUDED.raw
                RETURNING track_id
                """,
                new
                {
                    Key = id.TrackKey, Title = id.Title ?? "", Artist = id.Artist ?? "", id.Album, id.ArtworkUrl,
                    id.Provider, id.ProviderTrackId, Raw = id.RawJson,
                },
                tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO content_track (content_id, track_id, matched, offset_seconds, confidence, identifier, identified_at)
            VALUES (@ContentId, @TrackId, @Matched, @Offset, @Confidence, @Identifier::jsonb, now())
            ON CONFLICT (content_id) DO UPDATE
                SET track_id = EXCLUDED.track_id, matched = EXCLUDED.matched,
                    offset_seconds = EXCLUDED.offset_seconds, confidence = EXCLUDED.confidence,
                    identifier = EXCLUDED.identifier, identified_at = now()
            """,
            new
            {
                ContentId = contentId, TrackId = trackId, id.Matched,
                Offset = id.OffsetSeconds, id.Confidence, Identifier = id.IdentifierJson,
            },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Queue the caller's videos for identification.
    /// </summary>
    /// <param name="mode">
    /// <c>missing</c>: never asked. <c>no_match</c>: asked, nothing found - try
    /// again, the identifier may have learned. <c>failed</c>: the queue gave
    /// up. <c>all</c>: every video, results and all.
    /// </param>
    /// <remarks>
    /// Videos the caller holds a live copy of - or, with a null account, every
    /// personal account's - never a corpus: a corpus is browsed, not
    /// identified. The queue's partial unique index makes a repeat request free.
    /// </remarks>
    public async Task<int> QueueAsync(long? accountId, string mode, CancellationToken ct)
    {
        var condition = mode switch
        {
            "missing" => "NOT EXISTS (SELECT 1 FROM content_track t WHERE t.content_id = c.content_id)",
            "no_match" => "EXISTS (SELECT 1 FROM content_track t WHERE t.content_id = c.content_id AND NOT t.matched)",
            "failed" => "EXISTS (SELECT 1 FROM work_item w WHERE w.kind = 'music' AND w.content_id = c.content_id AND w.state = 'failed')",
            "all" => "true",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "missing | no_match | failed | all"),
        };

        await using var conn = await db.OpenAsync(ct);
        if (mode == "failed")
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM work_item w USING location l
                 WHERE w.kind = 'music' AND w.state = 'failed'
                   AND l.content_id = w.content_id AND l.gone_at IS NULL
                   AND (@Account::bigint IS NULL OR l.account_id = @Account)
                """,
                new { Account = accountId }, cancellationToken: ct));
        }

        return await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO work_item (kind, content_id)
            SELECT 'music', c.content_id
              FROM content c
             WHERE c.mime LIKE 'video/%'
               AND EXISTS (SELECT 1 FROM location l JOIN account a ON a.account_id = l.account_id
                            WHERE l.content_id = c.content_id AND l.gone_at IS NULL AND a.kind = 'personal'
                              AND (@Account::bigint IS NULL OR l.account_id = @Account))
               AND {condition}
            ON CONFLICT DO NOTHING
            """,
            new { Account = accountId }, commandTimeout: 600, cancellationToken: ct));
    }

    /// <summary>Queue one video ahead of everything else.</summary>
    public async Task<bool> QueueOneAsync(long accountId, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id, enqueued_at)
            SELECT 'music', c.content_id, '1970-01-01'   -- sorts first
              FROM content c
             WHERE c.content_id = @Content AND c.mime LIKE 'video/%'
               AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = c.content_id
                            AND l.account_id = @Account AND l.gone_at IS NULL)
            ON CONFLICT DO NOTHING
            """,
            new { Account = accountId, Content = contentId }, cancellationToken: ct)) > 0;
    }

    public async Task<MusicStatus> StatusAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<MusicStatus>(new CommandDefinition(
            """
            WITH mine AS (
                SELECT DISTINCT c.content_id
                  FROM content c JOIN location l ON l.content_id = c.content_id
                 WHERE l.account_id = @Account AND l.gone_at IS NULL AND c.mime LIKE 'video/%'
            )
            SELECT count(*)                                                                    AS Videos,
                   count(*) FILTER (WHERE w.state = 'pending')                                 AS Pending,
                   count(*) FILTER (WHERE w.state = 'running')                                 AS Processing,
                   count(*) FILTER (WHERE t.matched)                                           AS Matched,
                   count(*) FILTER (WHERE t.content_id IS NOT NULL AND NOT t.matched)          AS NoMatch,
                   count(*) FILTER (WHERE w.state = 'failed')                                  AS Failed,
                   max(t.identified_at)                                                        AS LastIdentifiedAt
              FROM mine m
              LEFT JOIN content_track t ON t.content_id = m.content_id
              LEFT JOIN work_item w ON w.kind = 'music' AND w.content_id = m.content_id
                                    AND w.state IN ('pending', 'running', 'failed')
            """,
            new { Account = accountId }, cancellationToken: ct));
    }

    /// <summary>The viewer's question: what is playing in this one, and how many share it.</summary>
    public async Task<ContentMusic?> ForContentAsync(long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ContentMusic>(new CommandDefinition(
            """
            SELECT c.content_id AS ContentId,
                   CASE WHEN w.state IN ('pending', 'running', 'failed') THEN w.state
                        WHEN t.matched THEN 'matched'
                        WHEN t.content_id IS NOT NULL THEN 'no_match'
                        ELSE 'never' END                                    AS State,
                   coalesce(w.attempts, 0)                                  AS Attempts,
                   w.last_error                                             AS Error,
                   t.identified_at                                          AS IdentifiedAt,
                   tr.key AS TrackKey, tr.title AS Title, tr.artist AS Artist, tr.album AS Album,
                   tr.artwork_url AS ArtworkUrl,
                   (SELECT count(*) FROM content_track x WHERE x.track_id = tr.track_id) AS SameTrackCount
              FROM content c
              LEFT JOIN content_track t ON t.content_id = c.content_id
              LEFT JOIN track tr ON tr.track_id = t.track_id
              LEFT JOIN work_item w ON w.kind = 'music' AND w.content_id = c.content_id
                                    AND w.state IN ('pending', 'running', 'failed')
             WHERE c.content_id = @Content
            """,
            new { Content = contentId }, cancellationToken: ct));
    }

    public async Task<TrackSummary?> TrackAsync(string key, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TrackSummary>(new CommandDefinition(
            "SELECT key AS Key, title AS Title, artist AS Artist, album AS Album, artwork_url AS ArtworkUrl FROM track WHERE key = @Key",
            new { Key = key }, cancellationToken: ct));
    }

    /// <summary>The caller's videos that share a track, newest first, one page.</summary>
    public async Task<(long Total, IReadOnlyList<TrackVideo> Items)> TrackVideosAsync(
        long accountId, string key, int page, int size, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*) FROM content_track t
              JOIN track tr ON tr.track_id = t.track_id AND tr.key = @Key
             WHERE EXISTS (SELECT 1 FROM location l WHERE l.content_id = t.content_id
                            AND l.account_id = @Account AND l.gone_at IS NULL)
            """,
            new { Account = accountId, Key = key }, cancellationToken: ct));

        var items = await conn.QueryAsync<TrackVideo>(new CommandDefinition(
            """
            SELECT c.content_id AS ContentId,
                   regexp_replace(pick.rel_path, '^.*/', '') AS FileName,
                   c.taken_at AS TakenAt, c.duration_ms AS DurationMs
              FROM content_track t
              JOIN track tr ON tr.track_id = t.track_id AND tr.key = @Key
              JOIN content c ON c.content_id = t.content_id
              JOIN LATERAL (
                   SELECT l.rel_path FROM location l
                    WHERE l.content_id = c.content_id AND l.account_id = @Account AND l.gone_at IS NULL
                    ORDER BY l.location_id LIMIT 1
              ) pick ON true
             ORDER BY c.taken_at DESC NULLS LAST, c.content_id DESC
             OFFSET @Offset LIMIT @Size
            """,
            new { Account = accountId, Key = key, Offset = (page - 1) * size, Size = size },
            cancellationToken: ct));

        return (total, items.ToList());
    }
}
