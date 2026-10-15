using Tessera.Api.Auth;
using Tessera.Api.Boot;
using Tessera.Api.Licence;
using Tessera.Api.Workers;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>Settings and the licence state (CONTRACT §4).</summary>
public static class SettingsEndpoints
{
    public sealed record SettingsBody(int? ThumbnailConcurrency, bool? EmbedOnBattery);
    public sealed record LicenceBody(string? State);

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings").WithTags("settings").RequireUser();

        settings.MapGet("/", async (SettingsRepository repo, CoreEnvironment env, CancellationToken ct) =>
            Results.Ok(Dto(await repo.ReadAsync(ct), env)));

        // Partial: a field left out keeps its value.
        settings.MapPut("/", async (SettingsBody body, SettingsRepository repo, CoreEnvironment env, CancellationToken ct) =>
        {
            if (body.ThumbnailConcurrency is { } n)
            {
                if (n is < 1 or > 64)
                {
                    return Api.Fail(StatusCodes.Status400BadRequest, "thumbnailConcurrency must be 1..64");
                }

                await repo.SetThumbnailConcurrencyAsync(n, ct);
            }

            if (body.EmbedOnBattery is { } b)
            {
                await repo.SetEmbedOnBatteryAsync(b, ct);
            }

            return Results.Ok(Dto(await repo.ReadAsync(ct), env));
        });

        var licence = app.MapGroup("/api/licence").WithTags("licence").RequireUser();

        licence.MapGet("/", async (FreeTier tier, CancellationToken ct) =>
            Results.Ok(LibraryEndpoints.LicenceDto(await tier.StatusAsync(ct))));

        // The shell calls this after verifying with Gumroad; the core trusts
        // it (loopback, token) and persists it. Going to "licensed" releases
        // the queue: everything the cap held back is queued now.
        licence.MapPut("/", async (
            LicenceBody body, SettingsRepository repo, FreeTier tier, JobRepository jobs,
            EmbeddingWorker embeddings, CancellationToken ct) =>
        {
            if (!LicenceStates.IsKnown(body.State))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "state must be \"free\" or \"licensed\"");
            }

            await repo.SetLicenceStateAsync(body.State!, ct);
            if (body.State == LicenceStates.Licensed)
            {
                await jobs.QueueThumbnailsAsync(null, ct);
                await embeddings.BackfillAsync(ct);
            }

            return Results.Ok(LibraryEndpoints.LicenceDto(await tier.StatusAsync(ct)));
        });
    }

    private static object Dto(AppSettings s, CoreEnvironment env) => new
    {
        thumbnailConcurrency = s.ThumbnailConcurrency,
        embedOnBattery = s.EmbedOnBattery,
        version = CoreEnvironment.Version,
        dataDir = env.DataDir,
    };
}
