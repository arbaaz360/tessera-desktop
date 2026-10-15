using System.Text.Json;
using Dapper;
using Tessera.Data;

namespace Tessera.Api.Endpoints;

/// <summary>One live picture as the grid needs it.</summary>
internal sealed record AssetRow
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

/// <summary>
/// The asset shape the web app renders, in one place: the bucket's columns
/// and the per-item object search answers with (CONTRACT §4: a search item
/// is exactly a bucket entry, plus a score).
/// </summary>
internal static class AssetShapes
{
    /// <summary>The columns, over a source aliased <c>t</c> (account_id, content_id, taken_at) joined to <c>c</c> (content) and <c>th</c> (thumbnail).</summary>
    public const string Columns = """
        t.content_id AS ContentId,
        COALESCE(c.width, th.width) AS Width, COALESCE(c.height, th.height) AS Height,
        c.mime AS Mime, c.duration_ms AS DurationMs,
        t.taken_at AS TakenAt, c.first_seen AS FirstSeen,
        EXISTS (SELECT 1 FROM favorite fv
                 WHERE fv.account_id = @Account AND fv.content_id = t.content_id) AS IsFavorite
        """;

    /// <summary>Aspect ratio, 1 when the dimensions were never recorded.</summary>
    public static double Ratio(AssetRow r) =>
        r.Width is > 0 && r.Height is > 0 ? (double)r.Width.Value / r.Height.Value : 1.0;

    public static bool IsImage(AssetRow r) => r.Mime.StartsWith("image/", StringComparison.Ordinal);

    /// <summary>One asset as an object: the bucket's columns transposed, plus an optional score.</summary>
    public static object Item(AssetRow r, long accountId, bool trashed, float? score) => new
    {
        id = Api.Id(r.ContentId),
        ownerId = Api.Id(accountId),
        ratio = Ratio(r),
        isFavorite = r.IsFavorite,
        visibility = "timeline",
        isTrashed = trashed,
        isImage = IsImage(r),
        thumbhash = (string?)null,
        createdAt = Api.Iso(r.FirstSeen),
        fileCreatedAt = Api.Iso(r.TakenAt),
        localOffsetHours = 0.0,
        duration = r.DurationMs,
        projectionType = (string?)null,
        livePhotoVideoId = (string?)null,
        score,
    };

    /// <summary>The live rows for these ids, from the timeline. An id that is trashed or gone is absent.</summary>
    public static async Task<Dictionary<long, AssetRow>> LiveRowsAsync(Db db, long accountId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AssetRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
              FROM timeline_entry t
              JOIN content c ON c.content_id = t.content_id
              LEFT JOIN thumbnail th ON th.content_id = t.content_id AND th.kind = 'thumb'
             WHERE t.account_id = @Account
               AND t.content_id IN (SELECT value FROM json_each(@Ids))
            """,
            new { Account = accountId, Ids = JsonSerializer.Serialize(ids) },
            cancellationToken: ct));
        return rows.ToDictionary(r => r.ContentId);
    }
}
