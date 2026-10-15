using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The manifest is how one tool's hashes reach another, and a hash is identity
/// here. Every case below is a way the file can be wrong that must cost one
/// line rather than the run.
/// </summary>
public class HashManifestTests
{
    private const string Sha =
        "9cdd6ed36ec6d613a7feabcdb973e3e9e52fd59546b66f5ac39642a709ad973d";

    [Fact]
    public void Reads_a_well_formed_line()
    {
        Assert.True(HashManifest.TryParseLine($"/reddit/sub/a.jpg\t{Sha}", out var p, out var sha));
        Assert.Equal("/reddit/sub/a.jpg", p);
        Assert.Equal(Convert.FromHexString(Sha), sha);
    }

    [Fact]
    public void Keeps_spaces_in_a_path()
    {
        Assert.True(HashManifest.TryParseLine($"/library/a b/c d.jpg\t{Sha}", out var p, out _));
        Assert.Equal("/library/a b/c d.jpg", p);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no tab at all")]
    [InlineData("\t" + Sha)]                          // empty path
    [InlineData("/a.jpg\t" + Sha + "extra")]          // trailing junk
    [InlineData("/a.jpg\t" + "9cdd6ed3")]             // truncated hash
    [InlineData("/a.jpg,9cdd6ed3")]                   // a CSV where a TSV was meant
    public void Rejects_anything_that_is_not_the_format(string? line)
    {
        Assert.False(HashManifest.TryParseLine(line, out _, out _));
    }

    /// <summary>
    /// The case that would otherwise throw. Sixty-four characters, so a length
    /// check passes it, but they are not hex - and parsing hex by exception
    /// would take down a migration that had been running for hours.
    /// </summary>
    [Fact]
    public void Rejects_the_right_length_of_the_wrong_alphabet()
    {
        var notHex = new string('z', Hashing.HexLength);
        Assert.False(HashManifest.TryParseLine($"/a.jpg\t{notHex}", out _, out _));
    }

    [Fact]
    public void Rejects_a_hash_one_character_short_or_long()
    {
        Assert.False(HashManifest.TryParseLine($"/a.jpg\t{Sha[..63]}", out _, out _));
        Assert.False(HashManifest.TryParseLine($"/a.jpg\t{Sha}0", out _, out _));
    }

    [Fact]
    public void Accepts_either_case_of_hex()
    {
        Assert.True(HashManifest.TryParseLine($"/a.jpg\t{Sha.ToUpperInvariant()}", out _, out var u));
        Assert.True(HashManifest.TryParseLine($"/a.jpg\t{Sha}", out _, out var l));
        Assert.Equal(l, u);
    }
}
