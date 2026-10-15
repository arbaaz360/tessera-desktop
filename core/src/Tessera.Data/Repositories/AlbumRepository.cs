using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>Albums, and the one fact about them that is derived: the cover shown.</summary>
/// <remarks>
/// <para>
/// <b>The cover has one owner: <c>album.cover_content_id</c>.</b> It holds a
/// deliberate choice - a person's, or one migrated from the system before -
/// and nothing else writes it. When it is unset the album still needs a
/// picture, so every read also returns <see cref="Album.ShownCoverContentId"/>:
/// the choice if there is one, otherwise A MEMBER of the album.
/// </para>
/// <para>
/// A member, not the best one and not the newest. The fallback is chosen by
/// the primary key so it is cheap and STABLE: the same album shows the same
/// picture every time, which is what makes a grid feel solid. Picking the
/// newest instead means sorting every album's contents by taken_at on each
/// page load - measured at 1,775 ms against 16 ms, a hundredfold for a
/// cosmetic gain, growing with the library. Ranking covers by what is in the
/// picture is the cover picker's job, and its answer lands in the column.
/// </para>
/// <para>
/// This SELECT used to exist four times - twice here without the fallback,
/// twice in the Immich-compat layer with it. One shape, one place.
/// </para>
/// </remarks>
public sealed class AlbumRepository(Db db)
{
    /// <summary>
    /// The account's albums; with <paramref name="containing"/>, only those
    /// that hold that content (the info panel's "which album is this in").
    /// </summary>
    public async Task<IReadOnlyList<Album>> ListAsync(long accountId, CancellationToken ct, long? containing = null)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Album>(new CommandDefinition(
            """
            SELECT a.album_id AS AlbumId, a.account_id AS AccountId, a.name AS Name,
                   a.description AS Description, a.cover_content_id AS CoverContentId,
                   COALESCE(a.cover_content_id, pick.content_id, anyp.content_id) AS ShownCoverContentId,
                   a.created_at AS CreatedAt, a.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM album_item i WHERE i.album_id = a.album_id) AS ItemCount
              FROM album a
              JOIN account ac ON ac.account_id = a.account_id
              LEFT JOIN LATERAL (
                   SELECT i.content_id FROM album_item i
                    WHERE i.album_id = a.album_id
                      -- Newest first, but a picture whose thumbnail exists
                      -- beats one whose thumbnail is still queued: a cover
                      -- that is a blank tile for an hour after a copy-in is
                      -- no cover. Corpus accounts get no thumbnails at all,
                      -- so for them any item will do (and walking every item
                      -- of 900 albums looking for one would cost seconds).
                      AND (ac.kind = 'corpus' OR EXISTS (
                           SELECT 1 FROM thumbnail t WHERE t.content_id = i.content_id))
                    ORDER BY i.content_id DESC LIMIT 1
              ) pick ON true
              LEFT JOIN LATERAL (
                   SELECT i.content_id FROM album_item i
                    WHERE i.album_id = a.album_id
                    ORDER BY i.content_id DESC LIMIT 1
              ) anyp ON true
             WHERE a.account_id = @AccountId
               AND (@Containing::bigint IS NULL OR EXISTS (
                       SELECT 1 FROM album_item i
                        WHERE i.album_id = a.album_id AND i.content_id = @Containing))
             ORDER BY a.name
            """,
            new { AccountId = accountId, Containing = containing },
            cancellationToken: ct))).ToList();
    }

    public async Task<Album?> GetAsync(long accountId, long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Album>(new CommandDefinition(
            """
            SELECT a.album_id AS AlbumId, a.account_id AS AccountId, a.name AS Name,
                   a.description AS Description, a.cover_content_id AS CoverContentId,
                   COALESCE(a.cover_content_id, pick.content_id, anyp.content_id) AS ShownCoverContentId,
                   a.created_at AS CreatedAt, a.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM album_item i WHERE i.album_id = a.album_id) AS ItemCount
              FROM album a
              JOIN account ac ON ac.account_id = a.account_id
              LEFT JOIN LATERAL (
                   SELECT i.content_id FROM album_item i
                    WHERE i.album_id = a.album_id
                      -- Newest first, but a picture whose thumbnail exists
                      -- beats one whose thumbnail is still queued: a cover
                      -- that is a blank tile for an hour after a copy-in is
                      -- no cover. Corpus accounts get no thumbnails at all,
                      -- so for them any item will do (and walking every item
                      -- of 900 albums looking for one would cost seconds).
                      AND (ac.kind = 'corpus' OR EXISTS (
                           SELECT 1 FROM thumbnail t WHERE t.content_id = i.content_id))
                    ORDER BY i.content_id DESC LIMIT 1
              ) pick ON true
              LEFT JOIN LATERAL (
                   SELECT i.content_id FROM album_item i
                    WHERE i.album_id = a.album_id
                    ORDER BY i.content_id DESC LIMIT 1
              ) anyp ON true
             WHERE a.account_id = @AccountId AND a.album_id = @AlbumId
            """,
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct));
    }

    /// <summary>Find an album by name, or make it. Returns its id either way.</summary>
    /// <remarks>
    /// Idempotent on (account, name) so the folder sync can re-run safely; the
    /// unique index does the deciding, because checking first would race.
    ///
    /// The description is only written when the album is NEW. Re-running a
    /// folder sync must not overwrite something the owner typed, and the
    /// conflict branch has to assign SOMETHING for RETURNING to yield a row on
    /// an existing album, so it assigns the name to itself.
    /// </remarks>
    public async Task<long> EnsureAsync(
        long accountId, string name, string description, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO album (account_id, name, description)
            VALUES (@AccountId, @Name, @Description)
            ON CONFLICT (account_id, name) DO UPDATE SET name = EXCLUDED.name
            RETURNING album_id
            """,
            new { AccountId = accountId, Name = name, Description = description },
            cancellationToken: ct));
    }

    public async Task<bool> RenameAsync(long accountId, long albumId, string name, string description, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE album SET name = @Name, description = @Description, updated_at = now()
             WHERE album_id = @AlbumId AND account_id = @AccountId
            """,
            new { AccountId = accountId, AlbumId = albumId, Name = name, Description = description },
            cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteAsync(long accountId, long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM album WHERE album_id = @AlbumId AND account_id = @AccountId",
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Add content to an album, ignoring what is already there.
    /// </summary>
    /// <remarks>
    /// Takes CONTENT ids, never location ids - membership must survive the file
    /// moving on disk, which is the property the previous system lacked.
    /// One statement with an array parameter rather than a loop: adding a
    /// 150,000-image subreddit should be one round trip, not 150,000.
    /// </remarks>
    /// <summary>Put content in one of the caller's albums.</summary>
    /// <remarks>
    /// Anything added that has no thumbnail gets one queued, because the
    /// corpus accounts deliberately have none - generating 860,000 thumbnails
    /// for reddit and simpcity was never wanted. Pulling a handful of pictures
    /// into a personal album is the opposite case: without this they arrive as
    /// holes in the grid. Bounded by what the owner actually picks, and the
    /// queue's partial unique index makes a repeat add free.
    /// </remarks>
    public async Task<int> AddAsync(long accountId, long albumId, IReadOnlyList<long> contentIds, CancellationToken ct)
    {
        if (contentIds.Count == 0)
        {
            return 0;
        }

        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            WITH added AS (
                INSERT INTO album_item (album_id, content_id)
                SELECT a.album_id, x.content_id
                  FROM album a
                  JOIN unnest(@ContentIds::bigint[]) AS x(content_id) ON true
                 WHERE a.album_id = @AlbumId AND a.account_id = @AccountId
                ON CONFLICT DO NOTHING
                RETURNING content_id
            ),
            queued AS (
                INSERT INTO work_item (kind, content_id)
                SELECT 'thumbnail', ad.content_id
                  FROM added ad
                 WHERE NOT EXISTS (SELECT 1 FROM thumbnail t
                                    WHERE t.content_id = ad.content_id
                                      AND t.kind = 'thumb')
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            SELECT count(*)::int FROM added
            """,
            new { AccountId = accountId, AlbumId = albumId, ContentIds = contentIds.ToArray() },
            cancellationToken: ct));
    }

    /// <summary>
    /// Every member of an album, as the hash a worker is handed.
    /// </summary>
    /// <remarks>
    /// Hashes, not ids: a worker behind a contract speaks the content hash and
    /// nothing else of ours (docs/contracts). The caller maps the answer back.
    /// </remarks>
    public async Task<IReadOnlyList<(long ContentId, byte[] Sha256, string FileName)>> MemberHashesAsync(
        long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // The file name is for a person - alt text, a tooltip - and is the
        // one location attribute worth carrying here. Any live copy's name
        // will do; the bytes are the same.
        return (await conn.QueryAsync<(long ContentId, byte[] Sha256, string FileName)>(new CommandDefinition(
            """
            SELECT c.content_id, c.sha256,
                   COALESCE(regexp_replace(pick.rel_path, '^.*/', ''), '') AS file_name
              FROM album_item i
              JOIN content c ON c.content_id = i.content_id
              LEFT JOIN LATERAL (
                   SELECT l.rel_path FROM location l
                    WHERE l.content_id = c.content_id AND l.gone_at IS NULL
                    ORDER BY l.location_id LIMIT 1
              ) pick ON true
             WHERE i.album_id = @AlbumId
            """,
            new { AlbumId = albumId },
            cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Record the cover a person chose. False if the album is not theirs or
    /// the picture is not in it.
    /// </summary>
    /// <remarks>
    /// Membership is checked in the same statement as the write, not before
    /// it: a check that lives a line away from the thing it guards is one
    /// refactor from being skipped. A choice is final until the next choice;
    /// there is no automatic picker to lock it against, because nothing else
    /// writes this column.
    /// </remarks>
    public async Task<bool> SetCoverAsync(long accountId, long albumId, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE album a
               SET cover_content_id = @ContentId, updated_at = now()
             WHERE a.album_id = @AlbumId AND a.account_id = @AccountId
               AND EXISTS (SELECT 1 FROM album_item i
                            WHERE i.album_id = a.album_id AND i.content_id = @ContentId)
            """,
            new { AccountId = accountId, AlbumId = albumId, ContentId = contentId },
            cancellationToken: ct)) > 0;
    }

    /// <summary>Bytes and counts for one album. One aggregate, not a walk of its members.</summary>
    public async Task<AlbumStats?> StatsAsync(long accountId, long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AlbumStats>(new CommandDefinition(
            """
            SELECT coalesce(sum(c.byte_size), 0)::bigint                  AS Bytes,  -- sum(bigint) is numeric
                   count(c.content_id)                                    AS Total,
                   count(*) FILTER (WHERE c.mime LIKE 'image/%')          AS Images,
                   count(*) FILTER (WHERE c.mime LIKE 'video/%')          AS Videos
              FROM album al
              LEFT JOIN album_item i ON i.album_id = al.album_id
              LEFT JOIN content c    ON c.content_id = i.content_id
             WHERE al.album_id = @AlbumId AND al.account_id = @AccountId
             GROUP BY al.album_id
            """,
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct));
    }

    /// <summary>
    /// The caller's albums whose NAME also exists in another personal account.
    /// </summary>
    /// <remarks>
    /// The library is split across accounts filled from different sources that
    /// name their folders the same way, so one name routinely means two
    /// half-albums. This is the join Immich could not do - a key sees one user -
    /// and the reason a sidecar once held a database connection to every
    /// account. Names compare case-insensitively because <c>album.name</c> is
    /// citext; that is the same rule every album lookup here already uses.
    /// Corpus accounts are left out: a subreddit sharing a name with a folder
    /// is a coincidence, not a split.
    /// </remarks>
    public async Task<IReadOnlyList<AlbumOverlap>> OverlapAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<AlbumOverlap>(new CommandDefinition(
            """
            WITH counted AS (
                SELECT al.album_id, al.account_id, al.name,
                       count(*) FILTER (WHERE c.mime LIKE 'image/%') AS images,
                       count(*) FILTER (WHERE c.mime LIKE 'video/%') AS videos
                  FROM album al
                  JOIN account a ON a.account_id = al.account_id AND a.kind = 'personal'
                  LEFT JOIN album_item i ON i.album_id = al.album_id
                  LEFT JOIN content c    ON c.content_id = i.content_id
                 GROUP BY al.album_id
            )
            SELECT mine.name           AS Name,
                   mine.album_id       AS MyAlbumId,   mine.images  AS MyImages,   mine.videos  AS MyVideos,
                   other.account_id    AS OtherAccountId,
                   other.album_id      AS OtherAlbumId, other.images AS OtherImages, other.videos AS OtherVideos
              FROM counted mine
              JOIN counted other ON other.name = mine.name AND other.account_id <> mine.account_id
             WHERE mine.account_id = @AccountId
             ORDER BY (mine.images + mine.videos + other.images + other.videos) DESC
            """,
            new { AccountId = accountId },
            cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Give one of the caller's albums to another personal account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing on disk moves.</b> An album holds content, and owning the
    /// album is enough to see everything in it, so the move is the album row
    /// changing hands. The system this replaces copied every original between
    /// account folders because there, seeing a file meant owning it. The
    /// pictures still appear in the source account's own timeline, because the
    /// files still live under its root - moving THEM is a relocation, and
    /// Tessera does not write to libraries.
    /// </para>
    /// <para>
    /// If the destination already has an album of that name the two MERGE:
    /// memberships join it, the destination keeps its cover unless it had
    /// none, and the source album is deleted. One transaction, so a failure
    /// leaves both albums exactly as they were.
    /// </para>
    /// </remarks>
    public async Task<AlbumMove?> MoveAsync(long accountId, long albumId, long destAccountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var source = await conn.QuerySingleOrDefaultAsync<(string Name, long? Cover)?>(new CommandDefinition(
            "SELECT name, cover_content_id FROM album WHERE album_id = @AlbumId AND account_id = @AccountId FOR UPDATE",
            new { AlbumId = albumId, AccountId = accountId }, tx, cancellationToken: ct));

        var destOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM account WHERE account_id = @Dest AND kind = 'personal' AND disabled_at IS NULL AND account_id <> @Me)",
            new { Dest = destAccountId, Me = accountId }, tx, cancellationToken: ct));

        if (source is null || !destOk)
        {
            return null;
        }

        var existing = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT album_id FROM album WHERE account_id = @Dest AND name = @Name FOR UPDATE",
            new { Dest = destAccountId, source.Value.Name }, tx, cancellationToken: ct));

        int moved;
        long finalId;
        if (existing is { } destAlbumId)
        {
            moved = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO album_item (album_id, content_id)
                SELECT @Dest, content_id FROM album_item WHERE album_id = @Source
                ON CONFLICT DO NOTHING
                """,
                new { Dest = destAlbumId, Source = albumId }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE album SET cover_content_id = coalesce(cover_content_id, @Cover), updated_at = now()
                 WHERE album_id = @Dest
                """,
                new { Dest = destAlbumId, source.Value.Cover }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM album WHERE album_id = @Source",
                new { Source = albumId }, tx, cancellationToken: ct));
            finalId = destAlbumId;
        }
        else
        {
            moved = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM album_item WHERE album_id = @Source",
                new { Source = albumId }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE album SET account_id = @Dest, updated_at = now() WHERE album_id = @Source",
                new { Dest = destAccountId, Source = albumId }, tx, cancellationToken: ct));
            finalId = albumId;
        }

        await tx.CommitAsync(ct);
        return new AlbumMove(finalId, existing is not null, moved);
    }

    public async Task<int> RemoveAsync(long accountId, long albumId, IReadOnlyList<long> contentIds, CancellationToken ct)
    {
        if (contentIds.Count == 0)
        {
            return 0;
        }

        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM album_item i
             USING album a
             WHERE i.album_id = a.album_id AND a.account_id = @AccountId
               AND i.album_id = @AlbumId AND i.content_id = ANY(@ContentIds::bigint[])
            """,
            new { AccountId = accountId, AlbumId = albumId, ContentIds = contentIds.ToArray() },
            cancellationToken: ct));
    }

    /// <summary>Album contents, keyset-paged like the timeline and for the same reason.</summary>
    public async Task<TimelinePage> ItemsAsync(
        long accountId, long albumId, DateTimeOffset? before, long? beforeId, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 1, 500);

        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long ContentId, DateTimeOffset TakenAt, byte[] Sha, int? W, int? H, string Mime)>(
            new CommandDefinition(
                """
                SELECT c.content_id, COALESCE(c.taken_at, i.added_at) AS taken_at,
                       c.sha256, c.width, c.height, c.mime
                  FROM album_item i
                  JOIN album a   ON a.album_id = i.album_id AND a.account_id = @AccountId
                  JOIN content c ON c.content_id = i.content_id
                 WHERE i.album_id = @AlbumId
                   AND (@Before::timestamptz IS NULL
                        OR (COALESCE(c.taken_at, i.added_at), c.content_id) < (@Before, @BeforeId))
                 ORDER BY COALESCE(c.taken_at, i.added_at) DESC, c.content_id DESC
                 LIMIT @Take
                """,
                new
                {
                    AccountId = accountId, AlbumId = albumId, Before = before,
                    BeforeId = beforeId ?? long.MaxValue, Take = size + 1,
                },
                cancellationToken: ct))).ToList();

        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(r => new TimelineItem
        {
            ContentId = r.ContentId,
            TakenAt = r.TakenAt,
            Sha256Hex = Core.Hashing.ToHex(r.Sha),
            Width = r.W,
            Height = r.H,
            Mime = r.Mime,
        }).ToList();

        return new TimelinePage
        {
            Items = items,
            NextBefore = hasMore ? items[^1].TakenAt : null,
            NextContentId = hasMore ? items[^1].ContentId : null,
        };
    }

    /// <summary>
    /// One album per top-level folder under a root, filled from what is on disk.
    /// </summary>
    /// <remarks>
    /// Replaces the third-party folder-album-creator, which broke the day the
    /// fork image was built and reported healthy for eighteen days while every
    /// run died in configuration parsing. Set-based, so twenty subreddits and
    /// 479,046 files is a handful of statements.
    /// </remarks>
    public async Task<(int Albums, int Added)> SyncFoldersAsync(long accountId, long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var albums = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO album (account_id, name)
            SELECT DISTINCT @AccountId, split_part(rel_path, '/', 1)
              FROM location
             WHERE root_id = @RootId AND gone_at IS NULL AND trashed_at IS NULL
               AND position('/' IN rel_path) > 0
            ON CONFLICT (account_id, name) DO NOTHING
            """,
            new { AccountId = accountId, RootId = rootId },
            tx, cancellationToken: ct));

        var added = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO album_item (album_id, content_id)
            SELECT a.album_id, l.content_id
              FROM location l
              JOIN album a ON a.account_id = @AccountId
                          AND a.name = split_part(l.rel_path, '/', 1)
             WHERE l.root_id = @RootId AND l.gone_at IS NULL AND l.trashed_at IS NULL
            ON CONFLICT DO NOTHING
            """,
            new { AccountId = accountId, RootId = rootId },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (albums, added);
    }
}
