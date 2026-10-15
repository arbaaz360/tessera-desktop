using System.Text.Json;
using Tessera.Api.Auth;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Which workers Tessera can reach right now.
/// </summary>
/// <remarks>
/// <para>
/// A worker behind a contract (docs/contracts) can be down without Tessera
/// being down, and the web app wants to know before it offers a button that
/// would fail. This asks each worker's own /health and reports it as-is: a
/// worker that is up but whose evidence source is not says so itself, and
/// that answer is passed through rather than flattened into "ok".
/// </para>
/// <para>
/// Two views of one probe. <c>/api/workers</c> is the whole answer, for a
/// logged-in person. <c>/health/workers</c> is the summary - up, and how
/// many lanes the music worker can use - for whatever sizes capacity from
/// it (the VPN allocator in music-id/scripts), with no login: it is
/// telemetry, and nothing in it is worth guarding.
/// </para>
/// <para>
/// One place, so a new worker is added here and nowhere else in the UI's
/// picture of the system.
/// </para>
/// </remarks>
public static class WorkerEndpoints
{
    private static readonly (string Name, string Client)[] Workers =
    [
        ("cover-picker", "cover-picker"),
        ("music-identifier", "music-identifier"),
    ];

    private sealed record Probe(string Name, bool Ok, JsonElement? Detail);

    public static void MapWorkers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workers", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            var probes = await ProbeAllAsync(http, ct);
            return Results.Ok(probes.Select(p => new
            {
                name = p.Name,
                ok = p.Ok,
                detail = p.Detail is { } d ? (object)d : new { error = "unreachable" },
            }));
        }).WithTags("workers").RequireUser();

        app.MapGet("/health/workers", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            var probes = await ProbeAllAsync(http, ct);
            var music = probes.Single(p => p.Name == "music-identifier");
            var lanes = music.Detail is { } d && d.TryGetProperty("identifiers", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().ToList()
                : [];
            return Results.Ok(new
            {
                coverPicker = new { ok = probes.Single(p => p.Name == "cover-picker").Ok },
                music = new
                {
                    ok = music.Ok,
                    reachable = lanes.Count(l => l.TryGetProperty("reachable", out var r) && r.ValueKind == JsonValueKind.True),
                    usable = music.Detail is { } m && m.TryGetProperty("usable", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
                    lanes = lanes.Count,
                },
            });
        }).WithTags("workers");
    }

    private static Task<Probe[]> ProbeAllAsync(IHttpClientFactory http, CancellationToken ct) =>
        Task.WhenAll(Workers.Select(async w =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var res = await http.CreateClient(w.Client).GetAsync("/health", cts.Token);
                if (!res.IsSuccessStatusCode)
                {
                    return new Probe(w.Name, false, null);
                }

                var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token)).RootElement.Clone();
                var ok = body.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
                return new Probe(w.Name, ok, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new Probe(w.Name, false, null);
            }
        }));
}
