using System.Globalization;
using System.Text.RegularExpressions;

namespace Tessera.Core;

/// <summary>Where a picture's capture time came from, best first. Stored in <c>content.taken_src</c>.</summary>
public static class TakenSource
{
    /// <summary>A date written inside the file: EXIF, XMP, or a QuickTime/MP4 header.</summary>
    public const string Exif = "exif";

    /// <summary>A date in the file's name, as cameras, phones and screenshot tools write them.</summary>
    public const string FileName = "filename";

    /// <summary>The file's modified time: what is left when the file says nothing.</summary>
    public const string Mtime = "mtime";

    /// <summary>Higher is better. A later sighting only replaces a date with a strictly better one.</summary>
    public static int Rank(string? source) => source switch
    {
        Exif => 3,
        FileName => 2,
        Mtime => 1,
        _ => 0,
    };
}

/// <summary>
/// Turns the dates a file carries - header strings and file names - into
/// instants, or says it cannot.
/// </summary>
/// <remarks>
/// <para>
/// THE FLOATING-TIME RULE. An EXIF time with no offset is a wall-clock
/// reading in a zone nobody wrote down. It is read in the zone of the
/// machine doing the scan, using that zone's rules for that date (so a July
/// photo in London is read as BST, a January one as GMT), and stored as the
/// UTC instant that results. The library is one person's, on their own
/// machine, and most of their pictures were taken where they live; read
/// there, the instant sorts correctly against the pictures that do carry an
/// offset and against file times, which are true instants. Storing the
/// digits as if they were UTC would show the right clock face but sort
/// those pictures up to fourteen hours away from everything else.
/// </para>
/// <para>
/// An hour that happens twice (clocks going back) is read as standard time;
/// an hour that never happened (clocks going forward) gets the standard
/// offset, which lands it just after the jump. Both are what
/// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> does, and neither
/// throws.
/// </para>
/// <para>
/// A date is refused, and the next source tried, when it cannot be a capture
/// time: before 1900, more than a day in the future, or not a calendar date
/// at all (month 13, 30 February, hour 25). "Plausible" stops there; a
/// camera whose clock was never set says 2000-01-01, and that is what it is
/// shown as.
/// </para>
/// </remarks>
public static partial class CaptureDates
{
    /// <summary>
    /// What the header reader knows how to find, stored per content as
    /// <c>content.meta_version</c>. A content read by an older reader (0 =
    /// never read) is read again by the backfill, which is how a better
    /// reader reaches a library that already exists. Bumping it needs a
    /// migration that rebuilds <c>content_meta_pending_idx</c> with the new
    /// number, since SQL compares against this constant as a literal so the
    /// partial index applies.
    /// </summary>
    public const int ReaderVersion = 1;

    /// <summary>Nothing earlier than this is a capture date.</summary>
    public const int EarliestYear = 1900;

    /// <summary>Slack for a clock a few hours ahead, or a zone east of here.</summary>
    public static readonly TimeSpan FutureSlack = TimeSpan.FromDays(1);

    /// <summary>A date-only name is read as noon, so it stays on its day in any zone within twelve hours.</summary>
    private static readonly TimeSpan DateOnlyTime = TimeSpan.FromHours(12);

    private static readonly string[] ExifFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
    ];

    public static bool IsPlausible(DateTimeOffset t, DateTimeOffset now) =>
        t.Year >= EarliestYear && t <= now + FutureSlack;

    /// <summary>A wall-clock reading in <paramref name="zone"/>, as an instant (see the floating-time rule).</summary>
    public static DateTimeOffset InZone(DateTime wallClock, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>
    /// An EXIF date (<c>2024:01:01 12:34:56</c>) with its optional sub-second
    /// digits (<c>SubSecTimeOriginal</c>) and offset (<c>OffsetTimeOriginal</c>,
    /// <c>+05:30</c>).
    /// </summary>
    /// <returns>Null for blanks, the all-zero "no date" value, and anything implausible.</returns>
    public static DateTimeOffset? FromExif(string? value, string? subSeconds, string? offset, TimeZoneInfo zone, DateTimeOffset now)
    {
        var text = value?.Trim().TrimEnd('\0').Trim();
        if (text is null || text.Length < 19)
        {
            return null;
        }

        if (!DateTime.TryParseExact(text[..19], ExifFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }

        // A few writers put the fraction or the offset on the end of the
        // value itself rather than in the tags made for them.
        var rest = text[19..];
        if (rest.StartsWith('.'))
        {
            var digits = rest[1..].TakeWhile(char.IsAsciiDigit).Count();
            subSeconds ??= rest.Substring(1, digits);
            rest = rest[(1 + digits)..];
        }

        if (offset is null && rest.Length > 0)
        {
            offset = rest;
        }

        wall = wall.Add(Fraction(subSeconds));
        var t = TryOffset(offset, out var off) ? new DateTimeOffset(wall, off) : InZone(wall, zone);
        return IsPlausible(t, now) ? t : null;
    }

    /// <summary>
    /// An XMP date: ISO 8601, with or without an offset, possibly date-only
    /// (<c>photoshop:DateCreated</c> is often just <c>2024-01-01</c>).
    /// </summary>
    public static DateTimeOffset? FromXmp(string? value, TimeZoneInfo zone, DateTimeOffset now)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 10)
        {
            return null;
        }

        DateTimeOffset t;
        if (XmpHasOffset().IsMatch(text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            {
                return null;
            }
        }
        else
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            {
                return null;
            }

            t = InZone(text.Contains('T', StringComparison.Ordinal) ? wall : wall.Date + DateOnlyTime, zone);
        }

        return IsPlausible(t, now) ? t : null;
    }

    /// <summary>
    /// A date in a file name, from the patterns cameras, phones and
    /// screenshot tools actually write, most specific first. The first
    /// pattern that matches decides: a name whose date is impossible
    /// (IMG_20240230_…, "13.23.45 PM") gives no date at all, rather than
    /// being repaired or rescued by a looser pattern further down.
    /// </summary>
    /// <param name="fileName">The name only, never the folder: a folder called "2024-01-01 Trip" says when the folder was made.</param>
    public static DateTimeOffset? FromFileName(string fileName, TimeZoneInfo zone, DateTimeOffset now)
    {
        var name = Path.GetFileName(fileName);
        foreach (var pattern in NamePatterns)
        {
            var m = pattern.Match(name);
            if (m.Success)
            {
                return FromMatch(m, zone) is { } t && IsPlausible(t, now) ? t : null;
            }
        }

        return null;
    }

    /// <summary>
    /// In order. Each anchors on digits that are not part of a longer number,
    /// so a counter like <c>photo00012</c> never reads as a date.
    /// </summary>
    private static readonly Regex[] NamePatterns =
    [
        CompactWithSeparator(),   // IMG_20240101_123456, VID_…, PXL_20240101_123456789, 20240101_123456, Screenshot_20240101-123456
        CompactFourteen(),        // IMG20240101123456 (OnePlus, Oppo), 20240101123456
        Separated(),              // 2024-01-01 12.34.56, 2024-01-01 12_34_56, Screenshot_2024-01-01-12-34-56, Screenshot 2024-01-01 at 1.23.45 PM, Screenshot 2024-01-01 123456, signal-2024-01-01-123456
        WhatsApp(),               // IMG-20240101-WA0001: the day only
        SeparatedDate(),          // 2019-07-04 fireworks: the day only
    ];

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<mo>\d{2})(?<d>\d{2})[_-](?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})(?<ms>\d{3})?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactWithSeparator();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<mo>\d{2})(?<d>\d{2})(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactFourteen();

    // A one-digit hour only with a separator after it (macOS: "at 1.23.45 PM"),
    // so "2024-01-01 12345" is not read as 01:23:45.
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<ds>[-_.])(?<mo>\d{2})\k<ds>(?<d>\d{2})(?:[ _T-]|\s+at\s+)(?:(?<h>\d{2})(?<ts>[-_.:]?)|(?<h>\d)(?<ts>[-_.:]))(?<mi>\d{2})\k<ts>(?<s>\d{2})(?:\s?(?<ap>[AaPp][Mm]))?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Separated();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<mo>\d{2})(?<d>\d{2})-WA\d", RegexOptions.CultureInvariant)]
    private static partial Regex WhatsApp();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<ds>[-_.])(?<mo>\d{2})\k<ds>(?<d>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedDate();

    [GeneratedRegex(@"T.*(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex XmpHasOffset();

    private static DateTimeOffset? FromMatch(Match m, TimeZoneInfo zone)
    {
        var y = Int(m, "y");
        var mo = Int(m, "mo");
        var d = Int(m, "d");
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo))
        {
            return null;
        }

        if (!m.Groups["h"].Success)
        {
            return InZone(new DateTime(y, mo, d) + DateOnlyTime, zone);
        }

        var h = Int(m, "h");
        var mi = Int(m, "mi");
        var s = Int(m, "s");
        if (m.Groups["ap"].Success)
        {
            if (h is < 1 or > 12)
            {
                return null;
            }

            var pm = char.ToUpperInvariant(m.Groups["ap"].Value[0]) == 'P';
            h = h % 12 + (pm ? 12 : 0);
        }

        if (h > 23 || mi > 59 || s > 59)
        {
            return null;
        }

        var ms = m.Groups["ms"].Success ? Int(m, "ms") : 0;
        return InZone(new DateTime(y, mo, d, h, mi, s, ms), zone);
    }

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].ValueSpan, CultureInfo.InvariantCulture);

    /// <summary>EXIF sub-seconds are the digits after the point: "5" is half a second, "123" is 123 ms.</summary>
    private static TimeSpan Fraction(string? digits)
    {
        var d = digits?.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(d) || !d.All(char.IsAsciiDigit))
        {
            return TimeSpan.Zero;
        }

        var seven = d.Length >= 7 ? d[..7] : d.PadRight(7, '0');
        return TimeSpan.FromTicks(long.Parse(seven, CultureInfo.InvariantCulture));
    }

    /// <summary>An EXIF offset, <c>+05:30</c> or <c>-0800</c>; blanks and nonsense are no offset.</summary>
    private static bool TryOffset(string? value, out TimeSpan offset)
    {
        offset = default;
        var text = value?.Trim().TrimEnd('\0').Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text is "Z" or "z")
        {
            return true;
        }

        var m = OffsetPattern().Match(text);
        if (!m.Success)
        {
            return false;
        }

        var hours = int.Parse(m.Groups["h"].ValueSpan, CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups["m"].ValueSpan, CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (m.Groups["sign"].Value == "-")
        {
            offset = -offset;
        }

        return true;
    }

    [GeneratedRegex(@"^(?<sign>[+-])(?<h>\d{2}):?(?<m>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();
}
