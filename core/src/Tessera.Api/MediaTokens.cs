using System.Security.Cryptography;
using System.Text;

namespace Tessera.Api;

/// <summary>
/// Short-lived, single-purpose URLs that let a worker read one file.
/// </summary>
/// <remarks>
/// <para>
/// A worker behind a contract (docs/contracts) has no account and no session:
/// it is handed what it needs in the request. For a video that means a URL
/// to the bytes. The URL carries a token that says "this content, until this
/// time, signed by Tessera" - a capability, not a credential. The worker
/// cannot turn it into anything else, it dies in minutes, and nothing has to
/// exist in the account table for the worker to work.
/// </para>
/// <para>
/// The signing key is random at startup and lives nowhere. A token therefore
/// dies with the process, which is exactly the lifetime a worker's request
/// needs; a job caught by a restart is simply re-leased and gets a new URL.
/// </para>
/// </remarks>
public sealed class MediaTokens
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public string Issue(long contentId, TimeSpan? lifetime = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime ?? Lifetime).ToUnixTimeSeconds();
        var payload = $"{contentId}:{expires}";
        var mac = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));
        return $"{payload}:{Convert.ToHexStringLower(mac)}";
    }

    /// <summary>True only for a token this process issued, for this content, still in date.</summary>
    public bool IsValid(string? token, long contentId)
    {
        if (token is null)
        {
            return false;
        }

        var parts = token.Split(':');
        if (parts.Length != 3 ||
            !long.TryParse(parts[0], out var id) || id != contentId ||
            !long.TryParse(parts[1], out var expires) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}:{parts[1]}"));
        byte[] given;
        try
        {
            given = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
