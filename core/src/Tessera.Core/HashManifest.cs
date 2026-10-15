namespace Tessera.Core;

/// <summary>
/// The line format for handing precomputed hashes between tools:
/// <c>&lt;path&gt;TAB&lt;64 hex characters&gt;</c>, one per line.
/// </summary>
/// <remarks>
/// Deliberately the dumbest format that works. Anything that can hash a file
/// can write it, and a consumer needs no knowledge of the producer - which is
/// the whole point, because the alternative is one service reading another
/// service's tables and inheriting a schema it does not own.
/// </remarks>
public static class HashManifest
{
    /// <summary>
    /// Read one line. Returns false for anything that is not exactly the
    /// format, rather than throwing.
    /// </summary>
    /// <remarks>
    /// A malformed line in a million-line file must cost that line and nothing
    /// else. Parsing hex by exception would let one stray character - a CSV
    /// exported where a TSV was meant, a truncated write, a header row - abort
    /// a migration that has been running for hours.
    /// </remarks>
    public static bool TryParseLine(string? line, out string path, out byte[] sha256)
    {
        path = string.Empty;
        sha256 = [];

        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var tab = line.IndexOf('\t');

        // A path before the tab, and the hex must run to the end of the line:
        // trailing anything means this is not the format we think it is.
        if (tab <= 0 || !Hashing.TryParseSha256(line.AsSpan(tab + 1), out sha256))
        {
            return false;
        }

        path = line[..tab];
        return true;
    }
}
