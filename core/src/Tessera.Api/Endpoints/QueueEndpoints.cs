using Tessera.Api.Auth;
using Tessera.Api.Workers;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// The jobs page: what the background work is doing, and the controls for it.
/// </summary>
/// <remarks>
/// <para>
/// Shaped as Immich's queue API because the web app is generated against it,
/// but it reports TESSERA'S jobs and there are two of them. Immich declares
/// nineteen queues; this system makes thumbnails and scans the library. A page
/// listing seventeen queues that cannot move would be worse than no page:
/// somebody presses a control, nothing happens, and nothing says why.
/// </para>
/// <para>
/// Every number here is derived from the tables holding the work, so the page
/// cannot show a total that the system does not actually have.
/// </para>
/// </remarks>
public static class QueueEndpoints
{
    public sealed record QueueUpdate(bool? IsPaused);
    public sealed record QueueCommand(string Command, bool? Force);

    public static void MapQueues(this IEndpointRouteBuilder app)
    {
        MapConfig(app);

        var queues = app.MapGroup("/api/queues").WithTags("jobs").RequireUser();

        queues.MapGet("/", async (JobRepository jobs, Indexing indexing, CancellationToken ct) =>
            Results.Ok((await ListAllAsync(jobs, indexing, ct)).Select(Dto)));

        queues.MapGet("/{name}", async (string name, JobRepository jobs, Indexing indexing, CancellationToken ct) =>
        {
            if (!JobNames.IsKnown(name))
            {
                return Results.NotFound();
            }

            var all = await ListAllAsync(jobs, indexing, ct);
            var one = all.First(j => j.Name == name);
            return Results.Ok(Dto(one));
        });

        queues.MapPut("/{name}", async (
            string name, QueueUpdate body, JobRepository jobs, Indexing indexing, CancellationToken ct) =>
        {
            if (!JobNames.IsKnown(name))
            {
                return Results.NotFound();
            }

            if (body.IsPaused is not { } paused)
            {
                return Results.Problem(statusCode: 400, title: "isPaused is required");
            }

            if (name == JobNames.Embeddings)
            {
                // The switch lives in Foundation; Tessera's row mirrors it.
                if (!await indexing.SetRunnerAsync(!paused, ct))
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Foundation is not reachable");
                }
            }

            await jobs.SetPausedAsync(name, paused, ct);
            return Results.NoContent();
        });

        // The web app still sends "start" and "clear-failed" through the older
        // /jobs path, because those two never moved to /queues upstream. Same
        // handlers, one place, so the two paths cannot come to mean different
        // things.
        var legacy = app.MapGroup("/api/jobs").WithTags("jobs").RequireUser();

        legacy.MapPut("/{name}", async (
            string name, QueueCommand body, JobRepository jobs, MusicRepository music, Indexing indexing, CancellationToken ct) =>
        {
            if (!JobNames.IsKnown(name))
            {
                return Results.NotFound();
            }

            var force = body.Force ?? false;

            switch (body.Command)
            {
                case "start":
                    // Starting means QUEUEING work, never doing it here: an
                    // HTTP request that blocks on a million files is a request
                    // that times out halfway through and leaves no trace.
                    if (name == JobNames.Thumbnails)
                    {
                        var queued = await jobs.QueueThumbnailsAsync(force, ct);
                        return Results.Ok(new { queued });
                    }

                    if (name == JobNames.Music)
                    {
                        // Every personal account's videos; force means ask again
                        // about the ones that were asked before.
                        var queued = await music.QueueAsync(null, force ? "all" : "missing", ct);
                        return Results.Ok(new { queued });
                    }

                    if (name == JobNames.Faces || name == JobNames.Embeddings)
                    {
                        // The human start for the GPU work: switch the drain
                        // on. One heavy job at a time - refuse while the other
                        // GPU service is busy rather than let them share the
                        // card and halve each other.
                        var other = name == JobNames.Faces
                            ? (await indexing.RunnerAsync(ct))?.Running == true ? "Foundation's runner is mid-pass" : null
                            : (await indexing.FaceRequestsAsync(ct))?.Running > 0 ? "a FaceSearch ingest is running" : null;
                        if (other is not null)
                        {
                            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                                                   title: $"Not now: {other}. One GPU job at a time.");
                        }

                        if (name == JobNames.Embeddings && !await indexing.SetRunnerAsync(true, ct))
                        {
                            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Foundation is not reachable");
                        }

                        await jobs.SetPausedAsync(name, false, ct);
                        return Results.Ok(new { started = true });
                    }

                    return Results.Problem(
                        statusCode: 400,
                        title: "Start a library scan from its root",
                        detail: "A scan is per-root: POST /api/scan/roots/{rootId}/scan.");

                case "pause":
                    await jobs.SetPausedAsync(name, true, ct);
                    return Results.NoContent();

                case "resume":
                    await jobs.SetPausedAsync(name, false, ct);
                    return Results.NoContent();

                case "empty":
                    return Results.Ok(new { removed = await jobs.EmptyAsync(name, ct) });

                case "clear-failed":
                    return Results.Ok(new { cleared = await jobs.ClearFailedAsync(name, ct) });

                default:
                    return Results.Problem(statusCode: 400, title: $"Unknown command '{body.Command}'");
            }
        });
    }

    /// <summary>
    /// The slice of Immich's system config this system actually has.
    /// </summary>
    /// <remarks>
    /// Immich's config tree is large and almost none of it applies here. Only
    /// <c>job</c> is real, because it is the only part backed by something
    /// that changes behaviour - job_control.concurrency, which the workers
    /// read. Returning a fuller tree of inert settings would put controls on
    /// the admin page that alter nothing.
    ///
    /// The web app PUTs back whatever it was given, so this round-trips.
    /// </remarks>
    private static void MapConfig(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system-config", async (JobRepository jobs, CancellationToken ct) =>
        {
            var all = await jobs.ListAsync(ct);
            return Results.Ok(new
            {
                job = all.ToDictionary(j => j.Name, j => new { concurrency = j.Concurrency }),
            });
        }).WithTags("jobs").RequireUser();

        app.MapPut("/api/system-config", async (
            System.Text.Json.JsonElement body, JobRepository jobs, CancellationToken ct) =>
        {
            if (!body.TryGetProperty("job", out var job) ||
                job.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return Results.Problem(statusCode: 400, title: "Expected a 'job' object");
            }

            foreach (var entry in job.EnumerateObject())
            {
                if (!JobNames.IsKnown(entry.Name) ||
                    !entry.Value.TryGetProperty("concurrency", out var c) ||
                    !c.TryGetInt32(out var value))
                {
                    continue;
                }

                if (value is < 1 or > 64)
                {
                    return Results.Problem(
                        statusCode: 400,
                        title: $"Concurrency for {entry.Name} must be between 1 and 64");
                }

                await jobs.SetConcurrencyAsync(entry.Name, value, ct);
            }

            var all = await jobs.ListAsync(ct);
            return Results.Ok(new
            {
                job = all.ToDictionary(j => j.Name, j => new { concurrency = j.Concurrency }),
            });
        }).WithTags("jobs").RequireUser();
    }

    /// <summary>
    /// Immich's QueueResponseDto. Its statistics carry six numbers; this system
    /// has four of them and reports the other two as zero rather than inventing
    /// a plausible figure - delayed and paused are Redis concepts, and a
    /// leased Postgres queue has neither.
    /// </summary>
    /// <summary>
    /// Tessera's own queues plus the two that live in other services, read
    /// live: faces from FaceSearch's request list, embeddings from
    /// Foundation's runner. Unreachable shows as zeros with the switch as
    /// Tessera last knew it - never as a made-up healthy number.
    /// </summary>
    private static async Task<List<JobState>> ListAllAsync(JobRepository jobs, Indexing indexing, CancellationToken ct)
    {
        var all = (await jobs.ListAsync(ct)).ToList();
        var faces = await indexing.FaceRequestsAsync(ct);
        var runner = await indexing.RunnerAsync(ct);

        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Name == JobNames.Faces && faces is not null)
            {
                all[i] = all[i] with { Statistics = new JobStatistics(faces.Running, faces.Queued, faces.Failed, faces.Done) };
            }
            else if (all[i].Name == JobNames.Embeddings && runner is not null)
            {
                all[i] = all[i] with
                {
                    IsPaused = !runner.Wanted,
                    Statistics = new JobStatistics(runner.Running ? 1 : 0, runner.Pending, runner.LastExit is > 0 ? 1 : 0, 0),
                };
            }
        }

        return all;
    }

    private static object Dto(JobState j) => new
    {
        name = j.Name,
        isPaused = j.IsPaused,
        statistics = new
        {
            active = j.Statistics.Active,
            completed = j.Statistics.Completed,
            failed = j.Statistics.Failed,
            delayed = 0,
            waiting = j.Statistics.Waiting,
            paused = 0,
        },
    };
}
