using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Tessera.Api;
using Tessera.Api.Auth;
using Tessera.Api.Endpoints;
using Tessera.Api.Workers;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Migrations;
using Tessera.Data.Repositories;

var builder = WebApplication.CreateBuilder(args);

// A normaliser that quietly does nothing reads exactly like one that works, so
// the rules assert themselves before anything else starts. Failing here is far
// cheaper than filing a corpus into the wrong account for a fortnight.
PathRules.SelfTest();

// ------------------------------------------------------------------ options
builder.Services.Configure<TesseraDbOptions>(builder.Configuration.GetSection(TesseraDbOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<ScannerOptions>(builder.Configuration.GetSection(ScannerOptions.Section));
builder.Services.Configure<WatchOptions>(builder.Configuration.GetSection(WatchOptions.Section));
builder.Services.Configure<UpstreamOptions>(builder.Configuration.GetSection(UpstreamOptions.Section));
builder.Services.Configure<RootOptions>(builder.Configuration.GetSection(RootOptions.Section));

// ------------------------------------------------------------------ data
builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<ContentRepository>();
builder.Services.AddScoped<TimelineRepository>();
builder.Services.AddScoped<AlbumRepository>();
builder.Services.AddScoped<AccountRepository>();
builder.Services.AddScoped<ViewableContentRepository>();
builder.Services.AddScoped<JobRepository>();
builder.Services.AddScoped<MusicRepository>();
builder.Services.AddScoped<FolderRepository>();
builder.Services.AddSingleton<WorkItems>();
builder.Services.AddSingleton<MediaTokens>();
builder.Services.AddSingleton<ExclusiveWork>();
builder.Services.AddSingleton<SchemaMigrator>();

// Scoped: it holds the caller's identity and must never leak between requests.
builder.Services.AddScoped<CurrentUser>();

// Singleton: it enforces "one scan at a time" with an interlocked flag, which
// only works if there is one of it.
builder.Services.AddSingleton<Indexing>();
builder.Services.AddSingleton<Scanner>();
builder.Services.AddHostedService<ThumbnailWorker>();
builder.Services.AddHostedService<MusicWorker>();
builder.Services.AddHostedService<LibraryWatch>();
builder.Services.AddHostedService<FaceDrain>();

// ------------------------------------------------------------------ upstream
var upstream = builder.Configuration.GetSection(UpstreamOptions.Section).Get<UpstreamOptions>()
               ?? new UpstreamOptions();

builder.Services.AddHttpClient("foundation", c =>
{
    c.BaseAddress = new Uri(upstream.Foundation.BaseUrl);
    c.Timeout = upstream.Foundation.Timeout;
});

builder.Services.AddHttpClient("facesearch", c =>
{
    c.BaseAddress = new Uri(upstream.FaceSearch.BaseUrl);
    c.Timeout = upstream.FaceSearch.Timeout;
});

builder.Services.AddHttpClient("cover-picker", c =>
{
    c.BaseAddress = new Uri(upstream.CoverPicker.BaseUrl);
    c.Timeout = upstream.CoverPicker.Timeout;
});

builder.Services.AddHttpClient("music-identifier", c =>
{
    c.BaseAddress = new Uri(upstream.MusicIdentifier.BaseUrl);
    c.Timeout = upstream.MusicIdentifier.Timeout;
});

// ------------------------------------------------------------------ json
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Dapper maps snake_case columns onto PascalCase properties without an alias
// on every column.
DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// The web app is served from another origin during development and through an
// ingress in production, and it sends the session cookie.
const string WebCors = "web";
builder.Services.AddCors(o => o.AddPolicy(WebCors, p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

// First, so the decision covers error pages and unmatched routes as well as
// endpoints. What a client is allowed to keep is a property of the service.
app.UseCachePolicy();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors(WebCors);
app.UseMiddleware<AuthMiddleware>();

// ------------------------------------------------------------------ health
//
// live  = the process is up.
// ready = the database answers.
// Kept apart on purpose: a container that is alive but cannot reach its
// database should stop taking traffic without being killed and restarted into
// the same wall.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
{
    try
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                               title: "Database unreachable", detail: ex.Message);
    }
});

// queues = how much work each job has, for whoever sizes capacity from it.
// The VPN allocator (music-id/scripts) reads this every three minutes to
// decide how many identifier tunnels the music queue deserves; before Tessera
// it read the same number out of Immich's database directly, which is the
// coupling this endpoint exists to end. No login: it is telemetry, on the
// LAN, and there is nothing in a queue depth worth guarding.
app.MapGet("/health/queues", async (JobRepository jobs, CancellationToken ct) =>
{
    var states = await jobs.ListAsync(ct);
    return Results.Ok(states.Select(j => new
    {
        name = j.Name,
        paused = j.IsPaused,
        pending = j.Statistics.Waiting,
        running = j.Statistics.Active,
    }));
});

// -------------------------------------------------------------- migrations
// Before a single request is served. A build whose schema has not been applied
// answers queries against a shape it does not expect, and the failure surfaces
// later as something that reads like a data problem.
{
    var applied = await app.Services.GetRequiredService<SchemaMigrator>()
        .ApplyAsync(CancellationToken.None);
    foreach (var version in applied)
    {
        app.Logger.SchemaMigrated(version);
    }
}

// ------------------------------------------------------------------ routes
app.MapAuth();

// The web app speaks Immich's contract, so that contract is served here rather
// than the app being rewritten. Registered AFTER the native routes so a genuine
// collision fails the build loudly instead of one silently shadowing the other.
app.MapImmichCompat();
app.MapMedia();
app.MapUpstream();
app.MapAdmin();
app.MapScanning();
app.MapQueues();
app.MapCovers();
app.MapWorkers();
app.MapAlbumOps();
app.MapMusic();
app.MapFaces();
app.MapAlbumIndex();

// ----------------------------------------------------------- the web app
// Serve the built frontend from here, so its requests to /api are
// same-origin and land on Tessera. Nothing in that project is modified -
// this reads its build output, and if the folder is absent the API simply
// runs without a UI.
var webRoot = Environment.GetEnvironmentVariable("TESSERA_WEB_ROOT") ?? "/web";
if (Directory.Exists(webRoot) && File.Exists(Path.Combine(webRoot, "index.html")))
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        OnPrepareResponse = HttpCaching.ForStaticFile,
    });

    // An unmatched /api route must 404 as JSON, and it needs saying because
    // the default is worse than an error: the SPA fallback below would answer
    // index.html with a 200, the caller would parse HTML as JSON, and the page
    // would die somewhere far from the missing endpoint. A more specific
    // fallback route wins over the catch-all, so this runs first.
    app.MapFallback("/api/{**rest}", (HttpContext ctx) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "No such endpoint",
        detail: $"{ctx.Request.Method} {ctx.Request.Path} is not implemented by Tessera."));

    // A single-page app owns its own routes, so anything that is not a file
    // and not an API call has to come back as index.html. The pattern this maps
    // is {*path:nonfile}, so a request that names a file - a bundle that is no
    // longer part of this build, say - already 404s rather than being handed a
    // document to execute as a script. Do not add a route under /assets to say
    // the same thing: a matched endpoint makes the static file middleware stand
    // down, and the build's own files stop being served.
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        FileProvider = files,
        OnPrepareResponse = HttpCaching.ForAppShell,
    });
    app.Logger.WebAppServed(webRoot);
}
else
{
    app.Logger.WebAppAbsent(webRoot);
}

app.Run();

/// <summary>Exposed so the integration tests can spin the real host.</summary>
public partial class Program;
