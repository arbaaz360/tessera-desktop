using Dapper;
using Tessera.Core;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

public sealed class TimelineRepository(Db db)
{
    /// <summary>
    /// One page of the grid, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KEYSET pagination, not OFFSET. At two hundred thousand rows an OFFSET
    /// page still walks every row it skips, so page 500 costs five hundred
    /// times page 1 and the grid gets slower the further you scroll - exactly
    /// where a photo library is used hardest.
    /// </para>
    /// <para>
    /// The cursor is (taken_at, content_id) rather than taken_at alone, because
    /// timestamps are not unique: thousands of these files share a second, and a
    /// cursor on time alone silently skips or repeats rows at the boundary.
    /// The index is declared in that same order so this is one range scan.
    /// Two statements rather than one with <c>@Before IS NULL OR</c>: SQLite
    /// does not plan a disjunction on a parameter as a range, and the
    /// difference is a full index walk at depth.
    /// </para>
    /// </remarks>
    public async Task<TimelinePage> PageAsync(
        long accountId,
        DateTimeOffset? before,
        long? beforeContentId,
        int size,
        CancellationToken ct)
    {
        size = Math.Clamp(size, 1, 500);

        var sql = before is null
            ? """
              SELECT t.content_id, t.taken_at, c.sha256, c.width, c.height, c.mime
                FROM timeline_entry t
                JOIN content c ON c.content_id = t.content_id
               WHERE t.account_id = @AccountId
               ORDER BY t.taken_at DESC, t.content_id DESC
               LIMIT @Take
              """
            : """
              SELECT t.content_id, t.taken_at, c.sha256, c.width, c.height, c.mime
                FROM timeline_entry t
                JOIN content c ON c.content_id = t.content_id
               WHERE t.account_id = @AccountId
                 AND (t.taken_at, t.content_id) < (@Before, @BeforeId)
               ORDER BY t.taken_at DESC, t.content_id DESC
               LIMIT @Take
              """;

        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(long ContentId, long TakenAt, byte[] Sha, int? W, int? H, string Mime)>(
            new CommandDefinition(sql,
                new
                {
                    AccountId = accountId,
                    Before = Db.ToMs(before),
                    BeforeId = beforeContentId ?? long.MaxValue,
                    Take = size + 1,   // one extra: is there another page?
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
            Sha256Hex = Hashing.ToHex(r.Sha),
            Width = r.W,
            Height = r.H,
            Mime = r.Mime,
        }).ToList();

        return new TimelinePage
        {
            Items = items,
            // Asking for size+1 is what makes "that was the last page" different
            // from "there might be more".
            NextBefore = hasMore ? items[^1].TakenAt : null,
            NextContentId = hasMore ? items[^1].ContentId : null,
        };
    }

    public async Task<long> CountAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM timeline_entry WHERE account_id = @AccountId",
            new { AccountId = accountId },
            cancellationToken: ct));
    }
}
