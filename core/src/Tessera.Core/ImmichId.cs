using System.Globalization;

namespace Tessera.Core;

/// <summary>
/// Tessera identifies content with a bigint; the Immich API promises a uuid.
/// This maps between them without a lookup table.
/// </summary>
/// <remarks>
/// <para>
/// A stored mapping would be a second copy of a fact that already exists - the
/// thing this whole schema is built to avoid. So the id is ENCODED rather than
/// looked up: a fixed namespace with the content id in the low 48 bits. The
/// shape satisfies anything parsing a uuid, and it decodes back exactly.
/// </para>
/// <para>
/// 48 bits is 281 trillion. The library is two million.
/// </para>
/// </remarks>
public static class ImmichId
{
    private const string Prefix = "00000000-0000-4000-8000-";

    /// <summary>Largest content id this encoding can carry.</summary>
    public const long MaxId = 0xFFFF_FFFF_FFFFL;

    public static string From(long contentId)
    {
        if (contentId is < 0 or > MaxId)
        {
            throw new ArgumentOutOfRangeException(nameof(contentId), contentId, "outside the encodable range");
        }

        return Prefix + contentId.ToString("x12", CultureInfo.InvariantCulture);
    }

    /// <summary>Decode an id this class produced. False for anything else.</summary>
    public static bool TryParse(string? uuid, out long contentId)
    {
        contentId = 0;
        if (uuid is null || uuid.Length != 36 || !uuid.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(
            uuid.AsSpan(Prefix.Length),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out contentId);
    }

    /// <summary>The same trick for accounts, in a different namespace.</summary>
    public static string Account(long accountId) => "00000000-0000-4000-9000-" + accountId.ToString("x12", CultureInfo.InvariantCulture);

    public static bool TryParseAccount(string? uuid, out long accountId)
    {
        accountId = 0;
        if (uuid is null || uuid.Length != 36 ||
            !uuid.StartsWith("00000000-0000-4000-9000-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(
            uuid.AsSpan(24),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out accountId);
    }

    /// <summary>And for albums.</summary>
    public static string Album(long albumId) => "00000000-0000-4000-a000-" + albumId.ToString("x12", CultureInfo.InvariantCulture);

    public static bool TryParseAlbum(string? uuid, out long albumId)
    {
        albumId = 0;
        if (uuid is null || uuid.Length != 36 ||
            !uuid.StartsWith("00000000-0000-4000-a000-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(
            uuid.AsSpan(24),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out albumId);
    }
}
