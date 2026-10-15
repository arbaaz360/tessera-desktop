using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

public static class MediaEndpoints
{
    /// <summary>
    /// Serving the pixels: thumbnails and the original bytes.
    /// </summary>
    /// <remarks>
    /// Everything here is addressed by CONTENT, so a URL stays valid when the
    /// file moves. In the system this replaces the thumbnail URL carried the
    /// asset id, so a move produced a new id, a new thumbnail, and an orphan -
    /// 340,739 of them.
    /// </remarks>
    public sealed record ArchiveBody(string[] AssetIds);

    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        MapWorkerMedia(app);
        MapArchive(app);

        var g = app.MapGroup("/api/media").WithTags("media").RequireUser();

        g.MapGet("/{contentId:long}/{kind}", async (
            long contentId, string kind, CurrentUser me, ViewableContentRepository content,
            IOptions<StorageOptions> storage, HttpContext ctx, CancellationToken ct) =>
        {
            if (kind is not "thumb")
            {
                return Results.NotFound();
            }

            var row = await content.GetAsync(me.AccountIdOrThrow, contentId, ct);
            if (row is null)
            {
                return Results.NotFound();
            }

            var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, row.Value.Sha, kind, "webp");
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            // The hash IS the version: if the bytes change, the URL changes, so
            // this can be cached hard and for ever.
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            ctx.Response.Headers.ETag = $"\"{Hashing.ToHex(row.Value.Sha)}-{kind}\"";
            return Results.File(path, "image/webp");
        });

        g.MapGet("/{contentId:long}/original", async (
            long contentId, CurrentUser me, ViewableContentRepository content,
            HttpContext ctx, CancellationToken ct) =>
        {
            var row = await content.GetAsync(me.AccountIdOrThrow, contentId, ct);
            if (row is null || row.Value.AbsolutePath is null)
            {
                return Results.NotFound();
            }

            if (!File.Exists(row.Value.AbsolutePath))
            {
                // The database says it is here and the disk disagrees. That is
                // drift worth surfacing, not a 500.
                return Results.Problem(statusCode: StatusCodes.Status410Gone,
                                       title: "The file has moved or been removed since the last scan");
            }

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(row.Value.AbsolutePath, row.Value.Mime, enableRangeProcessing: true);
        });
    }


    /// <summary>
    /// The bytes, for a worker holding a token Tessera issued for exactly this
    /// content (see <see cref="MediaTokens"/>). No session, no account: the
    /// token is the whole authorisation, and it names one file.
    /// </summary>
    /// <remarks>
    /// Range requests are honoured so a worker can sample twelve seconds of a
    /// two-gigabyte video without pulling the file. Never cached: the URL is
    /// short-lived by design.
    /// </remarks>
    private static void MapWorkerMedia(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/worker-media/{contentId:long}/original", async (
            long contentId, string? token,
            MediaTokens tokens, ViewableContentRepository content, HttpContext ctx, CancellationToken ct) =>
        {
            if (!tokens.IsValid(token, contentId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                                       title: "The token is not valid for this content, or has expired");
            }

            if (await content.AnyLivePathAsync(contentId, ct) is not { } file || !File.Exists(file.AbsolutePath))
            {
                return Results.Problem(statusCode: StatusCodes.Status410Gone,
                                       title: "No live copy of this content on disk");
            }

            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(file.AbsolutePath, file.Mime, enableRangeProcessing: true);
        }).WithTags("workers");
    }

    /// <summary>
    /// A zip of the selected originals, streamed as it is built. Stored, not
    /// compressed: JPEGs and MP4s do not shrink, and deflating a gigabyte of
    /// them would only heat the CPU. Immich's route and body shape, because
    /// that is what the web app's SDK sends.
    /// </summary>
    private static void MapArchive(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/download/archive", async (
            ArchiveBody body, CurrentUser me, ViewableContentRepository content, HttpContext ctx, CancellationToken ct) =>
        {
            var files = new List<(string Name, string Path)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in body.AssetIds.Distinct())
            {
                if (!ImmichId.TryParse(raw, out var cid) ||
                    await content.GetAsync(me.AccountIdOrThrow, cid, ct) is not { AbsolutePath: { } abs } ||
                    !File.Exists(abs))
                {
                    continue;
                }

                // Two files with one name (the same picture twice, two folders'
                // "IMG_0001.jpg") get a suffix rather than overwriting.
                var name = Path.GetFileName(abs);
                var candidate = name;
                for (var n = 2; !seen.Add(candidate); n++)
                {
                    candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
                }

                files.Add((candidate, abs));
            }

            if (files.Count == 0)
            {
                return Results.NotFound();
            }

            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=tessera-{DateTime.UtcNow:yyyy-MM-dd}.zip";

            // ZipArchive writes headers and data descriptors synchronously and
            // Kestrel forbids synchronous writes on the response. So the zip is
            // built on a worker thread into a Pipe - whose stream accepts sync
            // writes and blocks when the reader is behind (back-pressure, 4 MB
            // in flight) - while this request thread copies the pipe to the
            // response asynchronously. Nothing is buffered to disk or memory.
            var pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(
                pauseWriterThreshold: 4 * 1024 * 1024, resumeWriterThreshold: 1024 * 1024));
            var writing = Task.Run(() =>
            {
                try
                {
                    using var zip = new System.IO.Compression.ZipArchive(
                        pipe.Writer.AsStream(leaveOpen: true), System.IO.Compression.ZipArchiveMode.Create,
                        leaveOpen: true, entryNameEncoding: System.Text.Encoding.UTF8);
                    foreach (var (name, path) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
                        entry.LastWriteTime = File.GetLastWriteTimeUtc(path);
                        using var source = File.OpenRead(path);
                        using var target = entry.Open();
                        source.CopyTo(target);
                    }
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                    return;
                }

                pipe.Writer.Complete();
            }, ct);

            await pipe.Reader.CopyToAsync(ctx.Response.Body, ct);
            await writing;
            return Results.Empty;
        }).WithTags("media").RequireUser();
    }
}
