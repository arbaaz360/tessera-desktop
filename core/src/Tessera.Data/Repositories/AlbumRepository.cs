using System.Text.Json;
using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>Albums, and the one fact about them that is derived: the cover shown.</summary>
/// <remarks>
/// <para>
/// <b>The cover has one owner: <c>album.cover_content_id</c>.</b> It holds a
/// person's choice and nothing else writes it. When it is unset the album
/// still needs a picture, so every read also returns
/// <see cref="Album.ShownCoverContentId"/>: the choice if there is one,
/// otherwise A MEMBER of the album - the newest one that has a thumbnail, so
/// a cover is never a blank tile while the queue catches up.
/// </para>
/// <para>
/// Chosen by primary key, not ranked: cheap and STABLE, so the same album
/// shows the same picture every time, which is what makes a grid feel solid.
/// </para>
/// </remarks>
public sealed class AlbumRepository(Db db)
{
    // A member counts, and may be the cover, while it is LIVE: a location
    // neither trashed nor gone, which is exactly a timeline_entry row. A
    // picture whose folder left the library stays a member (put the folder
    // back and it returns), but the album no longer claims it.
    private const string AlbumColumns = """
        SELECT a.album_id AS AlbumId, a.account_id AS AccountId, a.name AS Name,
               a.description AS Description, a.cover_content_id AS CoverContentId,
               COALESCE(a.cover_content_id,
                        (SELECT i.content_id FROM album_item i
                           JOIN timeline_entry t ON t.account_id = a.account_id AND t.content_id = i.content_id
                          WHERE i.album_id = a.album_id
                            AND EXISTS (SELECT 1 FROM thumbnail th WHERE th.content_id = i.content_id)
                          ORDER BY i.content_id DESC LIMIT 1),
                        (SELECT i.content_id FROM album_item i
                           JOIN timeline_entry t ON t.account_id = a.account_id AND t.content_id = i.content_id
                          WHERE i.album_id = a.album_id
                          ORDER BY i.content_id DESC LIMIT 1)) AS ShownCoverContentId,
               a.created_at AS CreatedAt, a.updated_at AS UpdatedAt, a.origin AS Origin,
               (SELECT count(*) FROM album_item i
                  JOIN timeline_entry t ON t.account_id = a.account_id AND t.content_id = i.content_id
                 WHERE i.album_id = a.album_id) AS ItemCount
          FROM album a
        """;

    /// <summary>
    /// The folder albums nothing holds any more: no root that made them is
    /// left, and none of their pictures is live. Deleted when a root goes and
    /// once at upgrade (migration 0003), by this one rule. A person's album is
    /// never touched, and neither is one with a live picture.
    /// </summary>
    internal const string DeleteLeftoverFolderAlbumsSql = """
        DELETE FROM album
         WHERE origin = 'folder'
           AND NOT EXISTS (SELECT 1 FROM album_root ar WHERE ar.album_id = album.album_id)
           AND NOT EXISTS (SELECT 1 FROM album_item i
                             JOIN timeline_entry t ON t.account_id = album.account_id AND t.content_id = i.content_id
                            WHERE i.album_id = album.album_id)
        """;

    /// <summary>
    /// The account's albums; with <paramref name="containing"/>, only those
    /// that hold that content (the info panel's "which album is this in").
    /// </summary>
    public async Task<IReadOnlyList<Album>> ListAsync(long accountId, CancellationToken ct, long? containing = null)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Album>(new CommandDefinition(
            AlbumColumns + """

             WHERE a.account_id = @AccountId
               AND (@Containing IS NULL OR EXISTS (
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
            AlbumColumns + """

             WHERE a.account_id = @AccountId AND a.album_id = @AlbumId
            """,
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct));
    }

    /// <summary>A person makes an album, or names one that exists. Returns its id either way.</summary>
    /// <remarks>
    /// Idempotent on (account, name); the unique index does the deciding,
    /// because checking first would race. The description is only written
    /// when the album is NEW, so naming an existing album never overwrites
    /// what someone typed. Naming a folder's album takes it over: it is a
    /// person's from then on and the app never deletes it.
    /// </remarks>
    public async Task<long> EnsureAsync(
        long accountId, string name, string description, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO album (account_id, name, description)
            VALUES (@AccountId, @Name, @Description)
            ON CONFLICT (account_id, name) DO UPDATE SET origin = 'manual'
            RETURNING album_id
            """,
            new { AccountId = accountId, Name = name, Description = description },
            cancellationToken: ct));
    }

    /// <summary>
    /// A person edits an album (the PATCH: name, description, cover). An
    /// edited folder album is theirs from then on, like one they made.
    /// </summary>
    public async Task<bool> RenameAsync(long accountId, long albumId, string name, string description, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE album SET name = @Name, description = @Description, origin = 'manual', updated_at = {Db.NowMs}
             WHERE album_id = @AlbumId AND account_id = @AccountId
            """,
            new { AccountId = accountId, AlbumId = albumId, Name = name, Description = description },
            cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteAsync(long accountId, long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM album WHERE album_id = @AlbumId AND account_id = @AccountId",
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Put content in one of the caller's albums, ignoring what is already there.
    /// </summary>
    /// <remarks>
    /// Takes CONTENT ids, never location ids - membership must survive the file
    /// moving on disk. One statement over a JSON array rather than a loop:
    /// adding a thousand pictures is one round trip.
    /// </remarks>
    /// <returns>How many were new to the album.</returns>
    public async Task<int> AddAsync(long accountId, long albumId, IReadOnlyList<long> contentIds, CancellationToken ct)
    {
        if (contentIds.Count == 0)
        {
            return 0;
        }

        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO album_item (album_id, content_id)
            SELECT a.album_id, x.value
              FROM album a, json_each(@ContentIds) x
             WHERE a.album_id = @AlbumId AND a.account_id = @AccountId
               AND EXISTS (SELECT 1 FROM content c WHERE c.content_id = x.value)
            ON CONFLICT DO NOTHING
            """,
            new { AccountId = accountId, AlbumId = albumId, ContentIds = JsonSerializer.Serialize(contentIds) },
            cancellationToken: ct));
    }

    /// <summary>
    /// Put every live picture under one folder of a root (or the whole root)
    /// into an album: what dropping a folder onto an album means.
    /// </summary>
    /// <returns>How many were new to the album.</returns>
    public async Task<int> AddUnderAsync(long accountId, long albumId, long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO album_item (album_id, content_id)
            SELECT DISTINCT a.album_id, l.content_id
              FROM album a
              JOIN location l ON l.root_id = @RootId AND l.account_id = a.account_id
                             AND l.gone_at IS NULL AND l.trashed_at IS NULL
             WHERE a.album_id = @AlbumId AND a.account_id = @AccountId
               AND (@Folder IS NULL OR substr(l.rel_path, 1, length(@Folder) + 1) = @Folder || '/')
            ON CONFLICT DO NOTHING
            """,
            new { AccountId = accountId, AlbumId = albumId, RootId = rootId, Folder = folder },
            commandTimeout: 600, cancellationToken: ct));
    }

    /// <summary>Every member of an album: content id, hash, and a file name for a person.</summary>
    public async Task<IReadOnlyList<(long ContentId, byte[] Sha256, string FileName)>> MemberHashesAsync(
        long albumId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long ContentId, byte[] Sha256, string? RelPath)>(new CommandDefinition(
            """
            SELECT c.content_id, c.sha256,
                   (SELECT l.rel_path FROM location l
                     WHERE l.content_id = c.content_id AND l.gone_at IS NULL
                     ORDER BY l.location_id LIMIT 1) AS rel_path
              FROM album_item i
              JOIN content c ON c.content_id = i.content_id
             WHERE i.album_id = @AlbumId
            """,
            new { AlbumId = albumId },
            cancellationToken: ct));

        return rows.Select(r => (r.ContentId, r.Sha256, FileNameOf(r.RelPath))).ToList();
    }

    private static string FileNameOf(string? relPath) =>
        relPath is null ? "" : relPath[(relPath.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Record the cover a person chose. False if the album is not theirs or
    /// the picture is not in it. Membership is checked in the same statement
    /// as the write, not before it.
    /// </summary>
    public async Task<bool> SetCoverAsync(long accountId, long albumId, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE album
               SET cover_content_id = @ContentId, updated_at = {Db.NowMs}
             WHERE album_id = @AlbumId AND account_id = @AccountId
               AND EXISTS (SELECT 1 FROM album_item i
                            WHERE i.album_id = album.album_id AND i.content_id = @ContentId)
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
            SELECT coalesce(sum(c.byte_size), 0)                      AS Bytes,
                   count(c.content_id)                                AS Total,
                   count(*) FILTER (WHERE c.mime LIKE 'image/%')      AS Images,
                   count(*) FILTER (WHERE c.mime LIKE 'video/%')      AS Videos
              FROM album al
              LEFT JOIN album_item i ON i.album_id = al.album_id
              LEFT JOIN content c    ON c.content_id = i.content_id
             WHERE al.album_id = @AlbumId AND al.account_id = @AccountId
             GROUP BY al.album_id
            """,
            new { AccountId = accountId, AlbumId = albumId },
            cancellationToken: ct));
    }

    public async Task<int> RemoveAsync(long accountId, long albumId, IReadOnlyList<long> contentIds, CancellationToken ct)
    {
        if (contentIds.Count == 0)
        {
            return 0;
        }

        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM album_item
             WHERE album_id = @AlbumId
               AND content_id IN (SELECT value FROM json_each(@ContentIds))
               AND EXISTS (SELECT 1 FROM album a WHERE a.album_id = @AlbumId AND a.account_id = @AccountId)
            """,
            new { AccountId = accountId, AlbumId = albumId, ContentIds = JsonSerializer.Serialize(contentIds) },
            cancellationToken: ct));
    }

    /// <summary>Album contents, keyset-paged like the timeline and for the same reason.</summary>
    public async Task<TimelinePage> ItemsAsync(
        long accountId, long albumId, DateTimeOffset? before, long? beforeId, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 1, 500);

        var sql = before is null
            ? """
              SELECT c.content_id, COALESCE(c.taken_at, i.added_at) AS taken_at,
                     c.sha256, c.width, c.height, c.mime
                FROM album_item i
                JOIN album a   ON a.album_id = i.album_id AND a.account_id = @AccountId
                JOIN content c ON c.content_id = i.content_id
               WHERE i.album_id = @AlbumId
               ORDER BY COALESCE(c.taken_at, i.added_at) DESC, c.content_id DESC
               LIMIT @Take
              """
            : """
              SELECT c.content_id, COALESCE(c.taken_at, i.added_at) AS taken_at,
                     c.sha256, c.width, c.height, c.mime
                FROM album_item i
                JOIN album a   ON a.album_id = i.album_id AND a.account_id = @AccountId
                JOIN content c ON c.content_id = i.content_id
               WHERE i.album_id = @AlbumId
                 AND (COALESCE(c.taken_at, i.added_at), c.content_id) < (@Before, @BeforeId)
               ORDER BY COALESCE(c.taken_at, i.added_at) DESC, c.content_id DESC
               LIMIT @Take
              """;

        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long ContentId, long TakenAt, byte[] Sha, int? W, int? H, string Mime)>(
            new CommandDefinition(sql,
                new
                {
                    AccountId = accountId, AlbumId = albumId, Before = Db.ToMs(before),
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
            TakenAt = Db.FromMs(r.TakenAt),
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
}
