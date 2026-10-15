namespace Tessera.Core;

/// <summary>
/// The decision a scanner must get right: whether a walk that found fewer
/// files than the database expects is a real deletion or a broken mount.
/// </summary>
/// <remarks>
/// <para>
/// Checking that the root directory exists is not enough, and the reason is
/// specific: Docker CREATES a bind-mount source that is missing. Rename the
/// folder on the host and the container still sees the path - present, and
/// empty. The existence check passes, the walk finds nothing, and every file
/// under that root gets marked gone.
/// </para>
/// <para>
/// So the guard cannot be about the directory. It has to be about the SHAPE of
/// the result: a walk that loses most of a library is reporting an accident,
/// because on this machine originals are never deleted in bulk. Pure and
/// separately testable precisely because a guard nobody can exercise is not a
/// guard - it is a comment.
/// </para>
/// </remarks>
public static class ScanSafety
{
    /// <summary>Below this many known files, a big swing means nothing.</summary>
    public const int MinimumBaseline = 100;

    /// <summary>Fraction of the known files a walk must still find.</summary>
    public const double RequiredFraction = 0.5;

    /// <summary>
    /// True when the walk's result is trustworthy enough to delete rows over.
    /// </summary>
    /// <param name="known">Files the database has under this root.</param>
    /// <param name="seen">Files the walk actually found.</param>
    public static bool MayMarkGone(long known, long seen)
    {
        if (known < MinimumBaseline)
        {
            return true;
        }

        return seen >= known * RequiredFraction;
    }

    /// <summary>Why a scan declined to mark anything gone.</summary>
    public static string Explain(long known, long seen) =>
        $"Walk found {seen:N0} files where the database has {known:N0}. "
        + $"Refusing to mark anything gone: losing more than "
        + $"{(1 - RequiredFraction) * 100:N0}% of a root in one scan is a broken "
        + "mount far more often than a real deletion. Re-run once the root is "
        + "verified, or force it deliberately.";
}
