using System.Globalization;
using System.Reflection;

namespace Tessera.Api.Boot;

/// <summary>A start that cannot proceed, with the exit code the shell reads.</summary>
public sealed class BootException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// What the shell hands the core, and where everything under the data dir
/// lives. CONTRACT §1 and §7.
/// </summary>
public sealed record CoreEnvironment(string Token, string DataDir, string WebOrigin, int Port, bool Dev)
{
    /// <summary>Exit code when a required variable is missing or malformed.</summary>
    public const int ExitBadEnvironment = 2;

    /// <summary>Exit code when core.pid names a process that is still alive.</summary>
    public const int ExitAlreadyRunning = 3;

    public const string DevOrigin = "http://localhost:5173";

    public string DbPath => Path.Combine(DataDir, "library.db");
    public string ThumbsDir => Path.Combine(DataDir, "thumbs");
    public string ModelsDir => Path.Combine(DataDir, "models");
    public string VectorsDir => Path.Combine(DataDir, "vectors");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string PidFile => Path.Combine(DataDir, "core.pid");

    /// <summary>The origins CORS allows: the shell's, plus Vite's in dev.</summary>
    public string[] AllowedOrigins => Dev ? [WebOrigin, DevOrigin] : [WebOrigin];

    /// <summary>The product version, as the csproj declares it.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Read the environment. Throws <see cref="BootException"/> rather than guessing.</summary>
    public static CoreEnvironment Read()
    {
        var token = Required("TESSERA_TOKEN");
        if (token.Length < 16)
        {
            throw new BootException(ExitBadEnvironment, "TESSERA_TOKEN is too short to be a token.");
        }

        var dataDir = Required("TESSERA_DATA_DIR");
        if (!Path.IsPathRooted(dataDir))
        {
            throw new BootException(ExitBadEnvironment, $"TESSERA_DATA_DIR must be an absolute path; got '{dataDir}'.");
        }

        var origin = Required("TESSERA_WEB_ORIGIN");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.PathAndQuery != "/")
        {
            throw new BootException(ExitBadEnvironment, $"TESSERA_WEB_ORIGIN must be an origin like http://tauri.localhost; got '{origin}'.");
        }

        var portText = Environment.GetEnvironmentVariable("TESSERA_PORT");
        var port = 0;
        if (!string.IsNullOrWhiteSpace(portText) &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535))
        {
            throw new BootException(ExitBadEnvironment, $"TESSERA_PORT must be 0..65535; got '{portText}'.");
        }

        var dev = Environment.GetEnvironmentVariable("TESSERA_DEV") == "1";

        return new CoreEnvironment(token, Path.GetFullPath(dataDir), origin.TrimEnd('/'), port, dev);
    }

    /// <summary>Make every directory of §7 exist.</summary>
    public void CreateDirectories()
    {
        foreach (var dir in new[] { DataDir, ThumbsDir, ModelsDir, VectorsDir, LogsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : throw new BootException(ExitBadEnvironment, $"{name} is not set. The shell sets it; by hand, export it first.");

    private static string ReadVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
