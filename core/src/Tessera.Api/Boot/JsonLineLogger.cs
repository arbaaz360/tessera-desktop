using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tessera.Api.Boot;

/// <summary>
/// Every log line as one JSON object, to stderr and to <c>logs/core-&lt;date&gt;.log</c>.
/// </summary>
/// <remarks>
/// Stdout belongs to the handshake and carries exactly one line, ever
/// (CONTRACT §1), so nothing here may touch it. The file rolls by UTC date;
/// the shell's log export zips the folder.
/// </remarks>
public sealed class JsonLineLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly TextWriter _stderr;
    private StreamWriter? _file;
    private DateOnly _fileDate;

    public JsonLineLoggerProvider(string logsDir, TextWriter? stderr = null)
    {
        _dir = logsDir;
        _stderr = stderr ?? Console.Error;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    internal void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var now = DateTimeOffset.UtcNow;
        var line = JsonSerializer.Serialize(new LogLine(
            now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            LevelName(level),
            category,
            eventId.Id == 0 ? null : eventId.Id,
            message,
            exception?.ToString()), LogLineContext.Default.LogLine);

        lock (_gate)
        {
            _stderr.WriteLine(line);
            _stderr.Flush();
            try
            {
                FileFor(DateOnly.FromDateTime(now.UtcDateTime)).WriteLine(line);
            }
            catch (IOException)
            {
                // The disk is the one thing a log line may not take the service down over.
            }
        }
    }

    private StreamWriter FileFor(DateOnly date)
    {
        if (_file is null || date != _fileDate)
        {
            _file?.Dispose();
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"core-{date:yyyy-MM-dd}.log");
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _fileDate = date;
        }

        return _file;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "none",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private sealed class Logger(JsonLineLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Write(logLevel, category, eventId, formatter(state, exception), exception);
        }
    }
}

internal sealed record LogLine(string Ts, string Level, string Category, int? Event, string Message, string? Exception);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LogLine))]
internal sealed partial class LogLineContext : JsonSerializerContext;
