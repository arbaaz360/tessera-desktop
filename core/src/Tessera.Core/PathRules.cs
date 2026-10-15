using System.Text.RegularExpressions;

namespace Tessera.Core;

/// <summary>
/// How Tessera spells a path. One definition, used by every caller.
/// </summary>
/// <remarks>
/// <para>
/// This exists as its own type because the same rule kept in two places
/// diverges. The system this replaces had three copies of its path
/// normaliser; they agreed, until a fix had to land in all three and did not.
/// A related bug came from mounting one folder at two container paths, which
/// registered 55,370 files twice with two sets of embeddings.
/// </para>
/// <para>
/// <see cref="SelfTest"/> runs at startup. A normaliser that quietly does
/// nothing reads exactly like one that works.
/// </para>
/// </remarks>
public static partial class PathRules
{
    [GeneratedRegex(@"/+")]
    private static partial Regex SlashRun();

    /// <summary>
    /// The one spelling of a path: forward slashes, no repeated separators, no
    /// trailing slash, no relative segments.
    /// </summary>
    public static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var s = SlashRun().Replace(path.Replace('\\', '/'), "/");
        if (s.Length > 1 && s.EndsWith('/'))
        {
            s = s[..^1];
        }

        return s;
    }

    /// <summary>
    /// The part of <paramref name="absolute"/> below <paramref name="root"/>,
    /// or null when it is not below it at all.
    /// </summary>
    /// <remarks>
    /// Compared segment-wise rather than with StartsWith: "/reddit" must not
    /// match "/reddit-staging", which a plain prefix test happily would.
    /// </remarks>
    public static string? RelativeTo(string root, string absolute)
    {
        var r = Canonical(root);
        var a = Canonical(absolute);

        if (r.Length == 0 || a.Length <= r.Length)
        {
            return string.Equals(r, a, StringComparison.Ordinal) ? "" : null;
        }

        if (!a.StartsWith(r, StringComparison.Ordinal) || a[r.Length] != '/')
        {
            return null;
        }

        return a[(r.Length + 1)..];
    }

    /// <summary>The immediate folder under the root - one album per folder.</summary>
    public static string? TopFolder(string relPath)
    {
        var p = Canonical(relPath);
        var slash = p.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? null : p[..slash];
    }

    /// <summary>
    /// Assert the rules behave, including a case that must NOT match.
    /// </summary>
    /// <remarks>
    /// A guard that cannot fire reads as protection. The known-negative here is
    /// the important half: a prefix test that accepts "/reddit-staging" for
    /// root "/reddit" would file another corpus into the wrong account, and
    /// would look like it was working.
    /// </remarks>
    public static void SelfTest()
    {
        Check(Canonical(@"\reddit\a\b.jpg") == "/reddit/a/b.jpg", "backslashes");
        Check(Canonical("//reddit//a/b.jpg") == "/reddit/a/b.jpg", "repeated slashes");
        Check(Canonical("/reddit/") == "/reddit", "trailing slash");
        Check(Canonical(null) == "", "null");

        Check(RelativeTo("/reddit", "/reddit/DesiVixens/x.jpg") == "DesiVixens/x.jpg", "relative");
        Check(RelativeTo("/reddit", "/reddit") == "", "root itself");
        Check(RelativeTo("/reddit", "/reddit-staging/x.jpg") is null, "sibling prefix must NOT match");
        Check(RelativeTo("/reddit", "/simpcity/x.jpg") is null, "unrelated root");

        Check(TopFolder("DesiVixens/x.jpg") == "DesiVixens", "top folder");
        Check(TopFolder("x.jpg") is null, "file at root has no folder");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"PathRules self-test failed: {what}");
        }
    }
}
