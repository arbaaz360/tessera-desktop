using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tessera.Api.Import;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Data;

namespace Tessera.Tests;

/// <summary>
/// Adding pictures, rule by rule: inside a root or outside, known bytes, a
/// name clash, a folder, a video, an album, a link, bytes from the webview,
/// a copy that fails half way, a trashed picture added again.
/// </summary>
public sealed class ImportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_file_inside_a_root_is_indexed_where_it_is_and_nothing_is_copied()
    {
        await using var rig = await ImportRig.StartAsync();
        var photos = Path.Combine(rig.Lib.Dir, "photos");
        var file = Path.Combine(photos, "trip", "a.png");
        TempLibrary.WritePng(file, seed: 1);
        await rig.Lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        var before = Snapshot(file);

        var result = await rig.Importer.PathAsync(file, rig.Target(), None);

        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.NotNull(result.ContentId);
        Assert.Equal(PathRules.Canonical(file), result.Path);
        Assert.Equal(1, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.False(Directory.Exists(rig.TesseraDir), "nothing copied, so the Tessera folder is not made");
        Assert.Equal(before, Snapshot(file));

        // Dropped again: already there, still nothing copied.
        var again = await rig.Importer.PathAsync(file, rig.Target(), None);
        Assert.Equal(ImportOutcome.Already, again.Outcome);
        Assert.Equal(result.ContentId, again.ContentId);
        Assert.Equal(1, await Count(rig.Lib.Db, "SELECT count(*) FROM work_item WHERE kind = 'thumbnail'"));
    }

    [Fact]
    public async Task A_file_from_outside_is_copied_into_the_tessera_folder_and_the_original_is_untouched()
    {
        await using var rig = await ImportRig.StartAsync();
        var outside = Path.Combine(rig.Lib.Dir, "Downloads", "beach.png");
        TempLibrary.WritePng(outside, seed: 2);
        var before = Snapshot(outside);

        var result = await rig.Importer.PathAsync(outside, rig.Target(), None);

        Assert.Equal(ImportOutcome.Copied, result.Outcome);
        var copy = Path.Combine(rig.TesseraDir, "Unsorted", "beach.png");
        Assert.Equal(PathRules.Canonical(copy), result.Path);
        Assert.Equal(File.ReadAllBytes(outside), File.ReadAllBytes(copy));
        Assert.Equal(before, Snapshot(outside));
        Assert.Empty(Directory.GetFiles(rig.TesseraDir, "*" + SafeCopy.PartialSuffix, SearchOption.AllDirectories));

        // The Tessera folder is a root, marked as the app's, and the copy is in the timeline.
        var root = Assert.Single(await rig.Lib.Library.RootsAsync(TempLibrary.Account, None));
        Assert.True(root.AppOwned);
        Assert.Equal(PathRules.Canonical(rig.TesseraDir), root.AbsPath);
        Assert.Equal(1, root.Files);
        Assert.Equal(1, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.Equal(PathRules.Canonical(rig.TesseraDir), await rig.Lib.Settings.ImportFolderAsync(None));

        // Its folders are not albums: "Unsorted" is not one.
        Assert.Empty(await rig.Lib.Albums.ListAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task Bytes_the_library_already_holds_are_not_copied_again()
    {
        await using var rig = await ImportRig.StartAsync();
        var photos = Path.Combine(rig.Lib.Dir, "photos");
        TempLibrary.WritePng(Path.Combine(photos, "x", "original.png"), seed: 3);
        var root = await rig.Lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        await rig.Lib.Scanner().ScanAsync(root, null, null, None);

        // The same bytes, elsewhere, under another name.
        var twin = Path.Combine(rig.Lib.Dir, "Desktop", "copy of original.png");
        Directory.CreateDirectory(Path.GetDirectoryName(twin)!);
        File.Copy(Path.Combine(photos, "x", "original.png"), twin);

        var result = await rig.Importer.PathAsync(twin, rig.Target(), None);

        Assert.Equal(ImportOutcome.Already, result.Outcome);
        Assert.False(Directory.Exists(rig.TesseraDir));
        Assert.Equal(1, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
        Assert.True(File.Exists(twin));

        // Dropped twice from outside: the second time it is the first copy.
        var other = Path.Combine(rig.Lib.Dir, "Desktop", "new.png");
        TempLibrary.WritePng(other, seed: 4);
        Assert.Equal(ImportOutcome.Copied, (await rig.Importer.PathAsync(other, rig.Target(), None)).Outcome);
        Assert.Equal(ImportOutcome.Already, (await rig.Importer.PathAsync(other, rig.Target(), None)).Outcome);
        Assert.Single(Directory.GetFiles(Path.Combine(rig.TesseraDir, "Unsorted")));
    }

    [Fact]
    public async Task A_name_clash_with_different_bytes_gets_a_number()
    {
        await using var rig = await ImportRig.StartAsync();
        var first = Path.Combine(rig.Lib.Dir, "camera", "IMG_0001.png");
        var second = Path.Combine(rig.Lib.Dir, "phone", "IMG_0001.png");
        TempLibrary.WritePng(first, seed: 5);
        TempLibrary.WritePng(second, seed: 6);

        var a = await rig.Importer.PathAsync(first, rig.Target(), None);
        var b = await rig.Importer.PathAsync(second, rig.Target(), None);

        Assert.Equal((ImportOutcome.Copied, ImportOutcome.Copied), (a.Outcome, b.Outcome));
        Assert.EndsWith("/Unsorted/IMG_0001.png", a.Path, StringComparison.Ordinal);
        Assert.EndsWith("/Unsorted/IMG_0001 (2).png", b.Path, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(second), File.ReadAllBytes(b.Path!));
        Assert.Equal(2, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task A_dropped_folder_is_indexed_in_place_never_copied_and_fills_the_album()
    {
        await using var rig = await ImportRig.StartAsync();
        var folder = Path.Combine(rig.Lib.Dir, "Holiday 2025");
        for (var i = 0; i < 4; i++)
        {
            TempLibrary.WritePng(Path.Combine(folder, i < 2 ? "" : "day2", $"p{i}.png"), seed: 10 + i);
        }

        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a picture");
        var album = await rig.Lib.Albums.EnsureAsync(TempLibrary.Account, "Trips", "", None);

        var result = await rig.Importer.PathAsync(folder, rig.Target(album, "Trips"), None);

        Assert.True(result.IsFolder);
        Assert.Equal(ImportOutcome.Added, result.Outcome);
        Assert.Equal(4, result.Pictures);
        Assert.False(Directory.Exists(rig.TesseraDir), "a folder is never copied");
        var root = Assert.Single(await rig.Lib.Library.RootsAsync(TempLibrary.Account, None));
        Assert.Equal(PathRules.Canonical(folder), root.AbsPath);
        Assert.False(root.AppOwned);
        Assert.Equal(4, (await rig.Lib.Albums.GetAsync(TempLibrary.Account, album, None))!.ItemCount);

        // A folder inside a library folder: rescanned there, already in the library.
        TempLibrary.WritePng(Path.Combine(folder, "day2", "late.png"), seed: 20);
        var inner = await rig.Importer.PathAsync(Path.Combine(folder, "day2"), rig.Target(album, "Trips"), None);
        Assert.Equal((ImportOutcome.Already, 3L, root.RootId), (inner.Outcome, inner.Pictures, inner.RootId));
        Assert.Equal(5, (await rig.Lib.Albums.GetAsync(TempLibrary.Account, album, None))!.ItemCount);
    }

    [Fact]
    public async Task A_folder_holding_a_library_folder_is_skipped_with_the_reason()
    {
        await using var rig = await ImportRig.StartAsync();
        var parent = Path.Combine(rig.Lib.Dir, "all");
        var child = Path.Combine(parent, "kept");
        TempLibrary.WritePng(Path.Combine(child, "a.png"), seed: 1);
        await rig.Lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(child), "kept", None);

        var result = await rig.Importer.PathAsync(parent, rig.Target(), None);

        Assert.Equal(ImportOutcome.Skipped, result.Outcome);
        Assert.Contains("holds the library folder", result.Reason, StringComparison.Ordinal);
        Assert.Single(await rig.Lib.Library.RootsAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task A_video_is_copied_and_indexed_like_a_picture_with_no_thumbnail_queued()
    {
        await using var rig = await ImportRig.StartAsync();
        var video = Path.Combine(rig.Lib.Dir, "Videos", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        MediaFiles.Mp4(video, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), seconds: 3, width: 640, height: 360);

        var result = await rig.Importer.PathAsync(video, rig.Target(), None);

        Assert.Equal(ImportOutcome.Copied, result.Outcome);
        Assert.EndsWith("/Unsorted/clip.mp4", result.Path, StringComparison.Ordinal);
        var mime = await Scalar<string>(rig.Lib.Db, $"SELECT mime FROM content WHERE content_id = {result.ContentId}");
        Assert.Equal("video/mp4", mime);
        Assert.Equal(3000, await Scalar<long>(rig.Lib.Db, $"SELECT duration_ms FROM content WHERE content_id = {result.ContentId}"));
        Assert.Equal(0, await Count(rig.Lib.Db, "SELECT count(*) FROM work_item WHERE kind = 'thumbnail'"));
        Assert.Equal(1, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task Dropped_on_an_album_the_copy_is_filed_under_its_name_and_joins_it()
    {
        await using var rig = await ImportRig.StartAsync();
        var album = await rig.Lib.Albums.EnsureAsync(TempLibrary.Account, "A: best/of?", "", None);
        var a = Path.Combine(rig.Lib.Dir, "in", "one.png");
        var b = Path.Combine(rig.Lib.Dir, "in", "two.png");
        TempLibrary.WritePng(a, seed: 30);
        TempLibrary.WritePng(b, seed: 31);

        var target = rig.Target(album, "A: best/of?");
        Assert.Equal(ImportOutcome.Copied, (await rig.Importer.PathAsync(a, target, None)).Outcome);
        Assert.Equal(ImportOutcome.Copied, (await rig.Importer.PathAsync(b, target, None)).Outcome);

        // A name Windows would refuse is made safe for the folder; the album keeps its name.
        Assert.True(File.Exists(Path.Combine(rig.TesseraDir, "A_ best_of_", "one.png")));
        var shown = await rig.Lib.Albums.GetAsync(TempLibrary.Account, album, None);
        Assert.Equal(2, shown!.ItemCount);
        Assert.Equal("A: best/of?", shown.Name);
        Assert.Equal("manual", shown.Origin);
    }

    [Fact]
    public async Task A_copy_that_fails_half_way_leaves_no_file_behind()
    {
        using var lib = new TempLibrary();
        var source = Path.Combine(lib.Dir, "big.png");
        File.WriteAllBytes(source, System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024));
        var folder = Path.Combine(lib.Dir, "Tessera", "Unsorted");
        var sha = await Hashing.Sha256FileAsync(source);

        // The disk goes away after the first megabyte.
        var failing = await Assert.ThrowsAsync<IOException>(() => SafeCopy.CopyFileAsync(
            source, folder, "big.png", sha, None, s => new FailAfter(s, 1024 * 1024)));
        Assert.Contains("unplugged", failing.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));

        // The source changed between the hash and the copy.
        var changed = await Assert.ThrowsAsync<IOException>(() => SafeCopy.CopyFileAsync(
            source, folder, "big.png", new byte[32], None));
        Assert.Contains("changed while it was being copied", changed.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));

        // A clean copy, for contrast, and the source never moved.
        var ok = await SafeCopy.CopyFileAsync(source, folder, "big.png", sha, None);
        Assert.False(ok.Existing);
        Assert.Single(Directory.GetFiles(folder));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_link_is_fetched_once_into_the_tessera_folder()
    {
        var png = PngBytes(seed: 40);
        var calls = 0;
        await using var rig = await ImportRig.StartAsync(new FakeHttp(request =>
        {
            Interlocked.Increment(ref calls);
            return request.RequestUri!.AbsolutePath switch
            {
                "/cat.png" => Bytes(png, "image/png"),
                "/photo" => Bytes(PngBytes(seed: 41), "image/png"),
                "/page" => Bytes("<html></html>"u8.ToArray(), "text/html"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));

        var cat = await rig.Importer.UrlAsync("https://example.test/cat.png", rig.Target(), None);
        Assert.Equal(ImportOutcome.Copied, cat.Outcome);
        Assert.Equal(png, File.ReadAllBytes(cat.Path!));
        Assert.EndsWith("/Unsorted/cat.png", cat.Path, StringComparison.Ordinal);

        // No extension in the link: the type names one.
        var photo = await rig.Importer.UrlAsync("https://example.test/photo", rig.Target(), None);
        Assert.EndsWith("/Unsorted/photo.png", photo.Path, StringComparison.Ordinal);

        // The same bytes again: fetched (there is no other way to know), not kept.
        var again = await rig.Importer.UrlAsync("https://example.test/cat.png", rig.Target(), None);
        Assert.Equal(ImportOutcome.Already, again.Outcome);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(rig.TesseraDir, "Unsorted")).Length);

        Assert.Equal(ImportOutcome.Skipped, (await rig.Importer.UrlAsync("https://example.test/page", rig.Target(), None)).Outcome);
        Assert.Equal("the server answered 404", (await rig.Importer.UrlAsync("https://example.test/gone.png", rig.Target(), None)).Reason);
        Assert.Equal(ImportOutcome.Skipped, (await rig.Importer.UrlAsync("file:///C:/Windows/win.ini", rig.Target(), None)).Outcome);
        Assert.Equal(5, calls);
        Assert.Empty(Directory.GetFiles(rig.TesseraDir, "*" + SafeCopy.PartialSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Bytes_from_the_webview_are_staged_kept_if_new_and_discarded_if_known()
    {
        await using var rig = await ImportRig.StartAsync();
        var png = PngBytes(seed: 50);

        var first = await rig.Importer.BytesAsync(new MemoryStream(png), "pasted.png", rig.Target(), None);
        var second = await rig.Importer.BytesAsync(new MemoryStream(png), "pasted again.png", rig.Target(), None);
        var text = await rig.Importer.BytesAsync(new MemoryStream("hello"u8.ToArray()), "notes.txt", rig.Target(), None);

        Assert.Equal((ImportOutcome.Copied, ImportOutcome.Already, ImportOutcome.Skipped), (first.Outcome, second.Outcome, text.Outcome));
        Assert.Equal(["pasted.png"], Directory.GetFiles(Path.Combine(rig.TesseraDir, "Unsorted")).Select(f => Path.GetFileName(f)).ToArray());
        Assert.Empty(Directory.GetFiles(rig.TesseraDir, "*" + SafeCopy.PartialSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_trashed_picture_added_again_comes_back()
    {
        await using var rig = await ImportRig.StartAsync();
        var outside = Path.Combine(rig.Lib.Dir, "Downloads", "x.png");
        TempLibrary.WritePng(outside, seed: 60);
        var first = await rig.Importer.PathAsync(outside, rig.Target(), None);
        await using (var conn = await rig.Lib.Db.OpenWriteAsync(None))
        {
            await conn.ExecuteAsync("UPDATE location SET trashed_at = 1 WHERE content_id = @Id", new { Id = first.ContentId });
        }

        Assert.Equal(0, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
        var again = await rig.Importer.PathAsync(outside, rig.Target(), None);

        Assert.Equal(ImportOutcome.Already, again.Outcome);
        Assert.Equal("it was in the trash; restored", again.Reason);
        Assert.Equal(1, await rig.Lib.Timeline.CountAsync(TempLibrary.Account, None));
    }

    [Fact]
    public async Task Not_a_picture_and_not_there_are_skipped_with_reasons()
    {
        await using var rig = await ImportRig.StartAsync();
        var text = Path.Combine(rig.Lib.Dir, "readme.txt");
        File.WriteAllText(text, "hi");

        Assert.Equal("not a picture or video", (await rig.Importer.PathAsync(text, rig.Target(), None)).Reason);
        Assert.Equal("not found", (await rig.Importer.PathAsync(Path.Combine(rig.Lib.Dir, "nope.png"), rig.Target(), None)).Reason);
    }

    [Fact]
    public async Task The_runner_reports_progress_and_counts_and_records_them()
    {
        await using var rig = await ImportRig.StartAsync();
        var photos = Path.Combine(rig.Lib.Dir, "photos");
        var inside = Path.Combine(photos, "a.png");
        TempLibrary.WritePng(inside, seed: 70);
        await rig.Lib.Library.AddRootAsync(TempLibrary.Account, PathRules.Canonical(photos), "photos", None);
        var outside = Path.Combine(rig.Lib.Dir, "out", "b.png");
        TempLibrary.WritePng(outside, seed: 71);
        var folder = Path.Combine(rig.Lib.Dir, "folder");
        TempLibrary.WritePng(Path.Combine(folder, "c.png"), seed: 72);

        var runner = new ImportRunner(rig.Importer, new Data.Repositories.ImportRepository(rig.Lib.Db), NullLogger<ImportRunner>.Instance);
        await runner.StartAsync(None);
        try
        {
            var job = await runner.EnqueueAsync(TempLibrary.Account, null, "Unsorted",
                [new(ImportKind.File, inside), new(ImportKind.File, outside), new(ImportKind.Folder, folder),
                 new(ImportKind.File, inside), new(ImportKind.File, Path.Combine(rig.Lib.Dir, "missing.jpg"))], None);
            Assert.Equal(5, job.Total);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!job.Finished && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var s = job.Snapshot();
            Assert.True(s.Finished);
            Assert.Equal(5, s.Done);
            Assert.Equal(new Data.Repositories.ImportCounts(Added: 1, Already: 1, Copied: 1, Skipped: 1, Folders: 1), s.Counts);
            Assert.Equal(ImportOutcome.Skipped, s.Items[4].Result.Outcome);

            var recorded = await new Data.Repositories.ImportRepository(rig.Lib.Db).LatestAsync(None);
            Assert.Equal((5, 1, 1, 1, 1, 1), (recorded!.Done, recorded.Added, recorded.Already, recorded.Copied, recorded.Skipped, recorded.Folders));
            Assert.NotNull(recorded.FinishedAt);
        }
        finally
        {
            await runner.StopAsync(None);
        }
    }

    // ------------------------------------------------------------ rig

    private sealed class ImportRig : IAsyncDisposable
    {
        private ImportRig(TempLibrary lib, ScanRunner scans, Importer importer, string tesseraDir)
        {
            Lib = lib;
            Scans = scans;
            Importer = importer;
            TesseraDir = tesseraDir;
        }

        public TempLibrary Lib { get; }
        public ScanRunner Scans { get; }
        public Importer Importer { get; }
        public string TesseraDir { get; }

        public ImportTarget Target(long? albumId = null, string? albumName = null) =>
            new(TempLibrary.Account, albumId, albumName ?? "Unsorted");

        public static async Task<ImportRig> StartAsync(HttpMessageHandler? http = null)
        {
            var lib = new TempLibrary();
            var tesseraDir = Path.Combine(lib.Dir, "Pictures", "Tessera");
            var scanner = lib.Scanner();
            var scans = new ScanRunner(scanner, lib.Library, lib.Db, NullLogger<ScanRunner>.Instance);
            await scans.StartAsync(None);
            var folder = new TesseraFolder(lib.Settings, lib.Library,
                Options.Create(new TesseraFolderOptions { DefaultPath = PathRules.Canonical(tesseraDir) }));
            var importer = new Importer(lib.Library, lib.Content, lib.Albums, lib.WorkItems, lib.Tier, scanner, scans, folder,
                new ImportHttp(new HttpClient(http ?? new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))),
                NullLogger<Importer>.Instance);
            return new ImportRig(lib, scans, importer, tesseraDir);
        }

        public async ValueTask DisposeAsync()
        {
            await Scans.StopAsync(None);
            Scans.Dispose();
            Lib.Dispose();
        }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    /// <summary>A stream that throws once it has given <paramref name="limit"/> bytes, as an unplugged disk does.</summary>
    private sealed class FailAfter(Stream inner, long limit) : Stream
    {
        private long _given;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_given >= limit)
            {
                throw new IOException("The device was unplugged.");
            }

            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _given)], cancellationToken);
            _given += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Bytes(byte[] body, string type)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] PngBytes(int seed)
    {
        var path = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N") + ".png");
        try
        {
            TempLibrary.WritePng(path, seed);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (long Length, DateTime Mtime, string Sha) Snapshot(string path) =>
        (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hashing.ToHex(Hashing.Sha256FileAsync(path).GetAwaiter().GetResult()));

    private static async Task<long> Count(Db db, string sql)
    {
        await using var conn = await db.OpenAsync(None);
        return await conn.ExecuteScalarAsync<long>(sql);
    }

    private static async Task<T> Scalar<T>(Db db, string sql)
    {
        await using var conn = await db.OpenAsync(None);
        return (await conn.ExecuteScalarAsync<T>(sql))!;
    }
}
