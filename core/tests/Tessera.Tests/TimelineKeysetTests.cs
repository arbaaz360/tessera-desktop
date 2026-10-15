using System.Diagnostics;
using Dapper;
using Tessera.Data;
using Xunit.Abstractions;

namespace Tessera.Tests;

/// <summary>
/// The grid's one promise: a page costs the same at the bottom of 200,000
/// rows as at the top. Keyset, not offset, and the index declared in the
/// cursor's order. The numbers are printed; the assertion is generous so a
/// slow CI box does not fail it, but anything in the tens of milliseconds
/// at depth would mean the planner stopped using the index.
/// </summary>
public sealed class TimelineKeysetTests(ITestOutputHelper output)
{
    private const int Rows = 200_000;
    private const int PageSize = 200;

    [Fact]
    public async Task A_page_costs_the_same_at_any_depth()
    {
        using var lib = new TempLibrary();
        var seed = Stopwatch.StartNew();
        await SeedAsync(lib.Db);
        output.WriteLine($"seeded {Rows:N0} timeline rows in {seed.ElapsedMilliseconds:N0} ms");
        Assert.Equal(Rows, await lib.Timeline.CountAsync(TempLibrary.Account, CancellationToken.None));

        var results = new List<(string Depth, double Ms, int Items)>();
        foreach (var (name, offset) in new[] { ("0%", 0), ("50%", Rows / 2), ("100%", Rows - PageSize / 2) })
        {
            var (before, beforeId) = await CursorAtAsync(lib.Db, offset);
            var best = double.MaxValue;
            var items = 0;
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                var page = await lib.Timeline.PageAsync(TempLibrary.Account, before, beforeId, PageSize, CancellationToken.None);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                items = page.Items.Count;
            }

            results.Add((name, best, items));
        }

        foreach (var (depth, ms, items) in results)
        {
            output.WriteLine($"page at depth {depth}: {ms:F2} ms, {items} items");
        }

        Assert.Equal(PageSize, results[0].Items);
        Assert.Equal(PageSize, results[1].Items);
        Assert.Equal(PageSize / 2, results[2].Items);
        Assert.All(results, r => Assert.True(r.Ms < 100, $"page at depth {r.Depth} took {r.Ms:F1} ms"));

        // The last page is the last page: no cursor beyond it.
        var (lastBefore, lastId) = await CursorAtAsync(lib.Db, Rows - PageSize / 2);
        var tail = await lib.Timeline.PageAsync(TempLibrary.Account, lastBefore, lastId, PageSize, CancellationToken.None);
        Assert.Null(tail.NextBefore);

        // Two consecutive pages never share a row, even where timestamps collide.
        var first = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, PageSize, CancellationToken.None);
        var second = await lib.Timeline.PageAsync(TempLibrary.Account, first.NextBefore, first.NextContentId, PageSize, CancellationToken.None);
        Assert.Empty(first.Items.Select(i => i.ContentId).Intersect(second.Items.Select(i => i.ContentId)));
    }

    /// <summary>
    /// 200,000 contents and their timeline rows, by two set-based statements.
    /// Timestamps repeat deliberately (every 97th row shares a millisecond),
    /// so the cursor's second column has to do its job.
    /// </summary>
    private static async Task SeedAsync(Db db)
    {
        var baseMs = Db.ToMs(new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var conn = await db.OpenWriteAsync(CancellationToken.None);
        await using var tx = conn.BeginTransaction(deferred: false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @Rows)
            INSERT INTO content (sha256, byte_size, mime, width, height, taken_at, taken_src)
            SELECT randomblob(32), 100000 + i, 'image/jpeg', 1920, 1080,
                   @Base + ((i / 97) * 977) * 1000 + (i % 7), 'mtime'
              FROM n
            """,
            new { Rows, Base = baseMs }, tx, commandTimeout: 600));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO timeline_entry (account_id, content_id, taken_at) SELECT 1, content_id, taken_at FROM content",
            transaction: tx, commandTimeout: 600));
        await tx.CommitAsync();
    }

    private static async Task<(DateTimeOffset? Before, long? BeforeId)> CursorAtAsync(Db db, int offset)
    {
        if (offset == 0)
        {
            return (null, null);
        }

        await using var conn = await db.OpenAsync(CancellationToken.None);
        var row = await conn.QuerySingleAsync<(long TakenAt, long ContentId)>(
            """
            SELECT taken_at, content_id FROM timeline_entry WHERE account_id = 1
             ORDER BY taken_at DESC, content_id DESC LIMIT 1 OFFSET @Offset
            """,
            new { Offset = offset - 1 });
        return (Db.FromMs(row.TakenAt), row.ContentId);
    }
}
