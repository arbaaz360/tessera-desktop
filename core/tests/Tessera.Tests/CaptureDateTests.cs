using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The date rules on their own: EXIF strings with and without an offset, the
/// zone a floating time is read in, every file-name pattern, and the dates
/// that must be refused rather than repaired.
/// </summary>
public sealed class CaptureDateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A fixed +05:30 zone, so no test depends on where it runs.</summary>
    internal static readonly TimeZoneInfo Plus0530 =
        TimeZoneInfo.CreateCustomTimeZone("Test+05:30", TimeSpan.FromMinutes(330), "Test +05:30", "Test +05:30");

    /// <summary>London's rules: GMT in winter, BST (+01:00) from the last Sunday of March to the last of October.</summary>
    internal static readonly TimeZoneInfo LondonLike = TimeZoneInfo.CreateCustomTimeZone(
        "Test/London", TimeSpan.Zero, "Test London", "Test GMT", "Test BST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    // ------------------------------------------------------------------ EXIF

    [Fact]
    public void An_exif_time_with_an_offset_is_that_instant_whatever_the_zone()
    {
        var t = CaptureDates.FromExif("2023:07:14 09:15:00", null, "+02:00", Plus0530, Now);
        Assert.Equal(new DateTimeOffset(2023, 7, 14, 9, 15, 0, TimeSpan.FromHours(2)), t);
        Assert.Equal(new DateTime(2023, 7, 14, 7, 15, 0), t!.Value.UtcDateTime);
    }

    [Fact]
    public void An_exif_time_without_an_offset_is_read_in_the_scanning_machines_zone()
    {
        var t = CaptureDates.FromExif("2023:07:14 09:15:00", null, null, Plus0530, Now);
        Assert.Equal(new DateTimeOffset(2023, 7, 14, 9, 15, 0, TimeSpan.FromMinutes(330)), t);
        Assert.Equal(new DateTime(2023, 7, 14, 3, 45, 0), t!.Value.UtcDateTime);
    }

    [Fact]
    public void A_floating_time_takes_the_zones_rule_for_its_own_date()
    {
        var summer = CaptureDates.FromExif("2023:07:14 09:15:00", null, null, LondonLike, Now);
        var winter = CaptureDates.FromExif("2023:01:14 09:15:00", null, null, LondonLike, Now);
        Assert.Equal(TimeSpan.FromHours(1), summer!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, winter!.Value.Offset);

        // 01:30 on the last Sunday of March 2023 never happened in London; it
        // gets the standard offset rather than an exception.
        var gap = CaptureDates.FromExif("2023:03:26 01:30:00", null, null, LondonLike, Now);
        Assert.Equal(new DateTime(2023, 3, 26, 1, 30, 0), gap!.Value.UtcDateTime);
    }

    [Fact]
    public void Sub_seconds_and_a_trailing_offset_are_honoured()
    {
        var t = CaptureDates.FromExif("2023:07:14 09:15:00", "123", "+02:00", Plus0530, Now);
        Assert.Equal(123, t!.Value.Millisecond);

        var inline = CaptureDates.FromExif("2023:07:14 09:15:00.5-07:00", null, null, Plus0530, Now);
        Assert.Equal(new DateTimeOffset(2023, 7, 14, 9, 15, 0, 500, TimeSpan.FromHours(-7)), inline);
    }

    [Fact]
    public void A_blank_offset_is_no_offset()
    {
        var t = CaptureDates.FromExif("2023:07:14 09:15:00", null, "   :  ", Plus0530, Now);
        Assert.Equal(TimeSpan.FromMinutes(330), t!.Value.Offset);
    }

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("    :  :     :  :  ")]
    [InlineData("")]
    [InlineData("2023:13:14 09:15:00")]
    [InlineData("2023:02:30 09:15:00")]
    [InlineData("1850:01:01 00:00:00")]
    [InlineData("2031:01:01 00:00:00")]
    public void An_exif_date_that_cannot_be_a_capture_time_is_refused(string value) =>
        Assert.Null(CaptureDates.FromExif(value, null, null, Plus0530, Now));

    [Theory]
    [InlineData("2023-07-14T09:15:00+02:00", "2023-07-14T07:15:00Z")]
    [InlineData("2023-07-14T09:15:00Z", "2023-07-14T09:15:00Z")]
    [InlineData("2023-07-14T09:15:00", "2023-07-14T03:45:00Z")]
    [InlineData("2023-07-14", "2023-07-14T06:30:00Z")]
    public void Xmp_dates_with_and_without_an_offset(string value, string utc) =>
        Assert.Equal(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture),
                     CaptureDates.FromXmp(value, Plus0530, Now));

    // ------------------------------------------------------------- file names

    [Theory]
    [InlineData("IMG_20240101_123456.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("VID_20231231_235959.mp4", "2023-12-31 23:59:59.000")]
    [InlineData("PXL_20240315_081530123.jpg", "2024-03-15 08:15:30.123")]
    [InlineData("PXL_20240315_081530123.MP.jpg", "2024-03-15 08:15:30.123")]
    [InlineData("MVIMG_20220707_101010.jpg", "2022-07-07 10:10:10.000")]
    [InlineData("20240101_123456.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("20240101_123456(0).jpg", "2024-01-01 12:34:56.000")]
    [InlineData("IMG20240101123456.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("2024-01-01 12.34.56.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("2024-01-01 12_34_56.png", "2024-01-01 12:34:56.000")]
    [InlineData("2024-01-01_12-34-56.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("photo_2024-01-01_12-34-56.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("Screenshot_20240101-123456.png", "2024-01-01 12:34:56.000")]
    [InlineData("Screenshot_20240101_123456_Chrome.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("Screenshot_2024-01-01-12-34-56.png", "2024-01-01 12:34:56.000")]
    [InlineData("Screenshot_2024-01-01-12-34-56-123_com.android.chrome.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("Screenshot 2024-01-01 at 12.34.56.png", "2024-01-01 12:34:56.000")]
    [InlineData("Screen Shot 2020-01-01 at 1.23.45 PM.png", "2020-01-01 13:23:45.000")]
    [InlineData("Screenshot 2023-11-05 at 9.08.07 AM.png", "2023-11-05 09:08:07.000")]
    [InlineData("Screenshot 2023-11-05 at 12.08.07 AM.png", "2023-11-05 00:08:07.000")]
    [InlineData("Screenshot 2024-01-01 123456.png", "2024-01-01 12:34:56.000")]
    [InlineData("signal-2024-01-01-123456.jpg", "2024-01-01 12:34:56.000")]
    [InlineData("IMG-20240101-WA0001.jpg", "2024-01-01 12:00:00.000")]
    [InlineData("VID-20240101-WA0012.mp4", "2024-01-01 12:00:00.000")]
    [InlineData("2019-07-04 fireworks.jpg", "2019-07-04 12:00:00.000")]
    [InlineData("2024-01-01 12345.jpg", "2024-01-01 12:00:00.000")]   // a counter, not 01:23:45
    public void A_date_in_the_file_name_is_read_in_the_local_zone(string name, string wall)
    {
        var expected = new DateTimeOffset(
            DateTime.ParseExact(wall, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(330));
        Assert.Equal(expected, CaptureDates.FromFileName(name, Plus0530, Now));
    }

    [Theory]
    [InlineData("IMG_20240230_123456.jpg")]      // 30 February
    [InlineData("IMG_20241301_123456.jpg")]      // month 13
    [InlineData("IMG_20240101_256000.jpg")]      // hour 25
    [InlineData("IMG_20240101_126100.jpg")]      // minute 61
    [InlineData("Screen Shot 2020-01-01 at 13.23.45 PM.png")]   // 13 PM
    [InlineData("IMG_20990101_000000.jpg")]      // the future
    [InlineData("IMG_1234.JPG")]
    [InlineData("photo00012.jpg")]
    [InlineData("DSC_0001.NEF")]
    [InlineData("20240101_1234567.jpg")]         // seven time digits: not a time
    [InlineData("1704112496000.jpg")]            // a Unix time in ms: not read
    public void A_name_without_a_real_date_gives_none(string name) =>
        Assert.Null(CaptureDates.FromFileName(name, Plus0530, Now));

    [Fact]
    public void Only_the_file_name_counts_not_its_folder() =>
        Assert.Null(CaptureDates.FromFileName("2024-01-01 Trip/IMG_0001.jpg", Plus0530, Now));

    [Fact]
    public void Ranks_put_headers_over_names_over_file_times()
    {
        Assert.True(TakenSource.Rank(TakenSource.Exif) > TakenSource.Rank(TakenSource.FileName));
        Assert.True(TakenSource.Rank(TakenSource.FileName) > TakenSource.Rank(TakenSource.Mtime));
        Assert.True(TakenSource.Rank(TakenSource.Mtime) > TakenSource.Rank(null));
    }
}
