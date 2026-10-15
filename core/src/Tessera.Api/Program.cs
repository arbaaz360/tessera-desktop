using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Tessera.Api;
using Tessera.Api.Auth;
using Tessera.Api.Boot;
using Tessera.Api.Endpoints;
using Tessera.Api.Licence;
using Tessera.Api.Search;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Migrations;
using Tessera.Data.Repositories;
using Tessera.Desktop.Search;

// ------------------------------------------------------------------ boot
//
// Everything that can fail before there is a host exits with a code the
// shell can read (CONTRACT §1): 2 for a bad environment, 3 for a live core
// already on this data dir. Stdout carries one line, ever: the ready line.
CoreEnvironment env;
try
{
    env = CoreEnvironment.Read();
    env.CreateDirectories();
}
catch (BootException ex)
{
    Fatal(ex.Message);
    return ex.ExitCode;
}

if (PidFile.LiveOwner(env.PidFile) is { } other)
{
    Fatal($"Another tessera-core (pid {other}) is running on {env.DataDir}; refusing to start twice.");
    return CoreEnvironment.ExitAlreadyRunning;
}

PidFile.Write(env.PidFile, Environment.ProcessId);

try
{
    // A normaliser that quietly does nothing reads exactly like one that
    // works, so the rules assert themselves before anything else starts.
    PathRules.SelfTest();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // -------------------------------------------------------------- logging
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new JsonLineLoggerProvider(env.LogsDir));
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter("System", LogLevel.Warning);

    // -------------------------------------------------------------- kestrel
    // 127.0.0.1 only, never 0.0.0.0. Port 0 lets the OS choose.
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, env.Port));

    // -------------------------------------------------------------- services
    builder.Services.AddSingleton(env);
    builder.Services.AddSingleton<Readiness>();
    builder.Services.AddSingleton(new Db(env.DbPath));
    builder.Services.AddSingleton<SchemaMigrator>();

    // Repositories are stateless over Db, so they are singletons and the
    // workers take them directly: no captive scopes, no service locator.
    builder.Services.AddSingleton<ContentRepository>();
    builder.Services.AddSingleton<TimelineRepository>();
    builder.Services.AddSingleton<AlbumRepository>();
    builder.Services.AddSingleton<AccountRepository>();
    builder.Services.AddSingleton<ViewableContentRepository>();
    builder.Services.AddSingleton<JobRepository>();
    builder.Services.AddSingleton<FolderRepository>();
    builder.Services.AddSingleton<SettingsRepository>();
    builder.Services.AddSingleton<LibraryRepository>();
    builder.Services.AddSingleton<WorkItems>();
    builder.Services.AddSingleton<ExclusiveWork>();
    builder.Services.AddSingleton<FreeTier>();

    builder.Services.Configure<StorageOptions>(o => o.ThumbnailRoot = env.ThumbsDir);
    builder.Services.Configure<ScannerOptions>(_ => { });
    builder.Services.Configure<WatchOptions>(_ => { });

    // Scoped: it holds the caller's identity and must never leak between requests.
    builder.Services.AddScoped<CurrentUser>();

    // -------------------------------------------------------------- search
    // CONTRACT §6: the core consumes IImageEmbedder and IVectorIndex. Until
    // the search library is merged, the fake stands in. The coordinator
    // replaces these two lines with AddTesseraSearch(new SearchOptions {
    // ModelsDir = env.ModelsDir }) and nothing else changes.
    builder.Services.AddSingleton<IImageEmbedder, FakeImageEmbedder>();
    builder.Services.AddSingleton<IVectorIndex>(sp => new InMemoryVectorIndex(sp.GetRequiredService<IImageEmbedder>().Dimensions));

    builder.Services.AddSingleton<VectorStore>();
    builder.Services.AddSingleton<ModelReadiness>();
    builder.Services.AddSingleton<PowerSource>();
    builder.Services.AddSingleton<SearchService>();

    // -------------------------------------------------------------- workers
    builder.Services.AddSingleton<Scanner>();
    builder.Services.AddSingleton<ScanRunner>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanRunner>());
    builder.Services.AddSingleton<LibraryWatch>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryWatch>());
    builder.Services.AddSingleton<ThumbnailWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ThumbnailWorker>());
    builder.Services.AddSingleton<EmbeddingWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddingWorker>());

    // -------------------------------------------------------------- json
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    // -------------------------------------------------------------- cors
    // One origin: the shell's. Plus Vite's in dev. Credentials are allowed so a
    // fetch with credentials: 'include' is not refused by the browser.
    const string WebCors = "web";
    builder.Services.AddCors(o => o.AddPolicy(WebCors, p => p
        .WithOrigins(env.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

    var app = builder.Build();
    app.Logger.BootStarting(CoreEnvironment.Version, env.DataDir, env.WebOrigin, env.Dev);

    // -------------------------------------------------------------- pipeline
    // First, so the decision covers error pages and unmatched routes as well
    // as endpoints. What a client is allowed to keep is a property of the service.
    app.UseCachePolicy();

    // Errors are a status and {"detail": "…"}, nothing else (CONTRACT §4).
    app.UseExceptionHandler(h => h.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { detail = "Internal error. See logs/ in the data dir." });
    }));
    app.UseStatusCodePages(async ctx =>
    {
        var response = ctx.HttpContext.Response;
        if (!response.HasStarted)
        {
            await response.WriteAsJsonAsync(new { detail = ReasonPhrases.GetReasonPhrase(response.StatusCode) });
        }
    });
    app.UseCors(WebCors);
    app.UseMiddleware<TokenAuthMiddleware>();

    // -------------------------------------------------------------- health
    // live  = the process is up.
    // ready = the database is migrated and the workers are running.
    app.MapGet("/health/live", () => Results.Ok(new
    {
        status = "live",
        pid = Environment.ProcessId,
        version = CoreEnvironment.Version,
    }));

    app.MapGet("/health/ready", (Readiness readiness, ScanRunner scans) => readiness.IsReady
        ? Results.Ok(new
        {
            status = "ready",
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - readiness.StartedAt).TotalSeconds,
            scansQueued = scans.Queued,
        })
        : Api.Fail(StatusCodes.Status503ServiceUnavailable, "starting"));

    // -------------------------------------------------------------- migrations
    // Before a single request is served. A build whose schema has not been
    // applied answers queries against a shape it does not expect.
    {
        var applied = await app.Services.GetRequiredService<SchemaMigrator>().ApplyAsync(CancellationToken.None);
        foreach (var version in applied)
        {
            app.Logger.SchemaMigrated(version);
        }

        var interrupted = await app.Services.GetRequiredService<LibraryRepository>().MarkInterruptedRunsAsync(CancellationToken.None);
        if (interrupted > 0)
        {
            app.Logger.InterruptedRuns(interrupted);
        }
    }

    // -------------------------------------------------------------- routes
    app.MapImmichCompat();
    app.MapMedia();
    app.MapAlbumOps();
    app.MapScanning();
    app.MapLibrary();
    app.MapSearch();
    app.MapSettings();

    // An unmatched /api route is a JSON 404, not an empty body: the caller
    // parses it and gets a sentence that names the route.
    app.MapFallback("/api/{**rest}", (HttpContext ctx) => Api.Fail(
        StatusCodes.Status404NotFound,
        $"{ctx.Request.Method} {ctx.Request.Path} is not implemented by tessera-core."));

    // -------------------------------------------------------------- go
    await app.StartAsync();

    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
        ?? throw new InvalidOperationException("Kestrel did not report an address.");
    var port = new Uri(addresses.First()).Port;

    app.Services.GetRequiredService<Readiness>().MarkReady();
    app.Logger.BootReady(port, Environment.ProcessId);

    // The one line on stdout, ever.
    Console.Out.WriteLine(JsonSerializer.Serialize(
        new ReadyLine("ready", port, Environment.ProcessId, CoreEnvironment.Version), ReadyLineContext.Default.ReadyLine));
    Console.Out.Flush();

    StdinWatch.Start(() =>
    {
        app.Logger.BootStdinClosed();
        app.Lifetime.StopApplication();
    });

    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception ex)
{
    Fatal($"tessera-core failed to start: {ex}");
    return 1;
}
finally
{
    PidFile.Remove(env.PidFile, Environment.ProcessId);
}

static void Fatal(string message) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        new FatalLine(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), "fatal", "boot", message),
        ReadyLineContext.Default.FatalLine));

/// <summary>The handshake line: <c>{"event":"ready","port":N,"pid":P,"version":"1.0.0"}</c>.</summary>
internal sealed record ReadyLine(string Event, int Port, int Pid, string Version);

internal sealed record FatalLine(string Ts, string Level, string Category, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyLine))]
[JsonSerializable(typeof(FatalLine))]
internal sealed partial class ReadyLineContext : JsonSerializerContext;

/// <summary>Exposed so the tests can find the executable's assembly.</summary>
public partial class Program;
