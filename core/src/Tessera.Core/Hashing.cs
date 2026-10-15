using System.Buffers;
using System.Security.Cryptography;

namespace Tessera.Core;

/// <summary>
/// Content hashing - the operation the whole schema is keyed on.
/// </summary>
/// <remarks>
/// Deliberately NOT using any hash the source system already stored. Measured
/// 2026-09-05: two byte-identical files carried different checksums in Immich's
/// own column, which is why its per-owner uniqueness constraint never stopped
/// 225,749 duplicates from existing. A hash is only trustworthy if you computed
/// it from the bytes yourself.
/// </remarks>
public static class Hashing
{
    // Large enough that the syscall overhead disappears, small enough to stay
    // off the large object heap when pooled.
    private const int BufferSize = 1 << 20;

    public static async Task<byte[]> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                // The scanner reads each file exactly once, front to back, and
                // never comes back. Telling the OS that stops it populating the
                // cache with data nothing will ask for again.
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });

        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }

            sha.TransformFinalBlock([], 0, 0);
            return sha.Hash ?? throw new InvalidOperationException("SHA-256 produced no hash");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    /// <summary>Hex characters in a SHA-256.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// Read a SHA-256 written as hex, or say no. Never throws.
    /// </summary>
    /// <remarks>
    /// The one place that decides what counts as a hash on the way in: ids
    /// from the web app, refs from workers, lines in a manifest. Sixty-four
    /// hex digits, either case, nothing else - a value that merely LOOKS like
    /// a hash (right length, a stray character) is refused rather than turned
    /// into an exception somewhere far from here.
    /// </remarks>
    public static bool TryParseSha256(ReadOnlySpan<char> hex, out byte[] sha256)
    {
        sha256 = [];
        if (hex.Length != HexLength)
        {
            return false;
        }

        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        sha256 = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>
    /// Where a derived file for this content lives, e.g. a thumbnail.
    /// </summary>
    /// <remarks>
    /// DERIVED, never stored. A stored path is a second copy of a fact that can
    /// drift from the first - which is how 340,739 orphaned thumbnails
    /// accumulated in the system this replaces. Fanned two levels so no
    /// directory holds millions of entries.
    /// </remarks>
    public static string DerivedPath(string root, ReadOnlySpan<byte> sha256, string kind, string ext)
    {
        var hex = ToHex(sha256);
        return Path.Combine(root, hex[..2], hex[2..4], $"{hex}-{kind}.{ext}");
    }
}
