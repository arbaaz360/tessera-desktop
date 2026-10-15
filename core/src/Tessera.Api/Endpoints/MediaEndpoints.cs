using Microsoft.Extensions.Options;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Serving the pixels under Tessera's own names, and the zip download.
/// </summary>
/// <remarks>
/// Everything here is addressed by CONTENT, so a URL stays valid when the
/// file moves. In the system this replaces the thumbnail URL carried the
/// asset id, so a move produced a new id, a new thumbnail, and an orphan -
/// 340,739 of them.
/// </remarks>
public static class MediaEndpoints
{
    public sealed record ArchiveBody(string[] AssetIds);

    public static void MapMedia(this IEndpointRouteBuilder app)
    {
        MapArchive(app);

        var g = app.MapGroup("/api/media").WithTags("media").RequireUser();

        g.MapGet("/{contentId:long}/{kind}", async (
            long contentId, string kind, CurrentUser me, ViewableContentRepository content,
            IOptions<StorageOptions> storage, HttpContext ctx, CancellationToken ct) =>
        {
            if (kind is not "thumb")
            {
                return Api.NotFound();
            }

            var row = await content.GetAsync(me.AccountIdOrThrow, contentId, ct);
            if (row is null)
            {
                return Api.NotFound();
            }

            var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, row.Value.Sha, kind, "webp");
            if (!File.Exists(path))
            {
                return Api.NotFound("No thumbnail yet");
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
                return Api.NotFound();
            }

            if (!File.Exists(row.Value.AbsolutePath))
            {
                return Api.Fail(StatusCodes.Status410Gone, "The file has moved or been removed since the last scan");
            }

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(row.Value.AbsolutePath, row.Value.Mime, enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// A zip of the selected originals, streamed as it is built. Stored, not
    /// compressed: JPEGs and MP4s do not shrink. Immich's route and body
    /// shape, because that is what the web app sends.
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
                if (!Api.TryParseId(raw, out var cid) ||
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
                return Api.NotFound("None of those pictures has a file on disk");
            }

            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=tessera-{DateTime.UtcNow:yyyy-MM-dd}.zip";

            // ZipArchive writes synchronously and Kestrel forbids synchronous
            // writes on the response. So the zip is built on a worker thread
            // into a Pipe - whose stream accepts sync writes and blocks when
            // the reader is behind (4 MB in flight) - while this request
            // thread copies the pipe to the response asynchronously.
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
