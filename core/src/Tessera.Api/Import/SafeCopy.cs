using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using Tessera.Core;

namespace Tessera.Api.Import;

/// <summary>Bytes written to a temporary name in the destination folder, read back and verified.</summary>
/// <param name="TempPath">The partial file. Never a media extension, so no scan picks it up.</param>
public sealed record Staged(string TempPath, string Folder, byte[] Sha256, long Length);

/// <summary>Where staged bytes ended up.</summary>
/// <param name="Path">The final file.</param>
/// <param name="Existing">True when a file of that name already held these bytes, so nothing was written.</param>
public sealed record Committed(string Path, bool Existing);

/// <summary>
/// The one way the app writes a picture: into its own folder, copied, never
/// moved, verified before it gets a real name.
/// </summary>
/// <remarks>
/// <para>
/// THREE STEPS, AND ONLY THE LAST ONE IS VISIBLE. The bytes go to a temporary
/// name beside the destination (<see cref="PartialSuffix"/>), are flushed to
/// the disk, read back and hashed, and only then renamed to the real name. A
/// copy that fails at any point - a source that shrinks or changes while it
/// is read, a full disk, a crash - leaves no file with a picture's name: the
/// temporary one is deleted on the way out, and one left by a crash ends in a
/// suffix no scan enumerates.
/// </para>
/// <para>
/// The source is opened for reading and nothing else: never moved, never
/// deleted, never written.
/// </para>
/// <para>
/// A name already taken by other bytes gets a number, as the file manager
/// does: "beach.jpg", then "beach (2).jpg". A name taken by the same bytes is
/// the same picture and is used as it is.
/// </para>
/// </remarks>
public static class SafeCopy
{
    /// <summary>The suffix of a copy in progress.</summary>
    public const string PartialSuffix = ".tessera-partial";

    private const int BufferSize = 1 << 20;

    /// <summary>Copy a file into a folder: stage, check against the hash read beforehand, commit.</summary>
    /// <param name="expectedSha256">The source's hash, read before the copy; a mismatch means the file changed meanwhile.</param>
    public static async Task<Committed> CopyFileAsync(
        string sourcePath, string folder, string fileName, byte[]? expectedSha256, CancellationToken ct,
        Func<Stream, Stream>? wrapSource = null)
    {
        await using var source = OpenRead(sourcePath);
        var staged = await StageAsync(wrapSource is null ? source : wrapSource(source), folder, ct);
        try
        {
            if (expectedSha256 is not null && !expectedSha256.AsSpan().SequenceEqual(staged.Sha256))
            {
                throw new IOException("The file changed while it was being copied; nothing was kept.");
            }

            return Commit(staged, fileName);
        }
        catch
        {
            Discard(staged);
            throw;
        }
    }

    /// <summary>A file opened the only way the app opens someone else's file: to read it.</summary>
    public static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0,
    });

    /// <summary>
    /// Write a stream to a temporary name in <paramref name="folder"/>, flush
    /// it to the disk, read it back, and check the two hashes agree.
    /// </summary>
    /// <param name="maxBytes">Refuse a stream longer than this (a download that never ends).</param>
    public static async Task<Staged> StageAsync(Stream source, string folder, CancellationToken ct, long maxBytes = long.MaxValue)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N")[..12] + PartialSuffix);
        try
        {
            byte[] written;
            long length = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var target = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                    BufferSize = 0,
                }))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                        {
                            length += read;
                            if (length > maxBytes)
                            {
                                throw new IOException(string.Create(CultureInfo.InvariantCulture,
                                    $"Larger than {maxBytes / (1024 * 1024)} MB; nothing was kept."));
                            }

                            sha.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    // On the disk, not in a cache, before it is read back.
                    await target.FlushAsync(ct);
                    target.Flush(flushToDisk: true);
                }

                written = sha.GetHashAndReset();
            }

            if (length == 0)
            {
                throw new IOException("The file is empty; nothing was kept.");
            }

            var onDisk = await Hashing.Sha256FileAsync(temp, ct);
            if (!onDisk.AsSpan().SequenceEqual(written))
            {
                throw new IOException("The copy did not read back the same as the original; nothing was kept.");
            }

            return new Staged(temp, folder, written, length);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Give staged bytes their real name: the wanted one if free, the same
    /// bytes under that name if they are already there, else a numbered one.
    /// </summary>
    public static Committed Commit(Staged staged, string fileName)
    {
        var name = SafeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(staged.Folder, n == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){ext}"));
            if (File.Exists(candidate))
            {
                if (SameBytes(candidate, staged))
                {
                    Discard(staged);
                    return new Committed(candidate, Existing: true);
                }

                continue;
            }

            try
            {
                // No overwrite: if another writer took the name between the
                // check and here, this throws and the next number is tried.
                File.Move(staged.TempPath, candidate, overwrite: false);
                return new Committed(candidate, Existing: false);
            }
            catch (IOException) when (File.Exists(candidate) && File.Exists(staged.TempPath))
            {
            }
        }

        Discard(staged);
        throw new IOException($"No free name for {name} in {staged.Folder}.");
    }

    /// <summary>Drop staged bytes the caller decided not to keep (they were already in the library).</summary>
    public static void Discard(Staged staged) => TryDelete(staged.TempPath);

    /// <summary>
    /// A name that is a valid file name on Windows and macOS: no separators,
    /// no characters Windows refuses, no trailing dot or space, not a device
    /// name. Empty becomes "picture".
    /// </summary>
    public static string SafeFileName(string? raw) => Safe(Path.GetFileName(raw ?? ""), "picture");

    /// <summary>The folder an album's copies go to: its name made safe, or "Unsorted".</summary>
    public static string SafeFolderName(string? albumName) => Safe(albumName, "Unsorted");

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string Safe(string? raw, string fallback)
    {
        var chars = (raw ?? "").Select(c => c < 32 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length > 120)
        {
            var ext = Path.GetExtension(s);
            ext = ext.Length > 20 ? "" : ext;
            s = s[..(120 - ext.Length)].TrimEnd('.', ' ') + ext;
        }

        if (s.Length == 0 || s.Trim('.').Length == 0)
        {
            return fallback;
        }

        return DeviceNames.Contains(Path.GetFileNameWithoutExtension(s)) ? "_" + s : s;
    }

    private static bool SameBytes(string path, Staged staged)
    {
        var info = new FileInfo(path);
        if (info.Length != staged.Length)
        {
            return false;
        }

        var sha = Hashing.Sha256FileAsync(path).GetAwaiter().GetResult();
        return sha.AsSpan().SequenceEqual(staged.Sha256);
    }

    private static void TryDelete(string path)
    {
        try
        {
            // Only ever the app's own temporary file, never a picture.
            if (path.EndsWith(PartialSuffix, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
