using Tessera.Core;

namespace Tessera.Tests;

/// <summary>
/// The one decision about what counts as a hash on the way in. Every id from
/// the web app, every ref from a worker and every manifest line goes through
/// it, so a value that merely looks like a hash must be refused here, once.
/// </summary>
public sealed class HashingTests
{
    private const string Hex = "a3e6502b3d8e0aca16530e52d68989f2bca178bcb0c334085f1de1fb89bcbb18";

    [Fact]
    public void Reads_either_case_and_round_trips()
    {
        Assert.True(Hashing.TryParseSha256(Hex, out var lower));
        Assert.True(Hashing.TryParseSha256(Hex.ToUpperInvariant(), out var upper));
        Assert.Equal(lower, upper);
        Assert.Equal(Hex, Hashing.ToHex(lower));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a3e6502b")]                                                          // too short
    [InlineData("a3e6502b3d8e0aca16530e52d68989f2bca178bcb0c334085f1de1fb89bcbb1")]  // 63
    [InlineData("a3e6502b3d8e0aca16530e52d68989f2bca178bcb0c334085f1de1fb89bcbb180")] // 65
    [InlineData("g3e6502b3d8e0aca16530e52d68989f2bca178bcb0c334085f1de1fb89bcbb18")]  // not hex
    [InlineData("00000000-0000-4000-8000-000000000001")]                              // an issued id
    public void Refuses_anything_that_is_not_exactly_a_hash(string input)
    {
        Assert.False(Hashing.TryParseSha256(input, out var sha));
        Assert.Empty(sha);
    }
}
