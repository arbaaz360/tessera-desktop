using System.Text.Json;

// Timing tests (200k search, embed throughput) must not share the cores.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tessera.Desktop.Search.Tests;

/// <summary>Where the optional local model export and the fixtures live.</summary>
internal static class TestPaths
{
    public static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>
    /// A directory holding vision.onnx, text.onnx and tokenizer.model:
    /// <c>TESSERA_MODELS_DIR</c>, else <c>models/out</c> in the repo, else null.
    /// Tests that need the model say "skipped" and pass when this is null.
    /// </summary>
    public static string? ModelDir { get; } = FindModelDir();

    /// <summary>Folder of real, non-personal images for the qualitative check, or null.</summary>
    public static string? QualitativeImages { get; } = FindQualitativeDir();

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Numbers the tests measured, for the README. One file per test class.</summary>
    public static string NumbersDir
    {
        get
        {
            string dir = Path.Combine(RepoRoot, "search", "tests", "out");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void WriteNumbers(string name, object numbers)
    {
        string path = Path.Combine(NumbersDir, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(numbers, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "CONTRACT.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string? FindModelDir()
    {
        string? env = Environment.GetEnvironmentVariable("TESSERA_MODELS_DIR");
        foreach (string? candidate in new[] { env, Path.Combine(FindRepoRoot(), "models", "out") })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (File.Exists(Path.Combine(candidate, "vision.onnx"))
                && File.Exists(Path.Combine(candidate, "text.onnx"))
                && File.Exists(Path.Combine(candidate, "tokenizer.model")))
                return candidate;
        }
        return null;
    }

    private static string? FindQualitativeDir()
    {
        string? env = Environment.GetEnvironmentVariable("TESSERA_QUALITATIVE_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        // Screenshots on this machine: windows, apps and web pages, no personal photos.
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string greenshot = Path.Combine(pictures, "Greenshot");
        return Directory.Exists(greenshot) ? greenshot : null;
    }
}
