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

    public static Task<byte[]> Sha256FileAsync(string path, CancellationToken ct = default) =>
        Sha256FileAsync(path, peek: null, ct);

    /// <summary>The bytes <see cref="Sha256FileAsync(string, Action{ReadOnlyMemory{byte}, Stream}?, CancellationToken)"/> reads first, and offers to its peek.</summary>
    public const int FirstChunk = BufferSize;

    /// <param name="peek">
    /// Called once, with the first <see cref="FirstChunk"/> bytes (fewer for
    /// a smaller file) and the open file, before the rest is read. It lets a
    /// caller read a file's headers from bytes already in memory, without
    /// opening the file a second time - on a network share or a USB disk an
    /// open is a round trip. It may seek the stream; the hash never depends
    /// on what it does.
    /// </param>
    public static async Task<byte[]> Sha256FileAsync(string path, Action<ReadOnlyMemory<byte>, Stream>? peek, CancellationToken ct = default)
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
            if (peek is not null)
            {
                // A whole first chunk, not whatever one read returns: the
                // peek is promised the front of the file.
                var first = await stream.ReadAtLeastAsync(buffer.AsMemory(0, BufferSize), BufferSize, throwOnEndOfStream: false, ct);
                sha.TransformBlock(buffer, 0, first, null, 0);
                peek(buffer.AsMemory(0, first), stream);
                stream.Position = first;
            }

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
