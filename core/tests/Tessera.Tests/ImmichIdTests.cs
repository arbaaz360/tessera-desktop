using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The id encoding is the seam between Tessera's identity and the API contract
/// the web app speaks. If it is not exactly reversible, a photo opens the wrong
/// photo - so these test the round trip, the boundaries, and that the three
/// namespaces cannot be mistaken for one another.
/// </summary>
public class ImmichIdTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(908405L)]
    [InlineData(1_979_437L)]
    [InlineData(ImmichId.MaxId)]
    [InlineData(0L)]
    public void Round_trips_exactly(long id)
    {
        Assert.True(ImmichId.TryParse(ImmichId.From(id), out var back));
        Assert.Equal(id, back);
    }

    [Fact]
    public void Looks_like_a_uuid()
    {
        var s = ImmichId.From(908405);
        Assert.Equal(36, s.Length);
        Assert.True(Guid.TryParse(s, out _));
    }

    [Fact]
    public void Refuses_ids_it_cannot_encode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImmichId.From(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImmichId.From(ImmichId.MaxId + 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-4000-8000-00000000000")]    // one short
    [InlineData("00000000-0000-4000-8000-0000000000000")]  // one long
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479")]   // a real uuid
    public void Rejects_anything_it_did_not_make(string? s)
    {
        Assert.False(ImmichId.TryParse(s, out _));
    }

    /// <summary>
    /// Three namespaces, and an id from one must never decode as another -
    /// otherwise album 12 and photo 12 are the same string and the wrong row
    /// comes back with no error anywhere.
    /// </summary>
    [Fact]
    public void The_namespaces_do_not_collide()
    {
        const long n = 12345;
        var content = ImmichId.From(n);
        var account = ImmichId.Account(n);
        var album = ImmichId.Album(n);

        Assert.NotEqual(content, account);
        Assert.NotEqual(content, album);
        Assert.NotEqual(account, album);

        Assert.False(ImmichId.TryParse(account, out _));
        Assert.False(ImmichId.TryParse(album, out _));
        Assert.False(ImmichId.TryParseAlbum(content, out _));
        Assert.False(ImmichId.TryParseAccount(album, out _));

        Assert.True(ImmichId.TryParseAccount(account, out var a) && a == n);
        Assert.True(ImmichId.TryParseAlbum(album, out var b) && b == n);
    }

    [Fact]
    public void Is_case_insensitive_about_the_prefix()
    {
        Assert.True(ImmichId.TryParse(ImmichId.From(255).ToUpperInvariant(), out var id));
        Assert.Equal(255, id);
    }
}
