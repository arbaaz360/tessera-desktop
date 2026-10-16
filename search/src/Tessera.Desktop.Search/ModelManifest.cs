using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tessera.Desktop.Search;

/// <summary>
/// What the export wrote and the release carries: every file's name, byte
/// size, sha256 and URL, plus the facts the consumer needs to drive the
/// model (dimensions, image size, text length, tokenizer settings).
/// </summary>
/// <remarks>
/// From manifest version 4 a file may be listed once per platform (its
/// <see cref="ModelFile.Platforms"/>): Windows keeps v2's vision tower,
/// macOS takes v3's, whose float32 arithmetic Core ML needs. A machine
/// downloads and loads only its own entries (<see cref="ForPlatform"/>);
/// <see cref="Embedded"/> and <see cref="ModelStore"/> resolve it. Every
/// entry of one name gives the same vectors to cosine 0.99999, which is why
/// they share one modelId and one index.
/// </remarks>
public sealed class ModelManifest
{
    [JsonPropertyName("modelId")] public required string ModelId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("licence")] public string? Licence { get; init; }
    [JsonPropertyName("release")] public string? Release { get; init; }
    [JsonPropertyName("dimensions")] public int Dimensions { get; init; } = 768;
    [JsonPropertyName("imageSize")] public int ImageSize { get; init; } = 256;
    [JsonPropertyName("textLength")] public int TextLength { get; init; } = 64;
    [JsonPropertyName("weights")] public string? Weights { get; init; }
    [JsonPropertyName("inputs")] public ModelInputs Inputs { get; init; } = new();
    [JsonPropertyName("tokenizer")] public TokenizerFacts Tokenizer { get; init; } = new();
    [JsonPropertyName("files")] public IReadOnlyList<ModelFile> Files { get; init; } = Array.Empty<ModelFile>();

    /// <summary>The platform <see cref="Files"/> was narrowed to, or null when it lists every platform's entries.</summary>
    [JsonPropertyName("platform"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Platform { get; init; }

    [JsonIgnore] public long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary><c>windows</c>, <c>macos</c> or <c>linux</c>: the names <see cref="ModelFile.Platforms"/> uses.</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>
    /// The files one platform downloads and loads: the entries without
    /// <see cref="ModelFile.Platforms"/> and those naming it. Throws when a
    /// name is then listed twice. Idempotent.
    /// </summary>
    public ModelManifest ForPlatform(string? platform = null)
    {
        platform ??= CurrentPlatform;
        if (string.Equals(Platform, platform, StringComparison.Ordinal)) return this;
        var files = Files.Where(f => f.IsFor(platform)).ToList();
        string? twice = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is not null)
            throw new InvalidDataException($"Manifest for {ModelId} lists '{twice}' more than once for {platform}.");
        return new ModelManifest
        {
            ModelId = ModelId, Version = Version, Source = Source, Licence = Licence, Release = Release,
            Dimensions = Dimensions, ImageSize = ImageSize, TextLength = TextLength, Weights = Weights,
            Inputs = Inputs, Tokenizer = Tokenizer, Files = files, Platform = platform,
        };
    }

    public ModelFile File(string name)
        => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Manifest for {ModelId} has no file '{name}'.");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModelManifest Parse(string json)
        => JsonSerializer.Deserialize<ModelManifest>(json, Json)
           ?? throw new InvalidDataException("Empty model manifest.");

    public static ModelManifest Parse(Stream stream)
        => JsonSerializer.Deserialize<ModelManifest>(stream, Json)
           ?? throw new InvalidDataException("Empty model manifest.");

    private static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonIndented);

    /// <summary>The manifest compiled into this assembly, narrowed to this platform's files.</summary>
    public static ModelManifest Embedded() => EmbeddedAsPublished().ForPlatform();

    /// <summary>The manifest compiled into this assembly, every platform's entries, as the release publishes it.</summary>
    public static ModelManifest EmbeddedAsPublished()
    {
        using Stream? s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Tessera.Desktop.Search.manifest.json");
        if (s is null) throw new InvalidOperationException("The embedded model manifest is missing from the assembly.");
        return Parse(s);
    }
}

public sealed class ModelFile
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }

    /// <summary>The platforms this entry is for (<c>windows</c>, <c>macos</c>, <c>linux</c>); null for every one.</summary>
    [JsonPropertyName("platforms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Platforms { get; init; }

    public bool IsFor(string platform) =>
        Platforms is null || Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Each tower's input as the export found it in the graph.</summary>
public sealed class ModelInputs
{
    [JsonPropertyName("vision")] public ModelInput Vision { get; init; } = new() { Name = "pixel_values" };
    [JsonPropertyName("text")] public ModelInput Text { get; init; } = new() { Name = "input_ids" };
}

public sealed class ModelInput
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>Dimensions as the graph declares them: a number, or the name of a free dimension.</summary>
    [JsonPropertyName("shape")] public IReadOnlyList<JsonElement>? Shape { get; init; }

    /// <summary>
    /// The name of the free batch dimension, which CoreML needs fixed
    /// (<c>AddFreeDimensionOverrideByName</c>). Manifests before model v3 do
    /// not list shapes; their exports named it <c>batch</c>.
    /// </summary>
    [JsonIgnore]
    public string BatchDimension => Shape is [{ ValueKind: JsonValueKind.String } first, ..] ? first.GetString()! : "batch";
}

/// <summary>How the text tower's input ids are built. Mirrors the HF tokenizer the export verified against.</summary>
public sealed class TokenizerFacts
{
    [JsonPropertyName("type")] public string Type { get; init; } = "sentencepiece";
    [JsonPropertyName("bosId")] public int BosId { get; init; } = 2;
    [JsonPropertyName("eosId")] public int EosId { get; init; } = 1;
    [JsonPropertyName("padId")] public int PadId { get; init; }
    [JsonPropertyName("unkId")] public int UnkId { get; init; } = 3;
    [JsonPropertyName("addBos")] public bool AddBos { get; init; }
    [JsonPropertyName("addEos")] public bool AddEos { get; init; } = true;
    [JsonPropertyName("paddingSide")] public string PaddingSide { get; init; } = "right";
    [JsonPropertyName("maxLength")] public int MaxLength { get; init; } = 64;
    [JsonPropertyName("lowercase")] public bool Lowercase { get; init; }
}
