using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The path rules, tested hardest where they have historically been wrong.
/// </summary>
/// <remarks>
/// Every case here corresponds to a real incident on this machine: one folder
/// mounted under two names registered 55,370 files twice; a prefix test that
/// accepted a sibling directory filed a corpus into the wrong account; a
/// normaliser that silently did nothing looked exactly like one that worked.
/// </remarks>
public class PathRulesTests
{
    [Theory]
    [InlineData(@"\library\a\b.jpg", "/library/a/b.jpg")]
    [InlineData("//library//a/b.jpg", "/library/a/b.jpg")]
    [InlineData("/library/a//b.jpg", "/library/a/b.jpg")]
    [InlineData("/library/", "/library")]
    [InlineData("/", "/")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Canonical_collapses_every_spelling_to_one(string? input, string expected) =>
        Assert.Equal(expected, PathRules.Canonical(input));

    [Fact]
    public void Canonical_is_idempotent()
    {
        // If canonicalising twice differed from once, two callers at different
        // points in a pipeline would disagree about the same file.
        const string messy = @"\\library\\a//b\c.jpg";
        var once = PathRules.Canonical(messy);
        Assert.Equal(once, PathRules.Canonical(once));
    }

    [Theory]
    [InlineData("/photos", "/photos/Travel/x.jpg", "Travel/x.jpg")]
    [InlineData("/photos", "/photos/x.jpg", "x.jpg")]
    [InlineData("/photos", "/photos", "")]
    public void RelativeTo_strips_the_root(string root, string abs, string expected) =>
        Assert.Equal(expected, PathRules.RelativeTo(root, abs));

    [Theory]
    [InlineData("/photos", "/photos-staging/x.jpg")]
    [InlineData("/photos", "/photoss/x.jpg")]
    [InlineData("/photos", "/archive/x.jpg")]
    [InlineData("/photos", "/x.jpg")]
    public void RelativeTo_refuses_a_root_that_merely_shares_a_prefix(string root, string abs)
    {
        // THE IMPORTANT CASE. A StartsWith test passes all of these, and the
        // consequence is a whole corpus filed under the wrong account while
        // looking entirely healthy.
        Assert.Null(PathRules.RelativeTo(root, abs));
    }

    [Fact]
    public void RelativeTo_matches_regardless_of_separator()
    {
        Assert.Equal("a/b.jpg", PathRules.RelativeTo("/photos", @"\photos\a\b.jpg"));
        Assert.Equal("a/b.jpg", PathRules.RelativeTo(@"\photos", "//photos//a/b.jpg"));
    }

    [Theory]
    [InlineData("Travel/x.jpg", "Travel")]
    [InlineData("a/b/c.jpg", "a")]
    [InlineData("x.jpg", null)]
    [InlineData("", null)]
    public void TopFolder_is_the_album(string rel, string? expected) =>
        Assert.Equal(expected, PathRules.TopFolder(rel));

    [Fact]
    public void SelfTest_passes()
    {
        // The same assertions the service runs at startup. If this test and the
        // startup check ever disagree, one of them is lying.
        var ex = Record.Exception(PathRules.SelfTest);
        Assert.Null(ex);
    }

    [Fact]
    public void SelfTest_would_actually_fail_if_the_rules_broke()
    {
        // A guard that cannot fire reads as protection. This proves the guard
        // has teeth by asserting the property it depends on: a sibling prefix
        // must not resolve. If someone "simplifies" RelativeTo to StartsWith,
        // this fails loudly instead of the system quietly misfiling.
        Assert.NotNull(PathRules.RelativeTo("/photos", "/photos/a"));
        Assert.Null(PathRules.RelativeTo("/photos", "/photos2/a"));
    }
}
