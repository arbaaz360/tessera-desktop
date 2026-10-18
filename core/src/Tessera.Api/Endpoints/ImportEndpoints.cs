using Microsoft.AspNetCore.Http.Features;
using Tessera.Api.Auth;
using Tessera.Api.Import;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Adding pictures: a drop, a file pick, a folder. One route takes paths and
/// links and answers with a job; one takes bytes the webview handed over.
/// </summary>
public static class ImportEndpoints
{
    public sealed record ImportBody(string[]? Paths, string[]? Urls, string? AlbumId);

    public static void MapImport(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/import").WithTags("import").RequireUser();

        // 202 and the job, every item pending. Folders are indexed where they
        // are; files inside a library folder too; any other file is copied
        // into the Tessera folder; a link is fetched once.
        g.MapPost("/", async (
            ImportBody body, CurrentUser me, AlbumRepository albums, ImportRunner imports, CancellationToken ct) =>
        {
            var paths = (body.Paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var urls = (body.Urls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (paths.Count + urls.Count == 0)
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "Nothing to import: give paths or urls");
            }

            if (paths.Count + urls.Count > 100_000)
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "At most 100,000 items in one import");
            }

            var (albumId, folderName, error) = await AlbumAsync(body.AlbumId, me, albums, ct);
            if (error is not null)
            {
                return error;
            }

            var inputs = paths.Select(p => new ImportInput(Directory.Exists(p) ? ImportKind.Folder : ImportKind.File, p))
                .Concat(urls.Select(u => new ImportInput(ImportKind.Url, u)))
                .ToList();
            var job = await imports.EnqueueAsync(me.AccountIdOrThrow, albumId, folderName, inputs, ct);
            return Results.Accepted($"/api/import/{job.Id}", Dto(job.Snapshot()));
        });

        g.MapGet("/{id:long}", async (long id, ImportRunner imports, ImportRepository runs, CancellationToken ct) =>
        {
            if (imports.Get(id) is { } job)
            {
                return Results.Ok(Dto(job.Snapshot()));
            }

            // From before a restart: the numbers survive, the per-item answers do not.
            return await runs.GetAsync(id, ct) is { } run ? Results.Ok(Dto(run)) : Api.NotFound($"No import {id}");
        });

        // The bytes of one dropped file, when the webview hands over data
        // rather than a path: ?name= the file's name, ?albumId= the album.
        g.MapPost("/upload", async (
            string? name, string? albumId, HttpContext ctx, CurrentUser me, AlbumRepository albums,
            ImportRunner imports, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Api.Fail(StatusCodes.Status400BadRequest, "name is required: the file's own name");
            }

            var (album, folderName, error) = await AlbumAsync(albumId, me, albums, ct);
            if (error is not null)
            {
                return error;
            }

            // A video is larger than Kestrel's default body limit of 30 MB.
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = Importer.MaxDownloadBytes;
            }

            var job = await imports.RunBytesAsync(me.AccountIdOrThrow, album, folderName, ctx.Request.Body, name, ct);
            return Results.Ok(Dto(job.Snapshot()));
        });
    }

    private static async Task<(long? AlbumId, string FolderName, IResult? Error)> AlbumAsync(
        string? raw, CurrentUser me, AlbumRepository albums, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "Unsorted", null);
        }

        if (!Api.TryParseId(raw, out var albumId) || await albums.GetAsync(me.AccountIdOrThrow, albumId, ct) is not { } album)
        {
            return (null, "", Api.NotFound($"No album {raw}"));
        }

        return (albumId, album.Name, null);
    }

    internal static object Dto(ImportSnapshot s) => new
    {
        id = s.Id,
        albumId = s.AlbumId is { } a ? Api.Id(a) : null,
        state = !s.Finished ? "running" : s.Cancelled ? "cancelled" : "done",
        startedAt = Api.Iso(s.StartedAt),
        finishedAt = Api.Iso(s.FinishedAt),
        total = s.Total,
        done = s.Done,
        counts = Counts(s.Counts),
        items = s.Items.Select(i => new
        {
            source = i.Source,
            kind = i.Result.IsFolder ? ImportKind.Folder : i.Kind,
            outcome = i.Result.Outcome,
            reason = i.Result.Reason,
            assetId = i.Result.ContentId is { } c ? Api.Id(c) : null,
            path = i.Result.Path,
            rootId = i.Result.RootId,
            pictures = i.Result.Pictures,
        }),
    };

    internal static object Dto(ImportRun r) => new
    {
        id = r.ImportId,
        albumId = r.AlbumId is { } a ? Api.Id(a) : null,
        state = r.FinishedAt is null ? "running" : r.Cancelled ? "cancelled" : "done",
        startedAt = Api.Iso(r.StartedAt),
        finishedAt = Api.Iso(r.FinishedAt),
        total = r.Total,
        done = r.Done,
        counts = Counts(new ImportCounts(r.Added, r.Already, r.Copied, r.Skipped, r.Folders)),
        items = Array.Empty<object>(),
    };

    private static object Counts(ImportCounts c) => new
    {
        added = c.Added,
        already = c.Already,
        copied = c.Copied,
        skipped = c.Skipped,
        folders = c.Folders,
    };
}
