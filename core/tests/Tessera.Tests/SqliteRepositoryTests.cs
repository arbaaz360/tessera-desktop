using Dapper;
using Tessera.Api.Licence;
using Tessera.Data;
using Tessera.Data.Migrations;
using Tessera.Data.Repositories;

namespace Tessera.Tests;

/// <summary>
/// The repositories against a real, temporary SQLite file: the schema's
/// triggers, the ported SQL, and Dapper's timestamp handling, which are the
/// three places a Postgres habit would quietly break.
/// </summary>
public sealed class SqliteRepositoryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_schema_applies_once_and_seeds_account_1()
    {
        using var lib = new TempLibrary();
        Assert.Empty(await new SchemaMigrator(lib.Db).ApplyAsync(None));

        var account = await new AccountRepository(lib.Db).GetAsync(TempLibrary.Account, None);
        Assert.NotNull(account);
        Assert.Equal("Library", account.DisplayName);
        Assert.True(account.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_new_location_puts_the_picture_on_the_timeline_and_a_move_keeps_its_id()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();

        var file = TempLibrary.File("a/one.png", takenAt: new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var id = await lib.Content.UpsertAsync(file, None);
        Assert.Equal(Reconciled.Added, await lib.Content.RecordLocationAsync(id, TempLibrary.Account, root, file, None));
        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        // Same path, same bytes: a heartbeat.
        Assert.Equal(Reconciled.Unchanged, await lib.Content.RecordLocationAsync(id, TempLibrary.Account, root, file, None));

        // Same bytes at a new path: a move, the same content id, still one timeline row.
        await Task.Delay(5);
        var scanStart = DateTimeOffset.UtcNow;
        await Task.Delay(5);
        var moved = file with { RelPath = "b/renamed.png" };
        Assert.Equal(id, await lib.Content.UpsertAsync(moved, None));
        Assert.Equal(Reconciled.Moved, await lib.Content.RecordLocationAsync(id, TempLibrary.Account, root, moved, None));
        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        // The old path was not seen by this scan: gone. The picture stays.
        Assert.Equal(1, await lib.Content.MarkGoneAsync(root, scanStart, null, [".png"], None));
        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        var page = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, 10, None);
        var item = Assert.Single(page.Items);
        Assert.Equal(id, item.ContentId);
        Assert.Equal(new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero), item.TakenAt);

        var view = await lib.Viewable.GetAsync(TempLibrary.Account, id, None);
        Assert.Equal("C:/photos/b/renamed.png", view!.Value.AbsolutePath);
    }

    [Fact]
    public async Task A_scan_only_marks_gone_what_it_looked_for()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        await lib.PlaceAsync(root, "x/keep.dng");
        await lib.PlaceAsync(root, "x/judge.png");
        await lib.PlaceAsync(root, "y/outside.png");
        await Task.Delay(5);

        // Scoped to x, enumerating png only: the dng was never looked at, y was never walked.
        Assert.Equal(1, await lib.Content.MarkGoneAsync(root, DateTimeOffset.UtcNow, "x", [".png"], None));
        Assert.Equal(2, await lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task Trash_hides_and_restore_shows()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var id = await lib.PlaceAsync(root, "p.png");

        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync($"UPDATE location SET trashed_at = {Db.NowMs} WHERE content_id = @Id", new { Id = id });
        }

        Assert.Equal(0, await lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.Null(await lib.Viewable.GetAsync(TempLibrary.Account, id, None));

        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync("UPDATE location SET trashed_at = NULL WHERE content_id = @Id", new { Id = id });
        }

        Assert.Equal(1, await lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task Retiming_content_moves_its_timeline_row()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var id = await lib.PlaceAsync(root, "p.png", takenAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using (var conn = await lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync("UPDATE content SET taken_at = @T WHERE content_id = @Id",
                new { Id = id, T = Db.ToMs(new DateTimeOffset(1999, 12, 31, 0, 0, 0, TimeSpan.Zero)) });
        }

        var page = await lib.Timeline.PageAsync(TempLibrary.Account, null, null, 10, None);
        Assert.Equal(1999, page.Items.Single().TakenAt.Year);
    }

    [Fact]
    public async Task Duplicates_are_contents_with_two_live_locations()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var file = TempLibrary.File("a/same.png");
        var id = await lib.Content.UpsertAsync(file, None);
        await lib.Content.RecordLocationAsync(id, TempLibrary.Account, root, file, None);
        await lib.Content.RecordLocationAsync(id, TempLibrary.Account, root, file with { RelPath = "b/same-copy.png" }, None);
        await lib.PlaceAsync(root, "c/other.png");

        var sets = await lib.Library.DuplicatesAsync(TempLibrary.Account, 100, None);
        var set = Assert.Single(sets);
        Assert.Equal(id, set.ContentId);
        Assert.Equal(["a/same.png", "b/same-copy.png"], set.Locations.Select(l => l.RelPath).ToArray());

        var roots = await lib.Library.RootsAsync(TempLibrary.Account, None);
        Assert.Equal(3, roots.Single().Files);
        Assert.Equal(2, roots.Single().Contents);
    }

    [Fact]
    public async Task Albums_follow_folders_and_names_are_case_insensitive()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var a1 = await lib.PlaceAsync(root, "Trips/1.png");
        await lib.PlaceAsync(root, "Trips/2.png");
        var b3 = await lib.PlaceAsync(root, "Work/3.png");
        await lib.PlaceAsync(root, "loose.png");

        Assert.Equal((2, 3), await lib.Folders.ReconcileAlbumsAsync(root, null, None));
        Assert.Equal((0, 0), await lib.Folders.ReconcileAlbumsAsync(root, null, None));

        var albums = await lib.Albums.ListAsync(TempLibrary.Account, None);
        Assert.Equal(["Trips", "Work"], albums.Select(a => a.Name).ToArray());
        Assert.Equal(2, albums[0].ItemCount);
        Assert.Equal(a1, (await lib.Albums.ListAsync(TempLibrary.Account, None, containing: a1)).Single().ShownCoverContentId is { } ? a1 : -1);

        var trips = albums[0].AlbumId;
        Assert.Equal(trips, await lib.Albums.EnsureAsync(TempLibrary.Account, "trips", "", None));

        Assert.Equal(1, await lib.Albums.AddAsync(TempLibrary.Account, trips, [b3, a1], None));
        Assert.Equal(3, (await lib.Albums.GetAsync(TempLibrary.Account, trips, None))!.ItemCount);
        Assert.Equal(1, await lib.Albums.RemoveAsync(TempLibrary.Account, trips, [b3], None));

        var stats = await lib.Albums.StatsAsync(TempLibrary.Account, trips, None);
        Assert.Equal(2, stats!.Total);
        Assert.Equal(2, stats.Images);
        Assert.Equal(2468, stats.Bytes);

        Assert.True(await lib.Albums.SetCoverAsync(TempLibrary.Account, trips, a1, None));
        Assert.False(await lib.Albums.SetCoverAsync(TempLibrary.Account, trips, b3, None));
        Assert.Equal(a1, (await lib.Albums.GetAsync(TempLibrary.Account, trips, None))!.ShownCoverContentId);

        var items = await lib.Albums.ItemsAsync(TempLibrary.Account, trips, null, null, 1, None);
        Assert.Single(items.Items);
        Assert.NotNull(items.NextBefore);
        var rest = await lib.Albums.ItemsAsync(TempLibrary.Account, trips, items.NextBefore, items.NextContentId, 1, None);
        Assert.Single(rest.Items);
        Assert.NotEqual(items.Items[0].ContentId, rest.Items[0].ContentId);
    }

    [Fact]
    public async Task Work_items_are_leased_retried_and_finally_failed()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var id = await lib.PlaceAsync(root, "a/p.png");

        Assert.True(await lib.WorkItems.EnqueueAsync(WorkItems.ThumbnailKind, id, None));
        Assert.False(await lib.WorkItems.EnqueueAsync(WorkItems.ThumbnailKind, id, None));

        var leased = Assert.Single(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMinutes(5), None));
        Assert.Equal(id, leased.ContentId);
        Assert.Equal("C:/photos", leased.Root);
        Assert.Equal("a/p.png", leased.Rel);
        Assert.Equal(1, leased.Attempts);
        Assert.Empty(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMinutes(5), None));

        await lib.WorkItems.CompleteAsync(leased.WorkId, "boom", None);
        for (var attempt = 2; attempt <= WorkItems.MaxAttempts; attempt++)
        {
            var again = Assert.Single(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMinutes(5), None));
            Assert.Equal(attempt, again.Attempts);
            await lib.WorkItems.CompleteAsync(again.WorkId, "boom", None);
        }

        Assert.Empty(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMinutes(5), None));
        var stats = (await lib.Jobs.ListAsync(None)).Single(j => j.Name == JobNames.Thumbnails).Statistics;
        Assert.Equal(1, stats.Failed);

        // Failed for good blocks a re-queue, and shows in the counts.
        Assert.False(await lib.WorkItems.EnqueueAsync(WorkItems.ThumbnailKind, id, None));
        var counts = await lib.Jobs.ThumbnailCountsAsync(None);
        Assert.Equal(new DerivedCounts(0, 0, 1), counts);
    }

    [Fact]
    public async Task An_expired_lease_is_handed_out_again()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var id = await lib.PlaceAsync(root, "a/p.png");
        await lib.WorkItems.EnqueueAsync(WorkItems.ThumbnailKind, id, None);

        Assert.Single(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMilliseconds(-1), None));
        Assert.Single(await lib.WorkItems.LeaseAsync(WorkItems.ThumbnailKind, 10, TimeSpan.FromMinutes(5), None));
    }

    [Fact]
    public async Task Settings_and_licence_persist()
    {
        using var lib = new TempLibrary();
        var s = await lib.Settings.ReadAsync(None);
        Assert.Equal(new AppSettings(4, false, true, LicenceStates.Free), s);

        await lib.Settings.SetThumbnailConcurrencyAsync(8, None);
        await lib.Settings.SetEmbedOnBatteryAsync(true, None);
        await lib.Settings.SetSearchEnabledAsync(false, None);
        await lib.Settings.SetLicenceStateAsync(LicenceStates.Licensed, None);
        Assert.Equal(new AppSettings(8, true, false, LicenceStates.Licensed), await lib.Settings.ReadAsync(None));
        Assert.Null(await lib.Tier.MaxContentIdAsync(None));
    }

    [Fact]
    public async Task The_free_tier_holds_back_content_beyond_the_cap()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await lib.PlaceAsync(root, $"a/{i}.png"));
        }

        Assert.Null(await lib.Library.CapBoundaryAsync(5, None));
        Assert.Equal(ids[2], await lib.Library.CapBoundaryAsync(3, None));

        // With a cap of 3, three thumbnails are queued and two wait for the licence.
        Assert.Equal(3, await lib.Folders.QueueThumbnailsUnderAsync(root, null, ids[2], None));
        var counts = await lib.Jobs.ThumbnailCountsAsync(None);
        Assert.Equal(5, counts.Pending);

        // Licensed: the rest follow.
        Assert.Equal(2, await lib.Jobs.QueueThumbnailsAsync(null, None));

        var status = await lib.Tier.StatusAsync(None);
        Assert.Equal(5, status.Indexed);
        Assert.False(status.CapReached);
        Assert.Equal(FreeTier.Cap, status.Cap);
    }

    [Fact]
    public async Task Removing_a_root_removes_its_locations_and_timeline_rows_but_not_content()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var id = await lib.PlaceAsync(root, "a/p.png");
        var album = await lib.Albums.EnsureAsync(TempLibrary.Account, "keep", "", None);
        await lib.Albums.AddAsync(TempLibrary.Account, album, [id], None);

        Assert.True(await lib.Library.RemoveRootAsync(TempLibrary.Account, root, None));
        Assert.Equal(0, await lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.Empty(await lib.Library.RootsAsync(TempLibrary.Account, None));
        Assert.Equal(1, (await lib.Albums.GetAsync(TempLibrary.Account, album, None))!.ItemCount);
        // Still viewable through the album, with no path to a file.
        var view = await lib.Viewable.GetAsync(TempLibrary.Account, id, None);
        Assert.NotNull(view);
        Assert.Null(view.Value.AbsolutePath);
    }

    [Fact]
    public async Task A_root_cannot_be_added_twice_or_nested()
    {
        using var lib = new TempLibrary();
        await lib.AddRootAsync("C:/photos");
        Assert.Equal(RootConflict.AlreadyARoot, (await lib.Library.ConflictAsync("C:/photos", None)).Conflict);
        Assert.Equal(RootConflict.AlreadyARoot, (await lib.Library.ConflictAsync("c:/Photos", None)).Conflict);
        Assert.Equal(RootConflict.InsideARoot, (await lib.Library.ConflictAsync("C:/photos/2024", None)).Conflict);
        Assert.Equal(RootConflict.ContainsARoot, (await lib.Library.ConflictAsync("C:/", None)).Conflict);
        Assert.Equal(RootConflict.None, (await lib.Library.ConflictAsync("C:/photos-raw", None)).Conflict);
    }

    [Fact]
    public async Task Folder_ledger_round_trips_timestamps_at_millisecond_precision()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var mtime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);
        await lib.Folders.ObservedAsync(root, "sub", mtime, 12, changed: true, None);

        var row = await lib.Folders.GetAsync(root, "sub", None);
        Assert.NotNull(row);
        Assert.Equal(mtime, row.Mtime);
        Assert.Equal(12, row.Entries);
        Assert.NotNull(row.ChangedAt);
        Assert.Single(await lib.Folders.ChangedAsync(root, None));

        await lib.Folders.ScannedAsync(root, "sub", None);
        Assert.Empty(await lib.Folders.ChangedAsync(root, None));
    }

    [Fact]
    public async Task Interrupted_runs_are_marked_at_start()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        var run = await lib.Library.CreateRunAsync(root, null, None);
        Assert.Contains(root, await lib.Library.ScanningRootsAsync(None));

        Assert.Equal(1, await lib.Library.MarkInterruptedRunsAsync(None));
        var row = await lib.Library.RunAsync(run, None);
        Assert.True(row!.Cancelled);
        Assert.NotNull(row.FinishedAt);
        Assert.Empty(await lib.Library.ScanningRootsAsync(None));
    }
}
