using System.Net.Http.Json;
using System.Text.Json;

namespace Tessera.Api.Workers;

/// <summary>
/// The two GPU services a picture goes through after it is in the library:
/// FaceSearch (faces, the person catalogue) and Foundation (SigLIP2 and
/// DINOv3 vectors, observations). Tessera talks to both; nothing else does.
/// </summary>
/// <remarks>
/// <para>
/// Both take a folder in the HOST's spelling and both keep their own queue.
/// FaceSearch queues one <em>request</em> per folder and runs one at a time
/// when told to; Foundation <em>registers</em> the images on the spot and a
/// resident runner vectorises everything registered when its switch is on.
/// </para>
/// <para>
/// So "queue" is cheap and automatic - every completed scan of a personal
/// account's folder does it - and "start" is the human's, from the jobs
/// page: one Run per service, which then drains everything queued. That is
/// the house rule (tools queue, a person starts, one heavy job at a time)
/// applied to a library where folders arrive every day, instead of a click
/// per album and a command per folder.
/// </para>
/// </remarks>
public sealed class Indexing(IHttpClientFactory http, ILogger<Indexing> log)
{
    public sealed record FaceRequests(int Queued, int Running, int Failed, int Done, long? OldestQueuedId);

    public sealed record RunnerState(bool Wanted, bool Running, int Pending, int? LastExit);

    // ----------------------------------------------------------- FaceSearch

    /// <summary>Queue a folder for face ingest. Not started.</summary>
    public async Task<long?> RequestFacesAsync(string hostFolder, string requestedBy, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient("facesearch").PostAsJsonAsync("/api/ingest-request",
                new { path = hostFolder, requestedBy }, ct);
            if (!res.IsSuccessStatusCode)
            {
                log.IndexingRefused("FaceSearch", (int)res.StatusCode, hostFolder);
                return null;
            }

            var made = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            return made.TryGetProperty("id", out var id) ? id.GetInt64() : null;
        }
        catch (HttpRequestException ex)
        {
            log.IndexingUnreachable("FaceSearch", ex);
            return null;
        }
    }

    /// <summary>Start one queued request. 409 means another ingest is running.</summary>
    public async Task<(bool Started, bool Busy)> StartFacesAsync(long requestId, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient("facesearch").PostAsync($"/api/ingest-request/{requestId}/start", null, ct);
            return (res.IsSuccessStatusCode, (int)res.StatusCode == 409);
        }
        catch (HttpRequestException ex)
        {
            log.IndexingUnreachable("FaceSearch", ex);
            return (false, false);
        }
    }

    public async Task<FaceRequests?> FaceRequestsAsync(CancellationToken ct)
    {
        try
        {
            var body = await http.CreateClient("facesearch").GetFromJsonAsync<JsonElement>("/api/ingest-requests?limit=500", ct);
            if (!body.TryGetProperty("requests", out var list))
            {
                return null;
            }

            int queued = 0, running = 0, failed = 0, done = 0;
            long? oldest = null;
            foreach (var r in list.EnumerateArray())
            {
                var status = r.GetProperty("status").GetString();
                var id = r.GetProperty("id").GetInt64();
                switch (status)
                {
                    case "queued":
                        queued++;
                        oldest = oldest is null || id < oldest ? id : oldest;
                        break;
                    case "running": running++; break;
                    case "done": done++; break;
                    case "unmounted": break;
                    default: failed++; break;
                }
            }

            return new FaceRequests(queued, running, failed, done, oldest);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            log.IndexingUnreachable("FaceSearch", ex);
            return null;
        }
    }

    // ----------------------------------------------------------- Foundation

    /// <summary>Register a folder's images. Cheap - rows, not vectors.</summary>
    public async Task<int?> RegisterAsync(string hostFolder, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient("foundation").PostAsJsonAsync("/api/register", new { path = hostFolder }, ct);
            if (!res.IsSuccessStatusCode)
            {
                log.IndexingRefused("Foundation", (int)res.StatusCode, hostFolder);
                return null;
            }

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.TryGetProperty("registered", out var n) ? n.GetInt32() : 0;
        }
        catch (HttpRequestException ex)
        {
            log.IndexingUnreachable("Foundation", ex);
            return null;
        }
    }

    public async Task<RunnerState?> RunnerAsync(CancellationToken ct)
    {
        try
        {
            var body = await http.CreateClient("foundation").GetFromJsonAsync<JsonElement>("/api/runner", ct);
            return new RunnerState(
                body.GetProperty("wanted").GetBoolean(),
                body.GetProperty("running").GetBoolean(),
                body.GetProperty("pending").GetInt32(),
                body.TryGetProperty("lastExit", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            log.IndexingUnreachable("Foundation", ex);
            return null;
        }
    }

    public async Task<bool> SetRunnerAsync(bool on, CancellationToken ct)
    {
        try
        {
            using var res = on
                ? await http.CreateClient("foundation").PostAsJsonAsync("/api/runner/start", new { scope = "personal" }, ct)
                : await http.CreateClient("foundation").PostAsync("/api/runner/stop", null, ct);
            return res.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            log.IndexingUnreachable("Foundation", ex);
            return false;
        }
    }
}
