using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace Tessera.Desktop.Search.Tests;

/// <summary>What the export script recorded from the reference tokenizer and model.</summary>
internal sealed class ExpectedFixtures
{
    [JsonPropertyName("tokenizer")] public TokenizerFacts Tokenizer { get; init; } = new();
    [JsonPropertyName("images")] public List<ImageFixture> Images { get; init; } = new();
    [JsonPropertyName("texts")] public List<TextFixture> Texts { get; init; } = new();

    public static ExpectedFixtures Load()
        => JsonSerializer.Deserialize<ExpectedFixtures>(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "expected.json")))!;
}

internal sealed class ImageFixture
{
    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("embedding")] public required float[] Embedding { get; init; }
}

internal sealed class TextFixture
{
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("inputIds")] public required long[] InputIds { get; init; }
    [JsonPropertyName("embedding")] public required float[] Embedding { get; init; }
}

public class TokenizerTests
{
    private readonly ITestOutputHelper _out;

    public TokenizerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void FixtureFactsMatchTheEmbeddedManifest()
    {
        var expected = ExpectedFixtures.Load();
        var shipped = ModelManifest.Embedded().Tokenizer;
        Assert.Equal(expected.Tokenizer.AddBos, shipped.AddBos);
        Assert.Equal(expected.Tokenizer.AddEos, shipped.AddEos);
        Assert.Equal(expected.Tokenizer.PadId, shipped.PadId);
        Assert.Equal(expected.Tokenizer.EosId, shipped.EosId);
        Assert.Equal(expected.Tokenizer.MaxLength, shipped.MaxLength);
    }

    [Fact]
    public void InputIdsMatchTheReferenceTokenizer()
    {
        if (TestPaths.ModelDir is null)
        {
            _out.WriteLine("skipped: no local model export (models/out or TESSERA_MODELS_DIR)");
            return;
        }
        var expected = ExpectedFixtures.Load();
        var tokenizer = GemmaTextTokenizer.FromFile(Path.Combine(TestPaths.ModelDir, "tokenizer.model"), expected.Tokenizer);

        var mismatches = new List<string>();
        foreach (var t in expected.Texts)
        {
            long[] ids = tokenizer.Encode(t.Text);
            Assert.Equal(64, ids.Length);
            if (!ids.SequenceEqual(t.InputIds))
                mismatches.Add($"'{t.Text}'\n  ours: {string.Join(",", ids.TakeWhile(x => x != 0))}\n  ref:  {string.Join(",", t.InputIds.TakeWhile(x => x != 0))}");
        }
        foreach (var m in mismatches) _out.WriteLine(m);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {expected.Texts.Count} sentences tokenised differently");
        _out.WriteLine($"{expected.Texts.Count} sentences tokenised identically to the reference (including unicode and a 200-token truncation)");
    }

    [Fact]
    public void TruncationKeepsTheEos()
    {
        if (TestPaths.ModelDir is null) { _out.WriteLine("skipped: no local model export"); return; }
        var facts = ModelManifest.Embedded().Tokenizer;
        var tokenizer = GemmaTextTokenizer.FromFile(Path.Combine(TestPaths.ModelDir, "tokenizer.model"), facts);
        long[] ids = tokenizer.Encode(string.Join(" ", Enumerable.Repeat("word", 500)));
        Assert.Equal(64, ids.Length);
        Assert.Equal(facts.EosId, ids[63]);
        Assert.DoesNotContain(facts.PadId, ids);
    }
}
