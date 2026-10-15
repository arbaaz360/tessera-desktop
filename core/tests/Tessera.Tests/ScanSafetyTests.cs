using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// A guard nobody can exercise is a comment. These are the known-positive and
/// known-negative the house rules ask for: the guard has to let a normal scan
/// through and stop the one that would empty a library.
/// </summary>
public class ScanSafetyTests
{
    [Fact]
    public void A_healthy_walk_may_mark_gone()
    {
        // Everything still there, plus a handful genuinely removed.
        Assert.True(ScanSafety.MayMarkGone(known: 479_046, seen: 479_046));
        Assert.True(ScanSafety.MayMarkGone(known: 479_046, seen: 478_900));
    }

    /// <summary>
    /// The case this exists for. Docker creates a missing bind-mount source,
    /// so a folder renamed on the host presents as a root that exists and is
    /// empty - which Directory.Exists cannot tell from a real empty library.
    /// </summary>
    [Fact]
    public void An_empty_root_may_not()
    {
        Assert.False(ScanSafety.MayMarkGone(known: 479_046, seen: 0));
    }

    [Fact]
    public void Losing_most_of_a_root_may_not()
    {
        Assert.False(ScanSafety.MayMarkGone(known: 100_000, seen: 40_000));
        Assert.False(ScanSafety.MayMarkGone(known: 100_000, seen: 49_999));
    }

    [Fact]
    public void Exactly_the_threshold_is_allowed()
    {
        Assert.True(ScanSafety.MayMarkGone(known: 100_000, seen: 50_000));
    }

    /// <summary>
    /// A small root swings wildly for ordinary reasons, so the ratio would
    /// block real deletions there and teach everyone to force past it.
    /// </summary>
    [Fact]
    public void A_small_root_is_not_second_guessed()
    {
        Assert.True(ScanSafety.MayMarkGone(known: 12, seen: 0));
        Assert.True(ScanSafety.MayMarkGone(known: ScanSafety.MinimumBaseline - 1, seen: 0));
    }

    [Fact]
    public void An_empty_database_is_not_a_loss()
    {
        Assert.True(ScanSafety.MayMarkGone(known: 0, seen: 0));
        Assert.True(ScanSafety.MayMarkGone(known: 0, seen: 5_000));
    }

    [Fact]
    public void A_growing_root_is_fine()
    {
        Assert.True(ScanSafety.MayMarkGone(known: 1_000, seen: 8_000));
    }

    [Fact]
    public void The_refusal_says_both_numbers()
    {
        var why = ScanSafety.Explain(known: 479_046, seen: 0);
        Assert.Contains("479,046", why, StringComparison.Ordinal);
        Assert.Contains("0", why, StringComparison.Ordinal);
    }
}
