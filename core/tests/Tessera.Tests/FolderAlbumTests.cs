using System.Reflection;
using System.Security.Cryptography;
using Dapper;
using Tessera.Data;
using Tessera.Data.Migrations;
using Tessera.Data.Repositories;

namespace Tessera.Tests;

/// <summary>
/// Which albums the folder rule made, and the one rule for deleting them:
/// none of the roots that made it is left and it has no live picture. A
/// person's album is never deleted, and neither is one with a live picture.
/// </summary>
public sealed class FolderAlbumTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Removing_a_root_deletes_only_the_folder_albums_it_leaves_with_nothing_live()
    {
        using var lib = new TempLibrary();
        var mine = await lib.Albums.EnsureAsync(TempLibrary.Account, "Mine", "", None);
        var trips = await lib.Albums.EnsureAsync(TempLibrary.Account, "Trips", "", None);

        var r1 = await lib.AddRootAsync("C:/one", "one");
        var screen = await lib.PlaceAsync(r1, "Screens A/1.png");
        await lib.PlaceAsync(r1, "Screens A/2.png");
        await lib.PlaceAsync(r1, "Shared/3.png");
        await lib.PlaceAsync(r1, "Trips/4.png");
        await lib.PlaceAsync(r1, "Mixed/5.png");

        var r2 = await lib.AddRootAsync("C:/two", "two");
        await lib.PlaceAsync(r2, "Shared/6.png");
        var other = await lib.PlaceAsync(r2, "Other/7.png");

        await lib.Folders.ReconcileAlbumsAsync(r1, null, None);
        await lib.Folders.ReconcileAlbumsAsync(r2, null, None);
        var byName = await ByNameAsync(lib);
        Assert.Equal(("folder", "folder", "manual", "manual"), (byName["Screens A"].Origin, byName["Shared"].Origin, byName["Trips"].Origin, byName["Mine"].Origin));

        // A person files a picture of root one into their album, and one of
        // root two into a folder album of root one.
        await lib.Albums.AddAsync(TempLibrary.Account, mine, [screen], None);
        await lib.Albums.AddAsync(TempLibrary.Account, byName["Mixed"].AlbumId, [other], None);

        Assert.Equal(1, await lib.Library.RemoveRootAsync(TempLibrary.Account, r1, None));   // Screens A, and nothing else

        byName = await ByNameAsync(lib);
        Assert.False(byName.ContainsKey("Screens A"));
        Assert.Equal(1, byName["Shared"].ItemCount);           // root two still holds a "Shared"
        Assert.Equal(1, byName["Mixed"].ItemCount);            // a live picture keeps it
        Assert.Equal(0, byName["Mine"].ItemCount);             // a person's album, kept, counted live
        Assert.Single(await lib.Albums.MemberHashesAsync(mine, None));
        Assert.Equal(trips, byName["Trips"].AlbumId);          // a person's album the rule had filled

        Assert.Equal(3, await lib.Library.RemoveRootAsync(TempLibrary.Account, r2, None));   // Shared, Other, Mixed
        Assert.Equal(["Mine", "Trips"], (await lib.Albums.ListAsync(TempLibrary.Account, None)).Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task Naming_or_editing_a_folder_album_makes_it_a_persons()
    {
        using var lib = new TempLibrary();
        var root = await lib.AddRootAsync();
        await lib.PlaceAsync(root, "Named/1.png");
        await lib.PlaceAsync(root, "Edited/2.png");
        await lib.PlaceAsync(root, "Left/3.png");
        await lib.Folders.ReconcileAlbumsAsync(root, null, None);
        var byName = await ByNameAsync(lib);

        Assert.Equal(byName["Named"].AlbumId, await lib.Albums.EnsureAsync(TempLibrary.Account, "named", "", None));
        Assert.True(await lib.Albums.RenameAsync(TempLibrary.Account, byName["Edited"].AlbumId, "Edited by me", "", None));

        Assert.Equal(1, await lib.Library.RemoveRootAsync(TempLibrary.Account, root, None));
        Assert.Equal(["Edited by me", "Named"], (await lib.Albums.ListAsync(TempLibrary.Account, None)).Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task The_upgrade_tells_folder_albums_from_a_persons_and_removes_the_leftovers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var db = new Db(Path.Combine(dir, "library.db"));
        try
        {
            await ApplyUpToAsync(db, "0002_capture_dates_and_copies");

            const long t = 1_792_133_640_361;   // a scan's end, Unix ms
            await using (var conn = await db.OpenWriteAsync(None))
            {
                await conn.ExecuteAsync("INSERT INTO scan_root (root_id, account_id, label, abs_path) VALUES (9, 1, 'kept', 'C:/kept')");

                // 1, 2: two folders of a root since removed: made in one statement,
                // filled in the next, moments after their pictures were seen.
                await Album(conn, 1, "Screens A", created: t, updated: t);
                await Album(conn, 2, "Screens B", created: t, updated: t);
                await Member(conn, 1, firstSeen: t - 152, added: t);
                await Member(conn, 1, firstSeen: t - 65, added: t);
                await Member(conn, 2, firstSeen: t - 7, added: t);

                // 3: the only folder of its scan; told by how soon it followed its pictures.
                await Album(conn, 3, "Swatches", created: t + 72_458, updated: t + 72_458);
                await Member(conn, 3, firstSeen: t + 72_451, added: t + 72_458);

                // 5: a person's, made first and filled minutes later, its pictures live.
                await Album(conn, 5, "A", created: t + 183_707_607, updated: t + 183_840_524);
                await Member(conn, 5, firstSeen: t + 183_683_917, added: t + 183_840_524, liveIn: (9, "x/a.png"));

                // 6: a person's, empty.
                await Album(conn, 6, "Someday", created: t + 200_000, updated: t + 200_000);

                // 7: a person's, made with pictures in one request, half a minute
                // after the pictures were seen; their folder since removed.
                await Album(conn, 7, "Picked", created: t + 300_000, updated: t + 300_004);
                await Member(conn, 7, firstSeen: t + 270_000, added: t + 300_004);

                // 8: born like a folder album, but someone typed a description.
                await Album(conn, 8, "Described", created: t, updated: t, description: "ours");
                await Member(conn, 8, firstSeen: t - 10, added: t);

                // 9: a folder album whose root and folder are still here.
                await Album(conn, 9, "x", created: t + 400_000, updated: t + 400_000);
                await Member(conn, 9, firstSeen: t + 399_990, added: t + 400_000, liveIn: (9, "x/b.png"));
                await Member(conn, 5, firstSeen: t + 1, added: t + 183_840_524);
            }

            var applied = await new SchemaMigrator(db).ApplyAsync(None);
            Assert.Equal(["0003_folder_albums_and_imports"], applied);

            await using var read = await db.OpenAsync(None);
            var albums = (await read.QueryAsync<(long Id, string Origin)>("SELECT album_id, origin FROM album ORDER BY album_id")).ToList();
            Assert.Equal([(5L, "manual"), (6L, "manual"), (7L, "manual"), (8L, "manual"), (9L, "folder")], albums);
            Assert.Equal([(9L, 9L)], (await read.QueryAsync<(long, long)>("SELECT album_id, root_id FROM album_root")).ToList());
            Assert.Equal(2, await read.ExecuteScalarAsync<long>("SELECT count(*) FROM album_item WHERE album_id = 5"));
        }
        finally
        {
            db.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task The_app_folder_may_nest_in_a_persons_root_and_is_scanned_by_its_own_root_only()
    {
        using var lib = new TempLibrary();
        var pictures = Path.Combine(lib.Dir, "Pictures");
        var app = Path.Combine(pictures, "Tessera");
        TempLibrary.WritePng(Path.Combine(pictures, "Holiday", "a.png"), seed: 1);
        TempLibrary.WritePng(Path.Combine(app, "Unsorted", "b.png"), seed: 2);

        // The app's folder first, then a person adds the folder that holds it: allowed.
        var own = await lib.Library.AddRootAsync(TempLibrary.Account, Core.PathRules.Canonical(app), "Tessera", appOwned: true, None);
        Assert.Equal(RootConflict.None, (await lib.Library.ConflictAsync(Core.PathRules.Canonical(pictures), None)).Conflict);
        var person = await lib.Library.AddRootAsync(TempLibrary.Account, Core.PathRules.Canonical(pictures), "Pictures", None);

        // Never the reverse: nothing inside the app's folder becomes a root, and the
        // app's folder may not hold a person's root.
        Assert.Equal(RootConflict.InsideARoot, (await lib.Library.ConflictAsync(Core.PathRules.Canonical(Path.Combine(app, "Unsorted")), None)).Conflict);
        Assert.Equal(RootConflict.ContainsARoot, (await lib.Library.ConflictAsync(Core.PathRules.Canonical(lib.Dir), None, appFolder: true)).Conflict);
        Assert.Equal(RootConflict.None, (await lib.Library.ConflictAsync(Core.PathRules.Canonical(Path.Combine(pictures, "Elsewhere")), None, appFolder: true)).Conflict);

        var scanner = lib.Scanner();
        var outer = await scanner.ScanAsync(person, null, null, None);
        var inner = await scanner.ScanAsync(own, null, null, None);
        Assert.Equal((1L, 1L), (outer.Seen, inner.Seen));
        Assert.Equal(2, await lib.Timeline.CountAsync(TempLibrary.Account, None));

        // No album named after the app's folder, and none for its "Unsorted".
        Assert.Equal(["Holiday"], (await lib.Albums.ListAsync(TempLibrary.Account, None)).Select(a => a.Name).ToArray());
    }

    // ------------------------------------------------------------ helpers

    private static async Task<Dictionary<string, Core.Domain.Album>> ByNameAsync(TempLibrary lib) =>
        (await lib.Albums.ListAsync(TempLibrary.Account, None)).ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A database as an older build left it: the embedded migrations up to and including <paramref name="last"/>.</summary>
    private static async Task ApplyUpToAsync(Db db, string last)
    {
        var asm = typeof(SchemaMigrator).Assembly;
        const string prefix = "Tessera.Data.Migrations.";
        await using var conn = await db.OpenWriteAsync(None);
        await conn.ExecuteAsync("CREATE TABLE schema_migration (version TEXT PRIMARY KEY, applied_at INTEGER NOT NULL DEFAULT 0)");
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = name[prefix.Length..^4];
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await conn.ExecuteAsync(await reader.ReadToEndAsync());
            await conn.ExecuteAsync("INSERT INTO schema_migration (version) VALUES (@Version)", new { Version = version });
            if (version == last)
            {
                return;
            }
        }
    }

    private static Task Album(System.Data.IDbConnection conn, long id, string name, long created, long updated, string description = "") =>
        conn.ExecuteAsync(
            "INSERT INTO album (album_id, account_id, name, description, created_at, updated_at) VALUES (@Id, 1, @Name, @Description, @Created, @Updated)",
            new { Id = id, Name = name, Description = description, Created = created, Updated = updated });

    /// <summary>A content first seen at a time, put in an album at a time; with a live location when given.</summary>
    private static async Task Member(System.Data.IDbConnection conn, long albumId, long firstSeen, long added, (long Root, string Rel)? liveIn = null)
    {
        var contentId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO content (sha256, byte_size, mime, taken_at, first_seen) VALUES (@Sha, 10, 'image/png', @Seen, @Seen) RETURNING content_id",
            new { Sha = RandomNumberGenerator.GetBytes(32), Seen = firstSeen });
        if (liveIn is { } at)
        {
            await conn.ExecuteAsync(
                "INSERT INTO location (content_id, account_id, root_id, rel_path) VALUES (@ContentId, 1, @Root, @Rel)",
                new { ContentId = contentId, at.Root, at.Rel });
        }

        // The touch trigger moves updated_at; put it back to what the case says.
        var updated = await conn.ExecuteScalarAsync<long>("SELECT updated_at FROM album WHERE album_id = @Id", new { Id = albumId });
        await conn.ExecuteAsync("INSERT INTO album_item (album_id, content_id, added_at) VALUES (@AlbumId, @ContentId, @Added)",
            new { AlbumId = albumId, ContentId = contentId, Added = added });
        await conn.ExecuteAsync("UPDATE album SET updated_at = max(@Updated, @Added) WHERE album_id = @AlbumId",
            new { AlbumId = albumId, Updated = updated, Added = added });
    }
}
