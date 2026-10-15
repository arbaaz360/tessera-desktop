using System.Net.Http.Json;
using System.Text.Json;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Sends queued videos to the music identifier and stores what it says.
/// </summary>
/// <remarks>
/// <para>
/// The shape every worker integration takes (docs/contracts/music.md): this
/// loop owns the queue and the result, the worker owns the listening. It is
/// handed a URL to the bytes with a token Tessera minted for that one file,
/// and answers "this track" or "heard it, nothing matched". Both are stored;
/// a video that was asked is not asked again until someone says so.
/// </para>
/// <para>
/// A worker that could NOT do its job - the identifier is down, the file did
/// not answer - returns a 5xx, and the item goes back to the queue for its
/// next attempt. That is a different thing from "no match", and the two are
/// never confused: one is an answer, the other is the absence of one.
/// </para>
/// <para>
/// A 503 with <c>Retry-After</c> is a third thing: every identifier lane is
/// resting (throttled), so no item will succeed until it lifts. The item goes
/// back untouched and the whole loop waits it out, rather than burning three
/// attempts per item against a wall and calling 58,000 videos failed.
/// </para>
/// <para>
/// Not under the archive lock. Reading twelve seconds of one video is not a
/// disk-wide walk, and it does not deserve to stop a thumbnail run.
/// </para>
/// </remarks>
public sealed class MusicWorker(
    WorkItems queue,
    IServiceProvider services,
    MediaTokens tokens,
    IHttpClientFactory http,
    IConfiguration config,
    ILogger<MusicWorker> log) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(15);

    /// <summary>Until when the worker has been told nothing will succeed.</summary>
    private DateTimeOffset _holdUntil = DateTimeOffset.MinValue;

    /// <summary>Where the worker reaches THIS service to fetch bytes.</summary>
    private readonly string _selfBase =
        (config["Tessera:SelfBaseUrl"] ?? "http://tessera-api:2500").TrimEnd('/');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hold = _holdUntil - DateTimeOffset.UtcNow;
                if (hold > TimeSpan.Zero)
                {
                    await Task.Delay(hold, stoppingToken);
                    continue;
                }

                if (await DrainAsync(stoppingToken) == 0)
                {
                    await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.MusicLoopFailed(ex);
                await Task.Delay(Idle, stoppingToken);
            }
        }
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        var music = scope.ServiceProvider.GetRequiredService<MusicRepository>();

        if (await jobs.IsPausedAsync(JobNames.Music, ct))
        {
            return 0;
        }

        var parallel = await jobs.ConcurrencyAsync(JobNames.Music, 1, ct);
        var batch = await queue.LeaseAsync(JobNames.MusicKind, parallel * 4, Lease, ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
            async (item, token) =>
        {
            if (item.Root is null)
            {
                await queue.AbandonAsync(item.WorkId, "every copy of the file is gone", token);
                return;
            }

            if (_holdUntil > DateTimeOffset.UtcNow)
            {
                // A sibling in this batch just learned that nothing will
                // succeed; this item never gets asked.
                await queue.ReleaseAsync(item.WorkId, CancellationToken.None);
                return;
            }

            try
            {
                var answer = await IdentifyAsync(item, token);
                await music.RecordAsync(item.ContentId, answer, token);
                await queue.CompleteAsync(item.WorkId, null, token);
            }
            catch (EveryLaneResting resting)
            {
                await queue.ReleaseAsync(item.WorkId, CancellationToken.None);
                var until = DateTimeOffset.UtcNow + resting.RetryAfter;
                if (until > _holdUntil)
                {
                    _holdUntil = until;
                    log.MusicHeld(resting.RetryAfter);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await queue.CompleteAsync(item.WorkId, ex.Message, CancellationToken.None);
                log.MusicFailed(ex, item.ContentId);
            }
        });

        return batch.Count;
    }

    private async Task<Identification> IdentifyAsync(Leased item, CancellationToken ct)
    {
        var url = $"{_selfBase}/api/worker-media/{item.ContentId}/original?token={tokens.Issue(item.ContentId)}";
        var client = http.CreateClient("music-identifier");

        using var res = await client.PostAsJsonAsync("/identify",
            new { id = Convert.ToHexStringLower(item.Sha256), url }, ct);

        if (res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && res.Headers.RetryAfter is { } retry)
        {
            var wait = retry.Delta ?? (retry.Date is { } at ? at - DateTimeOffset.UtcNow : Idle);
            throw new EveryLaneResting(wait < Idle ? Idle : wait > MaxHold ? MaxHold : wait);
        }

        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"identifier answered {(int)res.StatusCode}: {Trim(detail)}");
        }

        var body = await res.Content.ReadFromJsonAsync<WorkerAnswer>(ct)
                   ?? throw new InvalidOperationException("identifier answered with an empty body");

        return new Identification(
            body.Matched,
            body.Track?.Key, body.Track?.Title, body.Track?.Artist, body.Track?.Album, body.Track?.ArtworkUrl,
            body.Identifier.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            body.Track?.ProviderTrackId,
            body.Track?.Raw.GetRawText() ?? "{}",
            body.Track?.OffsetSeconds, body.Track?.Confidence,
            body.Identifier.ValueKind == JsonValueKind.Object ? body.Identifier.GetRawText() : "{}");
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    /// <summary>The worker said nobody can identify anything until then.</summary>
    private sealed class EveryLaneResting(TimeSpan retryAfter) : Exception("every identifier lane is resting")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }

    // The identifier block is the worker's to shape (name, version, a reason
    // such as no_audio) and is stored as it came.
    private sealed record WorkerAnswer(bool Matched, WorkerTrack? Track, JsonElement Identifier);

    private sealed record WorkerTrack(
        string Key, string Title, string Artist, string? Album, string? ArtworkUrl,
        string? ProviderTrackId, double? OffsetSeconds, double? Confidence, JsonElement Raw);

}
