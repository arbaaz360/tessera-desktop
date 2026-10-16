using SkiaSharp;
using Tessera.Api;
using Tessera.Api.Media;
using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The header reader over real bytes: EXIF in a JPEG with and without an
/// offset, the order of the three EXIF dates, eXIf and XMP in a PNG, an MP4
/// movie header, the fallbacks, the shape - and that none of it reads past
/// the headers.
/// </summary>
public sealed class MediaHeaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = CaptureDateTests.Plus0530;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tessera-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void A_jpeg_with_DateTimeOriginal_and_OffsetTimeOriginal_is_that_instant()
    {
        var path = PathFor("a.jpg");
        MediaFiles.Jpeg(path, new ExifFields(DateTimeOriginal: "2023:07:14 09:15:00", OffsetTimeOriginal: "+02:00", SubSecTimeOriginal: "250"));

        var h = MediaHeaders.Read(path, Zone, Now);
        Assert.Equal(new DateTimeOffset(2023, 7, 14, 9, 15, 0, 250, TimeSpan.FromHours(2)), h.TakenAt);
        Assert.Equal((96, 64), (h.Width, h.Height));
    }

    [Fact]
    public void A_jpeg_without_an_offset_is_read_in_the_local_zone()
    {
        var path = PathFor("b.jpg");
        MediaFiles.Jpeg(path, new ExifFields(DateTimeOriginal: "2023:07:14 09:15:00"));

        var h = MediaHeaders.Read(path, Zone, Now);
        Assert.Equal(new DateTime(2023, 7, 14, 3, 45, 0), h.TakenAt!.Value.UtcDateTime);
    }

    [Fact]
    public void Original_beats_digitized_beats_datetime()
    {
        var all = PathFor("all.jpg");
        MediaFiles.Jpeg(all, new ExifFields(DateTimeOriginal: "2021:01:01 01:01:01", DateTimeDigitized: "2022:02:02 02:02:02", DateTime: "2023:03:03 03:03:03"));
        Assert.Equal(2021, MediaHeaders.Read(all, Zone, Now).TakenAt!.Value.Year);

        var two = PathFor("two.jpg");
        MediaFiles.Jpeg(two, new ExifFields(DateTimeOriginal: "0000:00:00 00:00:00", DateTimeDigitized: "2022:02:02 02:02:02", DateTime: "2023:03:03 03:03:03"));
        Assert.Equal(2022, MediaHeaders.Read(two, Zone, Now).TakenAt!.Value.Year);

        var one = PathFor("one.jpg");
        MediaFiles.Jpeg(one, new ExifFields(DateTime: "2023:03:03 03:03:03"));
        Assert.Equal(2023, MediaHeaders.Read(one, Zone, Now).TakenAt!.Value.Year);
    }

    [Fact]
    public void A_png_carries_its_date_in_eXIf_or_in_XMP()
    {
        var exif = PathFor("exif.png");
        MediaFiles.Png(exif, new ExifFields(DateTimeOriginal: "2020:05:06 07:08:09", OffsetTimeOriginal: "-04:00"));
        Assert.Equal(new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.FromHours(-4)), MediaHeaders.Read(exif, Zone, Now).TakenAt);

        var xmp = PathFor("xmp.png");
        MediaFiles.Png(xmp, xmpCreateDate: "2019-03-04T05:06:07+01:00", width: 50, height: 70);
        var h = MediaHeaders.Read(xmp, Zone, Now);
        Assert.Equal(new DateTimeOffset(2019, 3, 4, 5, 6, 7, TimeSpan.FromHours(1)), h.TakenAt);
        Assert.Equal((50, 70), (h.Width, h.Height));
    }

    [Fact]
    public void An_mp4_gives_its_creation_time_duration_and_shape()
    {
        var path = PathFor("clip.mp4");
        MediaFiles.Mp4(path, new DateTime(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc), seconds: 12.5, width: 1920, height: 1080);

        var h = MediaHeaders.Read(path, Zone, Now);
        Assert.Equal(new DateTimeOffset(2022, 8, 9, 10, 11, 12, TimeSpan.Zero), h.TakenAt);
        Assert.Equal(12_500, h.DurationMs);
        Assert.Equal((1920, 1080), (h.Width, h.Height));
    }

    [Fact]
    public void A_phone_video_turned_a_quarter_is_portrait()
    {
        var path = PathFor("portrait.mp4");
        MediaFiles.Mp4(path, new DateTime(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc), seconds: 3, width: 1920, height: 1080, quarterTurn: true);
        var h = MediaHeaders.Read(path, Zone, Now);
        Assert.Equal((1080, 1920), (h.Width, h.Height));
    }

    [Fact]
    public void An_mp4_whose_creation_time_was_never_set_says_nothing()
    {
        var path = PathFor("unset.mp4");
        MediaFiles.Mp4(path, createdUtc: null, seconds: 1, width: 640, height: 480);
        Assert.Null(MediaHeaders.Read(path, Zone, Now).TakenAt);
    }

    [Fact]
    public void An_exif_orientation_of_a_quarter_turn_swaps_the_shape_and_the_thumbnail_turns_with_it()
    {
        var path = PathFor("turned.jpg");
        MediaFiles.Jpeg(path, new ExifFields(DateTimeOriginal: "2023:07:14 09:15:00", Orientation: 6), width: 96, height: 64);

        var h = MediaHeaders.Read(path, Zone, Now);
        Assert.Equal((64, 96), (h.Width, h.Height));

        var (bitmap, origin) = SkiaFiles.DecodeWithOrigin(path);
        using (bitmap)
        {
            Assert.Equal(SKEncodedOrigin.RightTop, origin);
            Assert.Equal((96, 64), (bitmap!.Width, bitmap.Height));   // stored sideways
            using var turned = SkiaFiles.Orient(bitmap, origin);
            Assert.Equal((64, 96), (turned.Width, turned.Height));
        }
    }

    [Fact]
    public void Orient_moves_the_top_left_pixel_where_exif_says()
    {
        using var source = new SKBitmap(4, 2);
        source.Erase(SKColors.Black);
        source.SetPixel(0, 0, SKColors.Red);

        // 6: turn a quarter clockwise. The stored top-left ends top-right.
        using var six = SkiaFiles.Orient(source, SKEncodedOrigin.RightTop);
        Assert.Equal((2, 4), (six.Width, six.Height));
        Assert.Equal(SKColors.Red, six.GetPixel(1, 0));

        // 8: a quarter anticlockwise. It ends bottom-left.
        using var eight = SkiaFiles.Orient(source, SKEncodedOrigin.LeftBottom);
        Assert.Equal(SKColors.Red, eight.GetPixel(0, 3));

        // 3: upside down. Bottom-right.
        using var three = SkiaFiles.Orient(source, SKEncodedOrigin.BottomRight);
        Assert.Equal(SKColors.Red, three.GetPixel(3, 1));

        Assert.Same(source, SkiaFiles.Orient(source, SKEncodedOrigin.TopLeft));
    }

    [Fact]
    public void Headers_are_read_never_the_pixels()
    {
        // A big noisy JPEG: megabytes of entropy-coded data behind the EXIF.
        var jpeg = PathFor("big.jpg");
        MediaFiles.Jpeg(jpeg, new ExifFields(DateTimeOriginal: "2023:07:14 09:15:00"), width: 2000, height: 1500, noise: true);
        var jpegRead = BytesReadFor(jpeg, out var jpegFacts);
        Assert.NotNull(jpegFacts.TakenAt);
        Assert.True(new FileInfo(jpeg).Length > 2_000_000);
        Assert.True(jpegRead < 64 * 1024, $"read {jpegRead} bytes of a {new FileInfo(jpeg).Length}-byte JPEG");

        // A camera MP4 with 16 MB of media BEFORE its movie header: the
        // reader must seek past it, not read it.
        var mp4 = PathFor("camera.mp4");
        MediaFiles.Mp4(mp4, new DateTime(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc), 60, 3840, 2160, mediaBytes: 16 << 20, moovLast: true);
        var mp4Read = BytesReadFor(mp4, out var mp4Facts);
        Assert.Equal(2022, mp4Facts.TakenAt!.Value.Year);
        Assert.True(mp4Read < 64 * 1024, $"read {mp4Read} bytes of a {new FileInfo(mp4).Length}-byte MP4");
    }

    private static long BytesReadFor(string path, out HeaderFacts facts)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        var counting = new CountingStream(file);
        facts = MediaHeaders.Read(counting, Path.GetFileName(path), Zone, Now);
        return counting.BytesRead;
    }

    [Fact]
    public void A_file_with_no_metadata_falls_back_to_its_name_then_its_modified_time()
    {
        var mtime = new DateTimeOffset(2018, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var plain = PathFor("plain.png");
        MediaFiles.Png(plain);
        var nothing = MediaHeaders.Read(plain, Zone, Now);
        Assert.Null(nothing.TakenAt);
        Assert.Equal((96, 64), (nothing.Width, nothing.Height));

        var byTime = MediaHeaders.Resolve(nothing, "plain.png", mtime, Zone, Now);
        Assert.Equal((mtime, TakenSource.Mtime), (byTime.TakenAt, byTime.TakenSrc));

        var byName = MediaHeaders.Resolve(nothing, "IMG_20200102_030405.png", mtime, Zone, Now);
        Assert.Equal(TakenSource.FileName, byName.TakenSrc);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(330)), byName.TakenAt);

        // A header date beats the name.
        var exif = new HeaderFacts(new DateTimeOffset(2015, 6, 7, 8, 9, 10, TimeSpan.Zero), null, null, null);
        Assert.Equal(TakenSource.Exif, MediaHeaders.Resolve(exif, "IMG_20200102_030405.png", mtime, Zone, Now).TakenSrc);
    }

    [Fact]
    public void A_file_that_is_not_media_says_nothing_and_does_not_throw()
    {
        var junk = PathFor("junk.mkv");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(junk, [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(default, MediaHeaders.Read(junk, Zone, Now));

        var truncated = PathFor("truncated.jpg");
        MediaFiles.Jpeg(truncated, new ExifFields(DateTimeOriginal: "2023:07:14 09:15:00"));
        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..40]);
        _ = MediaHeaders.Read(truncated, Zone, Now);   // whatever it finds, it returns

        Assert.ThrowsAny<IOException>(() => MediaHeaders.Read(PathFor("absent.jpg"), Zone, Now));
    }
}
