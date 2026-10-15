using System.Globalization;

namespace Tessera.Api;

/// <summary>The few shapes every endpoint shares.</summary>
public static class Api
{
    /// <summary>An error: the status, and <c>{"detail": "…"}</c>. CONTRACT §4.</summary>
    public static IResult Fail(int status, string detail) =>
        Results.Json(new { detail }, statusCode: status);

    public static IResult NotFound(string detail = "Not found") => Fail(StatusCodes.Status404NotFound, detail);

    /// <summary>ISO 8601 UTC with milliseconds, the one time format on the wire.</summary>
    public static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string? Iso(DateTimeOffset? t) => t is { } v ? Iso(v) : null;

    /// <summary>
    /// Ids on the wire are the database's own integers, written as decimal
    /// strings. The web app treats them as opaque; nothing is encoded.
    /// </summary>
    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseId(string? s, out long id) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;

    public static List<long> ParseIds(IEnumerable<string>? raw)
    {
        var ids = new List<long>();
        if (raw is null)
        {
            return ids;
        }

        foreach (var r in raw)
        {
            if (TryParseId(r, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
