using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Avi;
using MetadataExtractor.Formats.Bmp;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;
using Tessera.Core;
using MeDirectory = MetadataExtractor.Directory;

namespace Tessera.Api.Media;

/// <summary>What a file's headers say about it. Every field may be missing.</summary>
/// <param name="Width">The shape as shown: an EXIF orientation of 5–8 (or a HEIF/QuickTime quarter turn) swaps the stored width and height.</param>
public readonly record struct HeaderFacts(DateTimeOffset? TakenAt, int? Width, int? Height, int? DurationMs);

/// <summary>A file's capture date with where it came from, and its shape.</summary>
public readonly record struct Captured(DateTimeOffset TakenAt, string TakenSrc, int? Width, int? Height, int? DurationMs);

/// <summary>
/// Reads capture dates and dimensions from a file's headers, never its pixels.
/// </summary>
/// <remarks>
/// <para>
/// MetadataExtractor walks the container - JPEG segments up to the start of
/// scan, PNG and RIFF chunks, ISO-BMFF boxes - and SEEKS past the parts it
/// does not want, so a 4 GB video costs the few kilobytes of its <c>moov</c>
/// box wherever it sits. The stream is wrapped in a budget all the same
/// (<see cref="ReadBudget"/>): a malformed file that sends a parser reading
/// is cut off and treated as saying nothing, rather than read to the end.
/// </para>
/// <para>
/// The order is the task's, best first: EXIF DateTimeOriginal, then
/// DateTimeDigitized, then DateTime, each with its own offset tag; then XMP
/// (<c>exif:DateTimeOriginal</c>, <c>photoshop:DateCreated</c>,
/// <c>xmp:CreateDate</c>), which is where PNG, WebP and edited files keep
/// theirs; then the QuickTime/MP4 creation time. All of these are
/// <see cref="TakenSource.Exif"/>: "a date the file itself carries".
/// </para>
/// </remarks>
public static class MediaHeaders
{
    /// <summary>Bytes a parser may read from one file before it is stopped. Headers are kilobytes.</summary>
    public const long ReadBudget = 4L << 20;

    /// <summary>QuickTime counts from 1904; a zero there is "not set", and so is anything before video existed.</summary>
    private static readonly DateTimeOffset EarliestVideo = new(1970, 1, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Open the file and read its headers. Throws when the file cannot be
    /// opened (gone, locked, offline) - that is "not read", and a caller
    /// retries it later. A file that opens but parses to nothing returns
    /// empty facts - that is "read, and it says nothing".
    /// </summary>
    public static HeaderFacts Read(string path, TimeZoneInfo zone, DateTimeOffset now)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            // Small: a header read is a few KB at the front and the odd seek.
            BufferSize = 16 * 1024,
            Options = FileOptions.RandomAccess,
        });
        return Read(stream, Path.GetFileName(path), zone, now);
    }

    /// <summary>
    /// Read headers from the front of a file already in memory, reaching into
    /// the open file only past it (a camera MP4's <c>moov</c> after its media,
    /// a TIFF's IFD at the end). The scanner calls this from inside its hash
    /// pass: one open per file, and for a photo no read beyond the hash's own.
    /// </summary>
    public static HeaderFacts Read(ReadOnlyMemory<byte> head, Stream file, string fileName, TimeZoneInfo zone, DateTimeOffset now) =>
        Read(new HeadFirstStream(head, file), fileName, zone, now);

    /// <param name="fileName">Used only to tell formats apart when the magic bytes are ambiguous.</param>
    public static HeaderFacts Read(Stream stream, string fileName, TimeZoneInfo zone, DateTimeOffset now)
    {
        IReadOnlyList<MeDirectory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(new BudgetStream(stream, ReadBudget), fileName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Unknown format (MKV, JXL), truncated, malformed, or over
            // budget: the file says nothing, and the caller falls back to the
            // name and then the file time. Never a scan error.
            return default;
        }

        var (w, h) = Shape(dirs);
        return new HeaderFacts(TakenAt(dirs, zone, now), w, h, DurationMs(dirs));
    }

    /// <summary>The date the file carries, else one in its name, else its modified time.</summary>
    public static Captured Resolve(HeaderFacts headers, string fileName, DateTimeOffset mtime, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (headers.TakenAt is { } exif)
        {
            return new Captured(exif, TakenSource.Exif, headers.Width, headers.Height, headers.DurationMs);
        }

        if (CaptureDates.FromFileName(fileName, zone, now) is { } named)
        {
            return new Captured(named, TakenSource.FileName, headers.Width, headers.Height, headers.DurationMs);
        }

        return new Captured(mtime, TakenSource.Mtime, headers.Width, headers.Height, headers.DurationMs);
    }

    // ------------------------------------------------------------- dates

    private static DateTimeOffset? TakenAt(IReadOnlyList<MeDirectory> dirs, TimeZoneInfo zone, DateTimeOffset now)
    {
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();

        if (sub is not null)
        {
            if (Exif(sub, ExifDirectoryBase.TagDateTimeOriginal, sub, ExifDirectoryBase.TagSubsecondTimeOriginal, ExifDirectoryBase.TagTimeZoneOriginal, zone, now) is { } original)
            {
                return original;
            }

            if (Exif(sub, ExifDirectoryBase.TagDateTimeDigitized, sub, ExifDirectoryBase.TagSubsecondTimeDigitized, ExifDirectoryBase.TagTimeZoneDigitized, zone, now) is { } digitized)
            {
                return digitized;
            }
        }

        // DateTime lives in IFD0; its sub-seconds and offset in the Exif sub-IFD.
        if (ifd0 is not null &&
            Exif(ifd0, ExifDirectoryBase.TagDateTime, sub, ExifDirectoryBase.TagSubsecondTime, ExifDirectoryBase.TagTimeZone, zone, now) is { } dateTime)
        {
            return dateTime;
        }

        foreach (var xmp in dirs.OfType<XmpDirectory>())
        {
            if (Xmp(xmp, zone, now) is { } x)
            {
                return x;
            }
        }

        return QuickTime(dirs, now) ?? Avi(dirs, zone, now);
    }

    private static DateTimeOffset? Exif(
        MeDirectory dir, int tag, MeDirectory? extras, int subSecTag, int offsetTag, TimeZoneInfo zone, DateTimeOffset now) =>
        CaptureDates.FromExif(dir.GetString(tag), extras?.GetString(subSecTag), extras?.GetString(offsetTag), zone, now);

    private const string NsExif = "http://ns.adobe.com/exif/1.0/";
    private const string NsPhotoshop = "http://ns.adobe.com/photoshop/1.0/";
    private const string NsXmp = "http://ns.adobe.com/xap/1.0/";

    private static DateTimeOffset? Xmp(XmpDirectory xmp, TimeZoneInfo zone, DateTimeOffset now)
    {
        var meta = xmp.XmpMeta;
        if (meta is null)
        {
            return null;
        }

        foreach (var (ns, name) in new[] { (NsExif, "DateTimeOriginal"), (NsPhotoshop, "DateCreated"), (NsXmp, "CreateDate") })
        {
            string? value;
            try
            {
                value = meta.GetPropertyString(ns, name);
            }
            catch (XmpCore.XmpException)
            {
                continue;
            }

            if (CaptureDates.FromXmp(value, zone, now) is { } t)
            {
                return t;
            }
        }

        return null;
    }

    private static DateTimeOffset? QuickTime(IReadOnlyList<MeDirectory> dirs, DateTimeOffset now)
    {
        // Apple's com.apple.quicktime.creationdate carries the offset the
        // phone was in; the movie header's is UTC by the spec (and local
        // time on some cameras, which nothing in the file can reveal).
        foreach (var meta in dirs.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            var text = meta.GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate);
            if (text is not null &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) &&
                t >= EarliestVideo && CaptureDates.IsPlausible(t, now))
            {
                return t;
            }
        }

        foreach (var movie in dirs.OfType<QuickTimeMovieHeaderDirectory>())
        {
            if (movie.GetObject(QuickTimeMovieHeaderDirectory.TagCreated) is DateTime created)
            {
                var t = new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc));
                if (t >= EarliestVideo && CaptureDates.IsPlausible(t, now))
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? Avi(IReadOnlyList<MeDirectory> dirs, TimeZoneInfo zone, DateTimeOffset now)
    {
        // IDIT, written as ctime(): "Mon Jan 01 12:34:56 2024", local to the camera.
        var text = dirs.OfType<AviDirectory>().FirstOrDefault()?.GetString(AviDirectory.TagDateTimeOriginal)?.Trim();
        if (text is null)
        {
            return null;
        }

        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return DateTime.TryParseExact(text, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var wall) &&
               CaptureDates.InZone(wall, zone) is var t && CaptureDates.IsPlausible(t, now)
            ? t
            : null;
    }

    // ------------------------------------------------------------- shape

    private static (int? Width, int? Height) Shape(IReadOnlyList<MeDirectory> dirs)
    {
        // HEIF: the largest image property is the primary (the others are
        // the thumbnail and the grid's tiles); its irot is the turn to apply.
        var heif = dirs.OfType<HeicImagePropertiesDirectory>()
            .Select(d => (Dir: d, W: Int(d, HeicImagePropertiesDirectory.TagImageWidth), H: Int(d, HeicImagePropertiesDirectory.TagImageHeight)))
            .Where(x => x.W > 0 && x.H > 0)
            .OrderByDescending(x => (long)x.W!.Value * x.H!.Value)
            .FirstOrDefault();
        if (heif.Dir is not null)
        {
            var turn = dirs.OfType<HeicImagePropertiesDirectory>().Select(d => Int(d, HeicImagePropertiesDirectory.TagRotation)).FirstOrDefault(r => r is not null);
            return Turned(heif.W, heif.H, turn is 90 or 270);
        }

        var video = dirs.OfType<QuickTimeTrackHeaderDirectory>()
            .Select(d => (Dir: d, W: Int(d, QuickTimeTrackHeaderDirectory.TagWidth), H: Int(d, QuickTimeTrackHeaderDirectory.TagHeight)))
            .FirstOrDefault(x => x.W > 0 && x.H > 0);
        if (video.Dir is not null)
        {
            var turn = Int(video.Dir, QuickTimeTrackHeaderDirectory.TagRotation);
            return Turned(video.W, video.H, turn is 90 or -90 or 270 or -270);
        }

        var (w, h) = Format(dirs);
        if (w is null || h is null)
        {
            return (null, null);
        }

        // EXIF orientation 5-8 are the quarter turns: what is shown is
        // portrait when what is stored is landscape, and the grid needs
        // what is shown. The thumbnail worker turns the pixels the same way.
        var orientation = dirs.OfType<ExifIfd0Directory>().Select(d => Int(d, ExifDirectoryBase.TagOrientation)).FirstOrDefault(o => o is not null);
        return Turned(w, h, orientation is >= 5 and <= 8);
    }

    private static (int? Width, int? Height) Format(IReadOnlyList<MeDirectory> dirs)
    {
        foreach (var d in dirs)
        {
            var (w, h) = d switch
            {
                JpegDirectory => (Int(d, JpegDirectory.TagImageWidth), Int(d, JpegDirectory.TagImageHeight)),
                PngDirectory => (Int(d, PngDirectory.TagImageWidth), Int(d, PngDirectory.TagImageHeight)),
                WebPDirectory => (Int(d, WebPDirectory.TagImageWidth), Int(d, WebPDirectory.TagImageHeight)),
                GifHeaderDirectory => (Int(d, GifHeaderDirectory.TagImageWidth), Int(d, GifHeaderDirectory.TagImageHeight)),
                BmpHeaderDirectory => (Int(d, BmpHeaderDirectory.TagImageWidth), Int(d, BmpHeaderDirectory.TagImageHeight)),
                AviDirectory => (Int(d, AviDirectory.TagWidth), Int(d, AviDirectory.TagHeight)),
                _ => (null, null),
            };
            if (w > 0 && h > 0)
            {
                return (w, Math.Abs(h.Value));   // a bottom-up BMP stores a negative height
            }
        }

        // TIFF and the RAW formats: the Exif sub-IFD's pixel dimensions are
        // the image's; IFD0 in a RAW file is often a small preview.
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (sub is not null && Int(sub, ExifDirectoryBase.TagExifImageWidth) is > 0 and var ew && Int(sub, ExifDirectoryBase.TagExifImageHeight) is > 0 and var eh)
        {
            return (ew, eh);
        }

        return (null, null);
    }

    private static (int? Width, int? Height) Turned(int? w, int? h, bool quarterTurn) => quarterTurn ? (h, w) : (w, h);

    private static int? DurationMs(IReadOnlyList<MeDirectory> dirs)
    {
        foreach (var movie in dirs.OfType<QuickTimeMovieHeaderDirectory>())
        {
            var ms = movie.GetObject(QuickTimeMovieHeaderDirectory.TagDuration) switch
            {
                TimeSpan span => span.TotalMilliseconds,
                IConvertible units when movie.GetObject(QuickTimeMovieHeaderDirectory.TagTimeScale) is IConvertible scale
                                        && scale.ToDouble(CultureInfo.InvariantCulture) > 0
                    => units.ToDouble(CultureInfo.InvariantCulture) * 1000 / scale.ToDouble(CultureInfo.InvariantCulture),
                _ => 0,
            };
            if (ms is > 0 and < int.MaxValue)
            {
                return (int)ms;
            }
        }

        return null;
    }

    private static int? Int(MeDirectory d, int tag)
    {
        try
        {
            return d.TryGetInt32(tag, out var v) ? v : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file, read from <c>head</c> while the position is inside it and
    /// from the file itself past it. Seekable both ways; never writes.
    /// </summary>
    private sealed class HeadFirstStream(ReadOnlyMemory<byte> head, Stream file) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Math.Max(file.Length, head.Length);

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position < head.Length)
            {
                var n = (int)Math.Min(buffer.Length, head.Length - _position);
                head.Span.Slice((int)_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            file.Position = _position;
            var read = file.Read(buffer);
            _position += read;
            return read;
        }

        public override int ReadByte()
        {
            if (_position < head.Length)
            {
                return head.Span[(int)_position++];
            }

            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Passes reads through and counts them; past the budget, it throws.
    /// Seeks are free - skipping a video's media data is the point - and
    /// not counted.
    /// </summary>
    private sealed class BudgetStream(Stream inner, long budget) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override int ReadByte()
        {
            var b = inner.ReadByte();
            if (b >= 0)
            {
                Count(1);
            }

            return b;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int n)
        {
            _read += n;
            if (_read > budget)
            {
                throw new InvalidDataException($"header read passed {budget} bytes; treating the file as saying nothing");
            }

            return n;
        }
    }
}
