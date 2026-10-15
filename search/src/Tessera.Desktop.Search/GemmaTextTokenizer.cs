using Microsoft.ML.Tokenizers;

namespace Tessera.Desktop.Search;

/// <summary>
/// Builds the text tower's input ids the way the SigLIP2 processor does:
/// SentencePiece pieces, no BOS, an EOS, truncated so the whole row fits
/// <see cref="TokenizerFacts.MaxLength"/>, padded on the right with the pad id.
/// </summary>
public sealed class GemmaTextTokenizer
{
    private readonly SentencePieceTokenizer _sp;
    private readonly TokenizerFacts _facts;

    public GemmaTextTokenizer(Stream tokenizerModel, TokenizerFacts facts)
    {
        ArgumentNullException.ThrowIfNull(tokenizerModel);
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        // Special tokens are added here, not by the library, so truncation
        // can keep the EOS the way the reference tokenizer does.
        _sp = SentencePieceTokenizer.Create(tokenizerModel, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    public static GemmaTextTokenizer FromFile(string path, TokenizerFacts facts)
    {
        using FileStream fs = File.OpenRead(path);
        return new GemmaTextTokenizer(fs, facts);
    }

    public int MaxLength => _facts.MaxLength;

    /// <summary>Piece ids without special tokens, truncation or padding.</summary>
    public IReadOnlyList<int> EncodePieces(string text)
    {
        if (_facts.Lowercase) text = text.ToLowerInvariant();
        return _sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    /// <summary>One padded row of <see cref="MaxLength"/> ids.</summary>
    public long[] Encode(string text)
    {
        var row = new long[_facts.MaxLength];
        Encode(text, row);
        return row;
    }

    public void Encode(string text, Span<long> row)
    {
        if (row.Length != _facts.MaxLength)
            throw new ArgumentException($"Row must hold {_facts.MaxLength} ids.", nameof(row));
        IReadOnlyList<int> pieces = EncodePieces(text);
        int specials = (_facts.AddBos ? 1 : 0) + (_facts.AddEos ? 1 : 0);
        int keep = Math.Min(pieces.Count, _facts.MaxLength - specials);
        int i = 0;
        if (_facts.AddBos) row[i++] = _facts.BosId;
        for (int p = 0; p < keep; p++) row[i++] = pieces[p];
        if (_facts.AddEos) row[i++] = _facts.EosId;
        row[i..].Fill(_facts.PadId);
    }
}
