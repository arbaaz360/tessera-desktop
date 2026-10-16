using Dapper;
using Tessera.Core;
using Tessera.Data;

namespace Tessera.Tests;

/// <summary>
/// The scanner over a real folder of real PNGs: counts that add up, a move
/// that keeps its content id, a removal that is marked and never deleted.
/// </summary>
public sealed class ScanTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Added_moved_and_gone_add_up_and_a_moved_file_keeps_its_content()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        for (var i = 0; i < 12; i++)
        {
            TempLibrary.WritePng(Path.Combine(photos, i < 6 ? "alpha" : "beta", $"img{i:00}.png"), seed: i);
        }

        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        var scanner = lib.Scanner();

        var first = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal((12L, 12L, 0L, 0L, 0L, 0L), (first.Seen, first.Added, first.Moved, first.Unchanged, first.MarkedGone, first.Errors));
        Assert.Equal(12, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        var beforeMove = await ContentIdOf(lib.Db, root, "alpha/img00.png");

        // Nothing changed: every file is a heartbeat, nothing is re-hashed.
        var again = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal((12L, 0L, 0L, 12L, 0L), (again.Seen, again.Added, again.Moved, again.Unchanged, again.MarkedGone));

        // A move: the same bytes at a new path, the old path gone, the id kept.
        // Nothing is gone: the old row was retired by the move, not missed.
        File.Move(Path.Combine(photos, "alpha", "img00.png"), Path.Combine(photos, "beta", "moved.png"));
        var moved = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal((12L, 0L, 1L, 0L, 11L, 0L), (moved.Seen, moved.Added, moved.Moved, moved.Copies, moved.Unchanged, moved.MarkedGone));
        Assert.Equal(beforeMove, await ContentIdOf(lib.Db, root, "beta/moved.png"));
        Assert.Equal(12, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        // A removal: marked gone, the row kept, nothing on disk touched by us.
        File.Delete(Path.Combine(photos, "beta", "img07.png"));
        var gone = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal((11L, 0L, 0L, 11L, 1L), (gone.Seen, gone.Added, gone.Moved, gone.Unchanged, gone.MarkedGone));
        Assert.Equal(11, await lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.Equal(13, await Count(lib.Db, "SELECT count(*) FROM location"));
        Assert.Equal(2, await Count(lib.Db, "SELECT count(*) FROM location WHERE gone_at IS NOT NULL"));

        // Folders became albums; the moved picture now sits in beta.
        var albums = await lib.Albums.ListAsync(TempLibrary.Account, None);
        Assert.Equal(["alpha", "beta"], albums.Select(a => a.Name).ToArray());
        Assert.Contains(beforeMove, (await lib.Albums.MemberHashesAsync(albums[1].AlbumId, None)).Select(m => m.ContentId));

        // Every live image has a thumbnail queued, once.
        Assert.Equal(12, await Count(lib.Db, "SELECT count(*) FROM work_item WHERE kind = 'thumbnail'"));

        var run = await lib.Library.RunAsync(gone.RunId, None);
        Assert.NotNull(run!.FinishedAt);
        Assert.Equal(11, run.FilesSeen);
        Assert.Equal(1, run.MarkedGone);
    }

    [Fact]
    public async Task A_scoped_scan_judges_only_its_folder()
    {
        using var lib = new TempLibrary();
        var photos = Path.Combine(lib.Dir, "photos");
        TempLibrary.WritePng(Path.Combine(photos, "a", "1.png"), 1);
        TempLibrary.WritePng(Path.Combine(photos, "b", "2.png"), 2);
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        var scanner = lib.Scanner();
        await scanner.ScanAsync(root, null, null, None);

        File.Delete(Path.Combine(photos, "b", "2.png"));
        var scoped = await scanner.ScanAsync(root, "a", null, None);
        Assert.Equal((1L, 1L, 0L), (scoped.Seen, scoped.Unchanged, scoped.MarkedGone));
        Assert.Equal(2, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        var whole = await scanner.ScanAsync(root, null, null, None);
        Assert.Equal(1, whole.MarkedGone);
    }

    [Fact]
    public async Task An_absent_root_is_refused_not_emptied()
    {
        using var lib = new TempLibrary();
        var root = await lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(Path.Combine(lib.Dir, "nowhere")), "nowhere", None);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => lib.Scanner().ScanAsync(root, null, null, None));
    }

    private static async Task<long> ContentIdOf(Db db, long root, string rel)
    {
        await using var conn = await db.OpenAsync(None);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT content_id FROM location WHERE root_id = @Root AND rel_path = @Rel AND gone_at IS NULL",
            new { Root = root, Rel = rel });
    }

    private static async Task<long> Count(Db db, string sql)
    {
        await using var conn = await db.OpenAsync(None);
        return await conn.ExecuteScalarAsync<long>(sql);
    }
}
