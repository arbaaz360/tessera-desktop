using Dapper;
using Tessera.Core;
using Tessera.Data;

namespace Tessera.Tests;

/// <summary>
/// The scanner over real folders, for the two things a scan used to get
/// wrong: a byte-identical second file counted as a move, and every picture
/// dated by its file time. Copies and moves are told apart by looking at the
/// disk; dates come from the headers at first sight, and from the backfill
/// for what was indexed before.
/// </summary>
public sealed class CopyAndDateScanTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeZoneInfo Zone = CaptureDateTests.Plus0530;

    // ------------------------------------------------------- copies and moves

    [Fact]
    public async Task A_fresh_folder_with_two_identical_files_is_one_added_and_one_copy()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        TempLibrary.WritePng(Path.Combine(photos, "a", "one.png"), seed: 1);
        File.Copy(Path.Combine(photos, "a", "one.png"), Path.Combine(photos, "a", "one copy.png"));
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);

        var scan = await lib.Scanner().ScanAsync(root, null, null, None);
        Assert.Equal((2L, 1L, 1L, 0L, 0L), (scan.Seen, scan.Added, scan.Copies, scan.Moved, scan.MarkedGone));

        var run = await lib.Library.RunAsync(scan.RunId, None);
        Assert.Equal((1L, 1L, 0L), (run!.Added, run.Copies, run.Moved));
        Assert.Single(await lib.Library.DuplicatesAsync(TempLibrary.Account, 10, None));
    }

    [Fact]
    public async Task The_reported_case_38_files_36_contents_is_36_added_2_copies_nothing_moved()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        for (var i = 0; i < 36; i++)
        {
            TempLibrary.WritePng(Path.Combine(photos, $"set{i % 3}", $"p{i:00}.png"), seed: i);
        }

        File.Copy(Path.Combine(photos, "set0", "p00.png"), Path.Combine(photos, "set1", "twin-a.png"));
        File.Copy(Path.Combine(photos, "set1", "p01.png"), Path.Combine(photos, "set2", "twin-b.png"));
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);

        // Eight threads, so the two halves of a pair really do race.
        var scan = await lib.Scanner(concurrency: 8).ScanAsync(root, null, null, None);
        Assert.Equal((38L, 36L, 2L, 0L, 0L), (scan.Seen, scan.Added, scan.Copies, scan.Moved, scan.Errors));
    }

    [Fact]
    public async Task A_rename_between_scans_is_one_move_and_nothing_gone()
    {
        using var lib = new TempLibrary();
        var (photos, root, scanner) = await TwoFilesAsync(lib);
        var id = await ContentIdOf(lib.Db, root, "a/1.png");

        File.Move(Path.Combine(photos, "a", "1.png"), Path.Combine(photos, "b", "renamed.png"));
        var scan = await scanner.ScanAsync(root, null, null, None);

        Assert.Equal((0L, 1L, 0L, 1L, 0L), (scan.Added, scan.Moved, scan.Copies, scan.Unchanged, scan.MarkedGone));
        Assert.Equal(id, await ContentIdOf(lib.Db, root, "b/renamed.png"));
        Assert.Equal(2, await lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task A_move_seen_by_a_scan_of_the_destination_folder_alone_is_still_a_move()
    {
        using var lib = new TempLibrary();
        var (photos, root, scanner) = await TwoFilesAsync(lib);

        // The watch scans the folder that changed; the old path is in a
        // folder this scan does not walk, so only the disk can say it went.
        File.Move(Path.Combine(photos, "a", "1.png"), Path.Combine(photos, "b", "from-a.png"));
        var scoped = await scanner.ScanAsync(root, "b", null, None);
        Assert.Equal((1L, 0L, 0L), (scoped.Moved, scoped.Copies, scoped.MarkedGone));

        var other = await scanner.ScanAsync(root, "a", null, None);
        Assert.Equal((0L, 0L), (other.Seen, other.MarkedGone));
    }

    [Fact]
    public async Task A_copy_to_a_second_path_between_scans_is_one_copy_and_no_move()
    {
        using var lib = new TempLibrary();
        var (photos, root, scanner) = await TwoFilesAsync(lib);

        File.Copy(Path.Combine(photos, "a", "1.png"), Path.Combine(photos, "b", "copy-of-1.png"));
        var scan = await scanner.ScanAsync(root, null, null, None);

        Assert.Equal((0L, 0L, 1L, 2L, 0L), (scan.Added, scan.Moved, scan.Copies, scan.Unchanged, scan.MarkedGone));
        var dup = Assert.Single(await lib.Library.DuplicatesAsync(TempLibrary.Account, 10, None));
        Assert.Equal(2, dup.Locations.Count);
    }

    [Fact]
    public async Task Deleting_one_of_two_copies_is_one_gone_and_the_picture_stays()
    {
        using var lib = new TempLibrary();
        var (photos, root, scanner) = await TwoFilesAsync(lib);
        File.Copy(Path.Combine(photos, "a", "1.png"), Path.Combine(photos, "b", "copy-of-1.png"));
        await scanner.ScanAsync(root, null, null, None);

        File.Delete(Path.Combine(photos, "a", "1.png"));
        var scan = await scanner.ScanAsync(root, null, null, None);

        Assert.Equal((0L, 0L, 0L, 2L, 1L), (scan.Added, scan.Moved, scan.Copies, scan.Unchanged, scan.MarkedGone));
        Assert.Equal(2, await lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.Empty(await lib.Library.DuplicatesAsync(TempLibrary.Account, 10, None));
    }

    [Fact]
    public async Task A_trashed_picture_that_is_renamed_stays_in_the_trash()
    {
        using var lib = new TempLibrary();
        var (photos, root, scanner) = await TwoFilesAsync(lib);
        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync("UPDATE location SET trashed_at = 1 WHERE rel_path = 'a/1.png'");
        }

        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        File.Move(Path.Combine(photos, "a", "1.png"), Path.Combine(photos, "a", "1-renamed.png"));
        var scan = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal(1L, scan.Moved);
        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    /// <summary>a/1.png and b/2.png, scanned once.</summary>
    private static async Task<(string Photos, long Root, Tessera.Api.Workers.Scanner Scanner)> TwoFilesAsync(TempLibrary lib)
    {
        var photos = Path.Combine(lib.Dir, "photos");
        TempLibrary.WritePng(Path.Combine(photos, "a", "1.png"), seed: 1);
        TempLibrary.WritePng(Path.Combine(photos, "b", "2.png"), seed: 2);
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        var scanner = lib.Scanner();
        var first = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal((2L, 0L, 0L), (first.Added, first.Copies, first.Moved));
        return (photos, root, scanner);
    }

    // ------------------------------------------------------------------ dates

    [Fact]
    public async Task New_content_is_dated_and_shaped_from_its_headers_at_first_sight()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        MediaFiles.Jpeg(Path.Combine(photos, "x", "camera.jpg"), new ExifFields(DateTimeOriginal: "2016:04:05 06:07:08", OffsetTimeOriginal: "+01:00", Orientation: 6), width: 96, height: 64);
        MediaFiles.Png(Path.Combine(photos, "x", "Screenshot_20190203-040506.png"), seed: 2);
        MediaFiles.Png(Path.Combine(photos, "x", "plain.png"), seed: 3);
        var mtime = new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(photos, "x", "plain.png"), mtime);
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);

        await lib.Scanner(zone: Zone).ScanAsync(root, null, null, None);

        var rows = (await Rows(lib.Db)).ToDictionary(r => r.RelPath);
        Assert.Equal(("exif", new DateTimeOffset(2016, 4, 5, 5, 7, 8, TimeSpan.Zero), 64L, 96L, 1L),
                     Shape(rows["x/camera.jpg"]));
        Assert.Equal(("filename", new DateTimeOffset(2019, 2, 2, 22, 35, 6, TimeSpan.Zero), 96L, 64L, 1L),
                     Shape(rows["x/Screenshot_20190203-040506.png"]));
        Assert.Equal(("mtime", new DateTimeOffset(mtime), 96L, 64L, 1L), Shape(rows["x/plain.png"]));

        // The timeline sorts by the capture date: 2019, 2016, 2011.
        var page = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, 10, None);
        Assert.Equal([2019, 2016, 2011], page.Items.Select(i => i.TakenAt.UtcDateTime.Year).ToArray());

        static (string?, DateTimeOffset, long?, long?, long) Shape(Row r) =>
            (r.TakenSrc, DateTimeOffset.FromUnixTimeMilliseconds(r.TakenAt), r.Width, r.Height, r.MetaVersion);
    }

    [Fact]
    public async Task The_backfill_retimes_content_indexed_before_dates_were_read_and_resumes()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        MediaFiles.Jpeg(Path.Combine(photos, "old", "camera.jpg"), new ExifFields(DateTimeOriginal: "2016:04:05 06:07:08", OffsetTimeOriginal: "+01:00"), seed: 1);
        MediaFiles.Png(Path.Combine(photos, "old", "IMG_20170809_101112.png"), seed: 2);
        MediaFiles.Png(Path.Combine(photos, "old", "plain.png"), seed: 3);
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        await lib.Scanner(zone: Zone).ScanAsync(root, null, null, None);

        // Make it a library from before this change: every content on its
        // file time, unread, no shape. What the core had on 2026-10-16.
        var fileTime = new DateTimeOffset(2025, 12, 25, 0, 0, 0, TimeSpan.Zero);
        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync(
                "UPDATE content SET taken_at = @T, taken_src = 'mtime', meta_version = 0, width = NULL, height = NULL",
                new { T = fileTime.ToUnixTimeMilliseconds() });
        }

        var before = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, 10, None);
        Assert.All(before.Items, i => Assert.Equal(fileTime, i.TakenAt));
        Assert.Equal(new(3, 0, 0, 0), await lib.Content.DateCountsAsync(None));

        var backfill = lib.Backfill(Zone);
        var pass = await backfill.PassAsync(None);
        Assert.Equal(new Tessera.Api.Workers.BackfillPass(Read: 3, Retimed: 2, Unreadable: 0), pass);

        // The trigger moved the timeline rows; the plain file keeps its time.
        var after = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, 10, None);
        Assert.Equal(
            [fileTime, new DateTimeOffset(2017, 8, 9, 4, 41, 12, TimeSpan.Zero), new DateTimeOffset(2016, 4, 5, 5, 7, 8, TimeSpan.Zero)],
            after.Items.Select(i => i.TakenAt).ToArray());
        Assert.All(after.Items, i => Assert.Equal((96, 64), (i.Width, i.Height)));
        Assert.Equal(new(0, 1, 1, 1), await lib.Content.DateCountsAsync(None));

        // Done means done: a second pass finds nothing to read.
        Assert.Equal(new Tessera.Api.Workers.BackfillPass(0, 0, 0), await backfill.PassAsync(None));
    }

    [Fact]
    public async Task The_backfill_leaves_what_it_cannot_read_pending_and_counts_it()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        MediaFiles.Jpeg(Path.Combine(photos, "a", "one.jpg"), new ExifFields(DateTimeOriginal: "2016:04:05 06:07:08"), seed: 1);
        MediaFiles.Jpeg(Path.Combine(photos, "a", "two.jpg"), new ExifFields(DateTimeOriginal: "2017:04:05 06:07:08"), seed: 2);
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        await lib.Scanner(zone: Zone).ScanAsync(root, null, null, None);
        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync("UPDATE content SET taken_src = 'mtime', meta_version = 0");
        }

        // One file changed under the scanner's feet: other bytes now.
        File.AppendAllText(Path.Combine(photos, "a", "two.jpg"), "edited");

        var backfill = lib.Backfill(Zone);
        Assert.Equal(new Tessera.Api.Workers.BackfillPass(1, 1, 1), await backfill.PassAsync(None));
        Assert.Equal(1, backfill.Unreadable);
        Assert.Equal(1, (await lib.Content.DateCountsAsync(None)).Pending);
    }

    private sealed record Row(string RelPath, string? TakenSrc, long TakenAt, long? Width, long? Height, long MetaVersion);

    private static async Task<IEnumerable<Row>> Rows(Db db)
    {
        await using var conn = await db.OpenAsync(None);
        return await conn.QueryAsync<Row>(
            """
            SELECT l.rel_path AS RelPath, c.taken_src AS TakenSrc, c.taken_at AS TakenAt,
                   c.width AS Width, c.height AS Height, c.meta_version AS MetaVersion
              FROM location l JOIN content c ON c.content_id = l.content_id
             WHERE l.gone_at IS NULL
            """);
    }

    private static async Task<long> ContentIdOf(Db db, long root, string rel)
    {
        await using var conn = await db.OpenAsync(None);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT content_id FROM location WHERE root_id = @Root AND rel_path = @Rel AND gone_at IS NULL",
            new { Root = root, Rel = rel });
    }
}
