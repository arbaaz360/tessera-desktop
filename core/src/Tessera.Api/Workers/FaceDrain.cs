using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// While the "faces" switch is on, starts FaceSearch's queued ingest
/// requests one after another, and switches itself off when none are left.
/// </summary>
/// <remarks>
/// FaceSearch queues requests and runs exactly one at a time, when told to.
/// Before this, "told to" was a person per request. The switch on the jobs
/// page is still the person - one press - and this loop is the finger that
/// presses Start on each request in turn, waiting for the previous to finish
/// and for Foundation's runner to be idle (one GPU job at a time). Nothing
/// starts while the switch is off, and the switch turns itself off at the
/// end, so a folder copied in tomorrow waits for a person again.
/// </remarks>
public sealed class FaceDrain(IServiceProvider services, Indexing indexing, ILogger<FaceDrain> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.WatchTickFailed(ex);
            }

            await Task.Delay(Every, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        if (await jobs.IsPausedAsync(JobNames.Faces, ct))
        {
            return;
        }

        var queue = await indexing.FaceRequestsAsync(ct);
        if (queue is null || queue.Running > 0)
        {
            return;                                  // unreachable, or busy: look again later
        }

        if (queue.OldestQueuedId is not { } next)
        {
            await jobs.SetPausedAsync(JobNames.Faces, true, ct);
            log.FaceDrainDone();
            return;
        }

        if ((await indexing.RunnerAsync(ct))?.Running == true)
        {
            return;                                  // Foundation has the card
        }

        var (started, busy) = await indexing.StartFacesAsync(next, ct);
        if (started)
        {
            log.FaceDrainStarted(next, queue.Queued - 1);
        }
        else if (!busy)
        {
            // Refused for a reason other than "already running" - an unmounted
            // path, say. Leave the switch on; the next tick picks the next
            // request, and this one stays visible on FaceSearch's own list.
        }
    }
}
