using Dapper;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Turns pending work items into thumbnail files on disk.
/// </summary>
/// <remarks>
/// <para>
/// The output path is DERIVED from the content hash, never stored. A stored
/// path is a second copy of a fact that drifts from the first; 340,739 orphaned
/// thumbnails in the previous system are what that costs.
/// </para>
/// <para>
/// Work is leased rather than claimed: a row is marked running with a deadline,
/// so a worker that dies mid-job releases it instead of parking it for ever.
/// </para>
/// </remarks>
public sealed class ThumbnailWorker(
    Db db,
    WorkItems queue,
    IServiceProvider services,
    ExclusiveWork exclusive,
    IOptions<StorageOptions> storage,
    ILogger<ThumbnailWorker> log) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before touching the disk.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var did = await DrainAsync(stoppingToken);
                if (did == 0)
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
                // A worker loop that dies takes the queue with it silently,
                // which is exactly the failure this project exists to avoid.
                log.ThumbnailLoopFailed(ex);
                await Task.Delay(Idle, stoppingToken);
            }
        }
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        // Paused means paused. Checked before leasing, so a pause does not
        // strand 32 items in 'running' until their lease expires.
        await using var scope = services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        if (await jobs.IsPausedAsync(JobNames.Thumbnails, ct))
        {
            return 0;
        }

        // Rendering a thumbnail reads the original off the archive disk, which
        // is the same disk a library scan walks. They do not share it well, so
        // whichever asks first gets it and the other waits - enforced by a lock
        // rather than by everyone remembering the rule. And because this loop
        // would otherwise re-take the disk the instant it let go, it steps
        // aside when a scan has been refused recently: the scan is the one
        // that puts new pictures in front of a person.
        if (exclusive.Wanted)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        await using var disk = await exclusive.TryTakeArchiveAsync(JobNames.Thumbnails, ct);
        if (disk is null)
        {
            return 0;
        }

        // Lease a batch proportional to how many will be worked at once, so a
        // pause or a crash never holds far more items than are actually being
        // rendered.
        var readers = await jobs.ConcurrencyAsync(JobNames.Thumbnails, 1, ct);
        var batch = await queue.LeaseAsync(JobNames.ThumbnailKind, readers * 8, Lease, ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = ct },
            async (item, token) =>
        {
            try
            {
                await RenderAsync(item, token);
                await queue.CompleteAsync(item.WorkId, null, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await queue.CompleteAsync(item.WorkId, ex.Message, CancellationToken.None);
                log.ThumbnailFailed(ex, item.ContentId);
            }
        });

        return batch.Count;
    }

    private async Task RenderAsync(Leased job, CancellationToken ct)
    {
        if (!job.Mime.StartsWith("image/", StringComparison.Ordinal))
        {
            // Video posters need a frame extractor, which this service does not
            // own. Recorded as done rather than retried for ever.
            return;
        }

        if (job.Root is null || job.Rel is null)
        {
            throw new FileNotFoundException("Every copy of the original is gone");
        }

        var absPath = PathRules.Canonical($"{job.Root}/{job.Rel}");
        if (!File.Exists(absPath))
        {
            throw new FileNotFoundException("Original is gone", absPath);
        }

        var opts = storage.Value;

        // One derived file per picture: the grid thumbnail. There is no
        // intermediate "preview" size by the owner's rule - the viewer shows
        // the original, and most originals are 1080-1440 px already. A second
        // size would have cost ~150 GB across the library for nothing.
        //
        // Two decoders. Skia handles JPEG, PNG, WebP, GIF, BMP in-process and
        // fast; it has no HEIF, AVIF or TIFF. Those - and anything Skia
        // refuses - go to libvips, which reads everything a phone produces.
        // Same output either way: a 320px WebP and the original's size.
        if (!NeedsVips(job.Mime))
        {
            using var input = File.OpenRead(absPath);
            using var codec = SKCodec.Create(input);
            using var original = codec is null ? null : SKBitmap.Decode(codec);
            if (original is not null)
            {
                await WriteAsync(job, original, "thumb", opts.ThumbSize, opts, ct);
                await RecordAsync(job.ContentId, original.Width, original.Height, opts, ct);
                return;
            }
        }

        var (ow, oh) = await VipsThumbnailAsync(job, absPath, "thumb", opts, ct);
        await RecordAsync(job.ContentId, ow, oh, opts, ct);
    }

    private static bool NeedsVips(string mime) => mime is
        "image/heic" or "image/heif" or "image/heic-sequence" or "image/heif-sequence" or
        "image/avif" or "image/tiff" or "image/x-tiff";

    /// <summary>
    /// The libvips path: <c>vips thumbnail</c> writes the WebP straight to a
    /// temp file, <c>vipsheader</c> reads the sizes. Returns the ORIGINAL's
    /// width and height.
    /// </summary>
    private async Task<(int Width, int Height)> VipsThumbnailAsync(
        Leased job, string absPath, string kind, StorageOptions opts, CancellationToken ct)
    {
        var path = Hashing.DerivedPath(opts.ThumbnailRoot, job.Sha256, kind, "webp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp.webp";

        // "thumbnail" fits the picture inside a square of the given size, so
        // the longest edge lands on it - the same rule Skia's path applies -
        // and never upscales.
        await RunAsync("vips", ["thumbnail", absPath, $"{tmp}[Q={opts.JpegQuality},strip]",
                                opts.ThumbSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "--size", "down"], ct);

        var (ow, oh) = await VipsSizeAsync(absPath, ct);
        // The thumbnail's size follows from the original's and the rule vips
        // applied (fit inside the square, never upscale) - no second decode.
        var scale = Math.Min(1f, (float)opts.ThumbSize / Math.Max(ow, oh));
        var w = Math.Max(1, (int)Math.Round(ow * scale));
        var h = Math.Max(1, (int)Math.Round(oh * scale));
        var size = new FileInfo(tmp).Length;

        File.Move(tmp, path, overwrite: true);
        await RecordThumbnailAsync(job.ContentId, kind, w, h, size, ct);
        return (ow, oh);
    }

    /// <summary>Width and height from the header, one process, no decode.</summary>
    private static async Task<(int Width, int Height)> VipsSizeAsync(string file, CancellationToken ct)
    {
        // "name: 3024x4032 uchar, 3 bands, srgb, heifload" - the size is the
        // first token after the colon.
        var text = await RunAsync("vipsheader", [file], ct);
        var after = text[(text.LastIndexOf(':') + 1)..].Trim();
        var dims = after.Split(' ', 2)[0].Split('x');
        return (int.Parse(dims[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(dims[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"{exe} did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            throw new InvalidOperationException($"{exe} exit {process.ExitCode}: {(err.Length > 200 ? err[..200] : err)}");
        }

        return await stdout;
    }

    private async Task WriteAsync(
        Leased job, SKBitmap source, string kind, int longest, StorageOptions opts, CancellationToken ct)
    {
        var scale = (float)longest / Math.Max(source.Width, source.Height);
        // Never upscale: enlarging a 200px image to 1440 costs disk and looks
        // worse than the original.
        if (scale > 1f)
        {
            scale = 1f;
        }

        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));

        using var resized = source.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Webp, opts.JpegQuality);

        var path = Hashing.DerivedPath(opts.ThumbnailRoot, job.Sha256, kind, "webp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp name and move into place, so a crash mid-write cannot
        // leave a truncated file that looks valid to every later reader.
        var tmp = path + ".tmp";
        await using (var output = File.Create(tmp))
        {
            data.SaveTo(output);
        }

        File.Move(tmp, path, overwrite: true);
        await RecordThumbnailAsync(job.ContentId, kind, w, h, data.Size, ct);
    }

    private async Task RecordThumbnailAsync(long contentId, string kind, int w, int h, long size, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO thumbnail (content_id, kind, format, width, height, byte_size)
            VALUES (@ContentId, @Kind, 'webp', @W, @H, @Size)
            ON CONFLICT (content_id, kind) DO UPDATE
                SET width = EXCLUDED.width, height = EXCLUDED.height,
                    byte_size = EXCLUDED.byte_size, generated_at = now()
            """,
            new { ContentId = contentId, Kind = kind, W = w, H = h, Size = size },
            cancellationToken: ct));
    }

    private async Task RecordAsync(long contentId, int w, int h, StorageOptions _, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE content SET width = COALESCE(width, @W), height = COALESCE(height, @H)
             WHERE content_id = @ContentId
            """,
            new { ContentId = contentId, W = w, H = h },
            cancellationToken: ct));
    }
}
