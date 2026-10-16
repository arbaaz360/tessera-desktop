using System.Diagnostics;
using Dapper;
using Tessera.Data;
using Xunit.Abstractions;

namespace Tessera.Tests;

/// <summary>
/// <c>GET /api/library</c> is polled every two seconds while a scan runs, and
/// it now counts where 200,000 pictures got their dates. Printed, with a
/// generous bound: the backfill's queue is a partial index, so "how much is
/// left" costs nothing once it is done; the by-source count is one pass.
/// </summary>
public sealed class DateCountsAtScaleTests(ITestOutputHelper output)
{
    private const int Rows = 200_000;

    [Fact]
    public async Task Date_counts_and_the_backfill_queue_stay_cheap_at_200k()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var seed = Stopwatch.StartNew();
        await SeedAsync(lib.Db, root);
        output.WriteLine($"seeded {Rows:N0} contents and locations in {seed.ElapsedMilliseconds:N0} ms");

        var (counts, countMs) = await BestOf(() => lib.Content.DateCountsAsync(CancellationToken.None));
        var (pending, pendingMs) = await BestOf(() => lib.Content.MetadataPendingAsync(0, 200, CancellationToken.None));
        output.WriteLine($"DateCountsAsync, 1/10 unread: {countMs:F1} ms; a backfill batch of 200: {pendingMs:F1} ms");
        Assert.Equal(Rows / 10, counts.Pending);
        Assert.Equal(Rows - Rows / 10, counts.Done);
        Assert.Equal(200, pending.Count);

        // The backfill finished: the queue is empty and asking is free.
        await using (var conn = await lib.Db.OpenWriteAsync(CancellationToken.None))
        {
            await conn.ExecuteAsync("UPDATE content SET meta_version = 1 WHERE meta_version = 0");
        }

        var (empty, emptyMs) = await BestOf(() => lib.Content.MetadataPendingAsync(0, 200, CancellationToken.None));
        var (_, doneCountMs) = await BestOf(() => lib.Content.DateCountsAsync(CancellationToken.None));
        output.WriteLine($"after the backfill: an empty batch {emptyMs:F2} ms, DateCountsAsync {doneCountMs:F1} ms");
        Assert.Empty(empty);
        Assert.True(emptyMs < 20, $"empty backfill batch took {emptyMs:F1} ms");
        Assert.True(doneCountMs < 1000, $"date counts took {doneCountMs:F1} ms");
    }

    private static async Task<(T Result, double Ms)> BestOf<T>(Func<Task<T>> run)
    {
        var best = double.MaxValue;
        T result = default!;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            result = await run();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return (result, best);
    }

    /// <summary>Contents with one live location each: 60% EXIF, 20% file name, 10% file time, 10% never read.</summary>
    private static async Task SeedAsync(Db db, long root)
    {
        await using var conn = await db.OpenWriteAsync(CancellationToken.None);
        await using var tx = conn.BeginTransaction(deferred: false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @Rows)
            INSERT INTO content (content_id, sha256, byte_size, mime, taken_at, taken_src, meta_version)
            SELECT i, randomblob(32), 1000 + i, 'image/jpeg', 1420070400000 + i * 60000,
                   CASE WHEN i % 10 < 6 THEN 'exif' WHEN i % 10 < 8 THEN 'filename' ELSE 'mtime' END,
                   CASE WHEN i % 10 = 9 THEN 0 ELSE 1 END
              FROM n
            """, new { Rows }, tx, commandTimeout: 600));
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO location (content_id, account_id, root_id, rel_path)
            SELECT content_id, 1, @Root, 'f/' || content_id || '.jpg' FROM content
            """, new { Root = root }, tx, commandTimeout: 600));
        await tx.CommitAsync();
    }
}
