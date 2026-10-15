using Tessera.Api;

namespace Tessera.Tests;

/// <summary>
/// A token is a capability for one file, for a while, from this process.
/// Each test is one way it must NOT be turned into more than that.
/// </summary>
public sealed class MediaTokensTests
{
    [Fact]
    public void A_token_opens_exactly_the_content_it_was_issued_for()
    {
        var tokens = new MediaTokens();
        var t = tokens.Issue(42);
        Assert.True(tokens.IsValid(t, 42));
        Assert.False(tokens.IsValid(t, 43));
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var tokens = new MediaTokens();
        var t = tokens.Issue(42, TimeSpan.FromSeconds(-1));
        Assert.False(tokens.IsValid(t, 42));
    }

    [Fact]
    public void A_token_from_another_process_is_refused()
    {
        var t = new MediaTokens().Issue(42);
        Assert.False(new MediaTokens().IsValid(t, 42));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("42:9999999999")]
    [InlineData("42:9999999999:nothex")]
    [InlineData("42:9999999999:00")]
    public void A_malformed_token_is_refused_not_thrown(string? t)
    {
        Assert.False(new MediaTokens().IsValid(t, 42));
    }

    [Fact]
    public void Tampering_with_the_content_id_or_expiry_breaks_the_signature()
    {
        var tokens = new MediaTokens();
        var parts = tokens.Issue(42).Split(':');
        Assert.False(tokens.IsValid($"43:{parts[1]}:{parts[2]}", 43));
        Assert.False(tokens.IsValid($"42:{long.Parse(parts[1]) + 3600}:{parts[2]}", 42));
    }
}
