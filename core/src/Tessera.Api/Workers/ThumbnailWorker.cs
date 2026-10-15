using System.Diagnostics;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>A format this build cannot decode on this platform. Recorded, not retried.</summary>
public sealed class UnsupportedFormatException(string message) : Exception(message);

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
/// Two decoders. Skia handles JPEG, PNG, WebP, GIF, BMP in-process and fast;
/// it has no HEIF, AVIF or TIFF. On macOS those go through the system's
/// <c>sips</c> (to a temporary JPEG, then Skia). Elsewhere they are
/// unsupported, and recorded as such: a failed work item with the reason, a
/// number on the Library page, never a silent hole.
/// </para>
/// <para>
/// A finished thumbnail queues the embedding, because the embedding worker
/// reads the thumbnail rather than the original: one decode of the big file.
/// </para>
/// </remarks>
public sealed class ThumbnailWorker(
    Db db,
    WorkItems queue,
    JobRepository jobs,
    ExclusiveWork exclusive,
    IOptions<StorageOptions> storage,
    ILogger<ThumbnailWorker> log) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    private long _rendered;
    private long _renderMs;

    /// <summary>Thumbnails written since start, and the time spent in them, for the README's numbers.</summary>
    public (long Rendered, long Milliseconds) Rate => (Interlocked.Read(ref _rendered), Interlocked.Read(ref _renderMs));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before touching the disk.
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

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
        if (await jobs.IsPausedAsync(JobNames.Thumbnails, ct))
        {
            return 0;
        }

        // Rendering reads originals off the library disk, which a scan also
        // walks. Whichever asks first gets it and the other waits; and because
        // this loop would otherwise re-take the disk the instant it let go, it
        // steps aside when a scan has been refused recently.
        if (exclusive.Wanted)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        await using var disk = await exclusive.TryTakeArchiveAsync(JobNames.Thumbnails, ct);
        if (disk is null)
        {
            return 0;
        }

        // Lease a batch proportional to how many will be worked at once, so a
        // crash never holds far more items than are actually being rendered.
        var readers = await jobs.ConcurrencyAsync(JobNames.Thumbnails, 4, ct);
        var batch = await queue.LeaseAsync(WorkItems.ThumbnailKind, readers * 8, Lease, ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = ct },
            async (item, token) =>
        {
            try
            {
                await RenderAsync(item, token);
                await queue.CompleteAsync(item.WorkId, null, token);
                await queue.EnqueueAsync(WorkItems.EmbeddingKind, item.ContentId, token);
                Interlocked.Increment(ref _rendered);
            }
            catch (UnsupportedFormatException ex)
            {
                await queue.AbandonAsync(item.WorkId, ex.Message, CancellationToken.None);
                log.ThumbnailUnsupported(item.ContentId, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await queue.CompleteAsync(item.WorkId, ex.Message, CancellationToken.None);
                log.ThumbnailFailed(ex, item.ContentId);
            }
        });

        Interlocked.Add(ref _renderMs, sw.ElapsedMilliseconds);
        log.ThumbnailBatch(batch.Count, sw.ElapsedMilliseconds);
        return batch.Count;
    }

    private async Task RenderAsync(Leased job, CancellationToken ct)
    {
        if (!job.Mime.StartsWith("image/", StringComparison.Ordinal))
        {
            throw new UnsupportedFormatException($"unsupported: {job.Mime} (no frame extractor in this build)");
        }

        if (job.Root is null || job.Rel is null)
        {
            throw new UnsupportedFormatException("every copy of the original is gone");
        }

        var absPath = PathRules.Canonical($"{job.Root}/{job.Rel}");
        if (!File.Exists(absPath))
        {
            throw new FileNotFoundException("Original is gone", absPath);
        }

        var opts = storage.Value;

        // One derived file per picture: the grid thumbnail. The viewer shows
        // the original; a second size would cost disk for nothing.
        if (!NeedsSystemDecoder(job.Mime))
        {
            using var original = DecodeWithSkia(absPath);
            if (original is not null)
            {
                await WriteAsync(job, original, opts, ct);
                await RecordSizeAsync(job.ContentId, original.Width, original.Height, ct);
                return;
            }
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new UnsupportedFormatException(NeedsSystemDecoder(job.Mime)
                ? $"unsupported: {job.Mime} is not decodable on this platform"
                : $"undecodable: {job.Mime} could not be read");
        }

        // macOS: sips reads everything the platform can (HEIC above all),
        // to a temporary JPEG that Skia then resizes like any other.
        var tmp = Path.Combine(Path.GetTempPath(), $"tessera-{Hashing.ToHex(job.Sha256)}.jpg");
        try
        {
            await SipsToJpegAsync(absPath, tmp, ct);
            using var converted = DecodeWithSkia(tmp)
                ?? throw new UnsupportedFormatException($"undecodable: sips produced nothing readable for {job.Mime}");
            await WriteAsync(job, converted, opts, ct);
            await RecordSizeAsync(job.ContentId, converted.Width, converted.Height, ct);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool NeedsSystemDecoder(string mime) => mime is
        "image/heic" or "image/heif" or "image/heic-sequence" or "image/heif-sequence" or
        "image/avif" or "image/tiff" or "image/x-tiff" or "image/jxl" or "image/x-raw";

    private static SKBitmap? DecodeWithSkia(string path)
    {
        using var input = File.OpenRead(path);
        using var codec = SKCodec.Create(input);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    private static async Task SipsToJpegAsync(string source, string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sips")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-s", "format", "jpeg", source, "--out", target })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("sips did not start");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            throw new UnsupportedFormatException($"sips exit {process.ExitCode}: {(err.Length > 200 ? err[..200] : err)}");
        }
    }

    private async Task WriteAsync(Leased job, SKBitmap source, StorageOptions opts, CancellationToken ct)
    {
        var scale = (float)opts.ThumbSize / Math.Max(source.Width, source.Height);
        // Never upscale: enlarging a 200px image costs disk and looks worse
        // than the original.
        if (scale > 1f)
        {
            scale = 1f;
        }

        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));

        using var resized = source.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("Resize failed");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Webp, opts.JpegQuality)
            ?? throw new InvalidOperationException("WebP encode failed");

        var path = Hashing.DerivedPath(opts.ThumbnailRoot, job.Sha256, "thumb", "webp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp name and move into place, so a crash mid-write cannot
        // leave a truncated file that looks valid to every later reader.
        var tmp = path + ".tmp";
        await using (var output = File.Create(tmp))
        {
            data.SaveTo(output);
        }

        File.Move(tmp, path, overwrite: true);
        await RecordThumbnailAsync(job.ContentId, w, h, data.Size, ct);
    }

    private async Task RecordThumbnailAsync(long contentId, int w, int h, long size, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO thumbnail (content_id, kind, format, width, height, byte_size)
            VALUES (@ContentId, 'thumb', 'webp', @W, @H, @Size)
            ON CONFLICT (content_id, kind) DO UPDATE
                SET width = excluded.width, height = excluded.height,
                    byte_size = excluded.byte_size, generated_at = {Db.NowMs}
            """,
            new { ContentId = contentId, W = w, H = h, Size = size },
            cancellationToken: ct));
    }

    private async Task RecordSizeAsync(long contentId, int w, int h, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE content SET width = COALESCE(width, @W), height = COALESCE(height, @H)
             WHERE content_id = @ContentId
            """,
            new { ContentId = contentId, W = w, H = h },
            cancellationToken: ct));
    }

    /// <summary>For logs: a rate as a number, never "fast".</summary>
    public static string PerSecond(long count, long ms) =>
        (ms <= 0 ? 0 : count * 1000.0 / ms).ToString("0.0", CultureInfo.InvariantCulture);
}
