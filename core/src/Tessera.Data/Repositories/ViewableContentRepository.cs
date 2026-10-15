using System.Globalization;
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
/// The rule: you may see content you hold a live location for, or content you
/// have put in an album you own. It lives here, once, and the callers ask. A
/// comment claiming three hand-written clauses agree is not a mechanism.
/// </remarks>
public sealed class ViewableContentRepository(Db db)
{
    /// <summary>
    /// Resolve a content the account may see, with a live path if one exists.
    /// </summary>
    /// <remarks>
    /// The permission test and the fetch are ONE statement, not a check
    /// followed by a read. The path may come from any live location; the
    /// bytes are identical - that is what the hash means.
    /// </remarks>
    public async Task<ViewableContent?> GetAsync(long accountId, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(byte[] Sha, string Mime, string? Root, string? Rel, int? Width, int? Height)?>(
            new CommandDefinition(
                """
                SELECT c.sha256, c.mime, r.abs_path, l.rel_path, c.width, c.height
                  FROM content c
                  LEFT JOIN location l ON l.location_id = (
                       SELECT l2.location_id FROM location l2
                        WHERE l2.content_id = c.content_id
                          AND l2.gone_at IS NULL AND l2.trashed_at IS NULL
                        ORDER BY (l2.account_id = @Account) DESC, l2.location_id
                        LIMIT 1)
                  LEFT JOIN scan_root r ON r.root_id = l.root_id
                 WHERE c.content_id = @Content
                   AND (EXISTS (SELECT 1 FROM location lx
                                 WHERE lx.content_id = c.content_id
                                   AND lx.account_id = @Account
                                   AND lx.gone_at IS NULL AND lx.trashed_at IS NULL)
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
    public async Task RecordDimensionsAsync(long contentId, int width, int height, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE content SET width = COALESCE(width, @W), height = COALESCE(height, @H)
             WHERE content_id = @ContentId
            """,
            new { ContentId = contentId, W = width, H = height },
            cancellationToken: ct));
    }

    /// <summary>
    /// Turn whatever id a caller holds into a content id.
    /// </summary>
    /// <remarks>
    /// Two spellings are legitimate: the decimal id this server issued, and
    /// the content hash itself - 64 hex characters. A value that is neither is
    /// refused rather than turned into an exception somewhere far from here.
    /// </remarks>
    public async Task<long?> ResolveIdAsync(string id, CancellationToken ct)
    {
        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var own))
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
}
