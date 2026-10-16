using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Tessera.Api;
using Tessera.Api.Licence;
using Tessera.Api.Workers;
using Tessera.Data;
using Tessera.Data.Migrations;
using Tessera.Data.Repositories;

namespace Tessera.Tests;

/// <summary>
/// A migrated library.db in a temporary folder, with every repository over
/// it, gone when the test is. The real schema, the real SQL: nothing here is
/// a fake of the thing under test.
/// </summary>
internal sealed class TempLibrary : IDisposable
{
    public TempLibrary()
    {
        Dir = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ThumbsDir);
        Db = new Db(Path.Combine(Dir, "library.db"));
        new SchemaMigrator(Db).ApplyAsync(CancellationToken.None).GetAwaiter().GetResult();

        Content = new ContentRepository(Db);
        Timeline = new TimelineRepository(Db);
        Albums = new AlbumRepository(Db);
        Folders = new FolderRepository(Db);
        Viewable = new ViewableContentRepository(Db);
        Jobs = new JobRepository(Db);
        Settings = new SettingsRepository(Db);
        Library = new LibraryRepository(Db);
        WorkItems = new WorkItems(Db);
        Exclusive = new ExclusiveWork();
        Tier = new FreeTier(Settings, Library);
    }

    public string Dir { get; }
    public string ThumbsDir => Path.Combine(Dir, "thumbs");
    public Db Db { get; }
    public ContentRepository Content { get; }
    public TimelineRepository Timeline { get; }
    public AlbumRepository Albums { get; }
    public FolderRepository Folders { get; }
    public ViewableContentRepository Viewable { get; }
    public JobRepository Jobs { get; }
    public SettingsRepository Settings { get; }
    public LibraryRepository Library { get; }
    public WorkItems WorkItems { get; }
    public ExclusiveWork Exclusive { get; }
    public FreeTier Tier { get; }

    public const long Account = Core.Domain.Account.BuiltInId;

    /// <param name="zone">Where a capture time with no offset is read; the machine's own when null.</param>
    public Scanner Scanner(int concurrency = 4, TimeZoneInfo? zone = null) => new(
        Db, Content, Folders, Library, Exclusive, Tier,
        Options.Create(new ScannerOptions { Concurrency = concurrency, LocalZone = zone ?? TimeZoneInfo.Local }),
        NullLogger<Scanner>.Instance);

    public HeaderBackfill Backfill(TimeZoneInfo? zone = null) => new(
        Content, Exclusive,
        Options.Create(new ScannerOptions { LocalZone = zone ?? TimeZoneInfo.Local }),
        NullLogger<HeaderBackfill>.Instance);

    /// <summary>A file the scanner could have seen: random bytes, so a random hash.</summary>
    public static ScannedFile File(string relPath, DateTimeOffset? takenAt = null, long size = 1234, string mime = "image/png") =>
        new(RandomNumberGenerator.GetBytes(32), size, mime, relPath, DateTimeOffset.UtcNow, takenAt ?? DateTimeOffset.UtcNow, "mtime", null, null);

    /// <summary>A root row for a path that need not exist on disk.</summary>
    public Task<long> AddRootAsync(string absPath = "C:/photos", string label = "photos") =>
        Library.AddRootAsync(Account, absPath, label, CancellationToken.None);

    /// <summary>Content and a live location in one call. Returns the content id.</summary>
    public async Task<long> PlaceAsync(long rootId, string relPath, DateTimeOffset? takenAt = null)
    {
        var f = File(relPath, takenAt);
        var id = await Content.UpsertAsync(f, CancellationToken.None);
        await Content.RecordLocationAsync(id, Account, rootId, f, CancellationToken.None);
        return id;
    }

    /// <summary>Write a small PNG with Skia: a flat colour and a circle, distinct per seed.</summary>
    public static void WritePng(string path, int seed, int width = 96, int height = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor((byte)(seed * 37), (byte)(seed * 91), (byte)(seed * 53)));
        using var paint = new SKPaint { Color = new SKColor((byte)(255 - seed * 37), (byte)(seed * 7), (byte)(seed * 11)) };
        canvas.DrawCircle(width / 2f, height / 2f, 8 + seed % 13, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        System.IO.File.WriteAllBytes(path, data.ToArray());
    }

    public void Dispose()
    {
        Db.Dispose();
        Exclusive.Dispose();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
