using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Api.Workers;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>The scan routes the web app already calls (CONTRACT §3).</summary>
public static class ScanEndpoints
{
    public static void MapScanning(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/scan").WithTags("scan").RequireUser();

        // A POST that returns at once with the run id rather than blocking
        // for an hour: a scan of 200,000 files is not an HTTP request.
        // Progress is read from /api/library.
        g.MapPost("/roots/{rootId:long}/scan", async (
            long rootId, string? folder, ScanRunner scans, LibraryRepository library, CancellationToken ct) =>
        {
            if (await library.RootAsync(rootId, ct) is null)
            {
                return Api.NotFound($"No root {rootId}");
            }

            var runId = await scans.EnqueueAsync(rootId, folder, ct);
            if (runId is null)
            {
                return Api.Fail(StatusCodes.Status409Conflict,
                    "That scan is already queued or running. Watch /api/library.");
            }

            return Results.Accepted("/api/library", new { started = true, rootId, folder = Scanner.NormaliseFolder(folder), runId });
        });

        // What the watch has noticed and not yet scanned: folders still
        // settling, and folders queued. Nothing waits for a person any more -
        // the buyer chose the folder - so waitingForYou is always false and is
        // kept only so the shape does not change under the web app.
        g.MapGet("/folders", async (FolderRepository folders, IOptions<WatchOptions> watch, CancellationToken ct) =>
        {
            var rows = await folders.WaitingAsync(ct);
            return Results.Ok(new
            {
                settleSeconds = (int)watch.Value.Settle.TotalSeconds,
                folders = rows.Select(r => new
                {
                    r.RootId, r.RootLabel, r.AccountId, r.Folder, r.Entries,
                    firstSeen = Api.Iso(r.FirstSeen), changedAt = Api.Iso(r.ChangedAt),
                    waitingForYou = false,
                }),
            });
        });
    }
}
