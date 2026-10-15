using Dapper;
using Tessera.Core;

namespace Tessera.Data.Repositories;

/// <summary>What a caller needs to serve a picture they are allowed to see.</summary>
/// <param name="ContentId">Tessera's identity for the bytes.</param>
/// <param name="Sha">The hash, which is also the thumbnail's name.</param>
/// <param name="Mime">Content type of the original.</param>
/// <param name="AbsolutePath">A live path to the bytes, or null if every copy is gone.</param>
/// <param name="Width">Pixel width of the original, when known.</param>
/// <param name="Height">Pixel height of the original, when known.</param>
public readonly record struct ViewableContent(
    long ContentId, byte[] Sha, string Mime, string? AbsolutePath, int? Width, int? Height);

/// <summary>
/// The single definition of "may this account see this content".
/// </summary>
/// <remarks>
/// <para>
/// There were three copies of this rule: the native media route, and two in
/// the Immich compatibility layer. Each was a hand-written SQL clause saying
/// the same thing, and each carried a comment claiming it matched the others.
/// A comment is not a mechanism. The first time one of them gained a case the
/// others did not - a shared album, a trashed flag, a partner account - the
/// three would disagree about who can see a photo, and nothing would report it.
/// </para>
/// <para>
/// This is the §5 hazard in the house rules, authored inside the very project
/// built to remove it. So the rule lives here, once, and the callers ask.
/// </para>
/// <para>
/// The rule itself: you may see content you hold a live location for, or
/// content you have put in an album you own. The second clause is what makes
/// pulling a picture out of a corpus account into a personal album work at all.
/// </para>
/// </remarks>
public sealed class ViewableContentRepository(Db db)
{
    /// <summary>
    /// Resolve a content the account may see, with a live path if one exists.
    /// </summary>
    /// <remarks>
    /// The permission test and the fetch are ONE statement, not a check
    /// followed by a read: an authorisation step that lives a line away from
    /// the thing it guards is one refactor from being skipped.
    ///
    /// The path may come from any live location, the caller's own preferred.
    /// The bytes are identical - that is what the hash means - so which copy is
    /// opened is a question about disks, not about permission.
    /// </remarks>
    public async Task<ViewableContent?> GetAsync(long accountId, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(byte[] Sha, string Mime, string? Root, string? Rel, int? Width, int? Height)?>(
            new CommandDefinition(
                """
                SELECT c.sha256, c.mime, r.abs_path, pick.rel_path, c.width, c.height
                  FROM content c
                  LEFT JOIN LATERAL (
                       SELECT l.root_id, l.rel_path
                         FROM location l
                        WHERE l.content_id = c.content_id
                          AND l.gone_at IS NULL AND l.trashed_at IS NULL
                        ORDER BY (l.account_id = @Account) DESC, l.location_id
                        LIMIT 1
                  ) pick ON true
                  LEFT JOIN scan_root r ON r.root_id = pick.root_id
                 WHERE c.content_id = @Content
                   AND (EXISTS (SELECT 1 FROM location l
                                 WHERE l.content_id = c.content_id
                                   AND l.account_id = @Account
                                   AND l.gone_at IS NULL AND l.trashed_at IS NULL)
                     OR EXISTS (SELECT 1 FROM album_item ai
                                  JOIN album al ON al.album_id = ai.album_id
                                 WHERE ai.content_id = c.content_id
                                   AND al.account_id = @Account))
                """,
                new { Account = accountId, Content = contentId },
                cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        var abs = row.Value.Root is null || row.Value.Rel is null
            ? null
            : PathRules.Canonical($"{row.Value.Root}/{row.Value.Rel}");

        return new ViewableContent(contentId, row.Value.Sha, row.Value.Mime, abs, row.Value.Width, row.Value.Height);
    }

    /// <summary>
    /// Record the original's pixel size, once. Never overwrites a known value.
    /// </summary>
    /// <remarks>
    /// The library came over from Immich without dimensions on the content
    /// row; only pictures the thumbnail worker has handled since carry them.
    /// Rather than a pass over two million files on the archive disk, a
    /// picture learns its size the first time its original is served: the
    /// file is being opened anyway, and reading the header costs nothing on
    /// top of streaming it.
    /// </remarks>
    public async Task RecordDimensionsAsync(long contentId, int width, int height, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE content SET width = COALESCE(width, @W), height = COALESCE(height, @H)
             WHERE content_id = @ContentId
            """,
            new { ContentId = contentId, W = width, H = height },
            cancellationToken: ct));
    }

    /// <summary>
    /// A live path and mime for content, with no permission test.
    /// </summary>
    /// <remarks>
    /// For the worker surface only, where a signed token has already settled
    /// who may read this - the token names one content and Tessera minted it.
    /// Every human-facing route goes through <see cref="GetAsync"/> instead.
    /// </remarks>
    public async Task<(string AbsolutePath, string Mime)?> AnyLivePathAsync(long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Root, string Rel, string Mime)?>(new CommandDefinition(
            """
            SELECT r.abs_path, l.rel_path, c.mime
              FROM content c
              JOIN location l ON l.content_id = c.content_id AND l.gone_at IS NULL AND l.trashed_at IS NULL
              JOIN scan_root r ON r.root_id = l.root_id
             WHERE c.content_id = @Content
             ORDER BY l.location_id LIMIT 1
            """,
            new { Content = contentId }, cancellationToken: ct));
        return row is null ? null : (PathRules.Canonical($"{row.Value.Root}/{row.Value.Rel}"), row.Value.Mime);
    }

    /// <summary>
    /// Turn whatever id a caller holds into a content id.
    /// </summary>
    /// <remarks>
    /// Two spellings are legitimate, and both are THIS system's: the id this
    /// server issued to the web app, and the content hash itself - 64 hex
    /// characters - which is what every worker and upstream on this machine
    /// answers in (decision 2026-09-11). There used to be a third, an Immich
    /// asset uuid resolved through a translation table; FaceSearch and
    /// Foundation now carry the hash, so nothing hands those out any more and
    /// the table is gone.
    /// </remarks>
    public async Task<long?> ResolveIdAsync(string id, CancellationToken ct)
    {
        if (ImmichId.TryParse(id, out var own))
        {
            return own;
        }

        if (!Hashing.TryParseSha256(id, out var sha))
        {
            return null;
        }

        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT content_id FROM content WHERE sha256 = @Sha",
            new { Sha = sha },
            cancellationToken: ct));
    }

    /// <summary>
    /// Of these hashes, the ones the account may see, with their content ids.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="GetAsync"/> - a live copy of their own, or
    /// an album of theirs - applied to a set at once, for answers that come
    /// back from upstream in hashes. A hash that fails the rule is absent.
    /// </remarks>
    public async Task<Dictionary<string, long>> ResolveVisibleHashesAsync(
        long accountId, IReadOnlyCollection<byte[]> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(byte[] Sha, long ContentId)>(new CommandDefinition(
            """
            SELECT c.sha256, c.content_id
              FROM content c
             WHERE c.sha256 = ANY(@Hashes)
               AND (EXISTS (SELECT 1 FROM location l
                             WHERE l.content_id = c.content_id AND l.account_id = @Account
                               AND l.gone_at IS NULL AND l.trashed_at IS NULL)
                 OR EXISTS (SELECT 1 FROM album_item ai JOIN album al ON al.album_id = ai.album_id
                             WHERE ai.content_id = c.content_id AND al.account_id = @Account))
            """,
            new { Hashes = hashes.ToArray(), Account = accountId },
            cancellationToken: ct));

        return rows.ToDictionary(r => Convert.ToHexStringLower(r.Sha), r => r.ContentId, StringComparer.Ordinal);
    }

    /// <summary>Content ids for a set of hashes, in one round trip.</summary>
    /// <remarks>
    /// A hash this database does not hold is simply absent from the result -
    /// the caller checks its own list to tell "unknown here" from "not asked".
    /// </remarks>
    public async Task<Dictionary<string, long>> ResolveHashesAsync(
        IReadOnlyCollection<byte[]> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(byte[] Sha, long ContentId)>(new CommandDefinition(
            "SELECT sha256, content_id FROM content WHERE sha256 = ANY(@Hashes)",
            new { Hashes = hashes.ToArray() },
            cancellationToken: ct));

        return rows.ToDictionary(r => Convert.ToHexStringLower(r.Sha), r => r.ContentId, StringComparer.Ordinal);
    }
}
