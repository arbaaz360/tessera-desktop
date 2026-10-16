using System.Diagnostics;
using Microsoft.Extensions.Options;
using Tessera.Api.Media;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>What one backfill pass did.</summary>
public readonly record struct BackfillPass(long Read, long Retimed, long Unreadable);

/// <summary>
/// Reads the headers of content indexed before the scanner read them at first
/// sight, so an existing library moves from file times to capture dates.
/// </summary>
/// <remarks>
/// <para>
/// RESUMABLE WITHOUT A CURSOR. What is left is a query, not a position:
/// contents whose <c>meta_version</c> is below the reader's
/// (<see cref="CaptureDates.ReaderVersion"/>). Each batch is written in one
/// transaction that stamps every content it read, so a restart picks up at
/// the first unstamped one and nothing is read twice. The partial index on
/// that column is the size of the backlog and empty once it is done, so the
/// idle check costs nothing.
/// </para>
/// <para>
/// A date only replaces a worse one, and the trigger on
/// <c>content.taken_at</c> moves the timeline row: no code here touches the
/// timeline. A content whose file cannot be read now - its root unmounted,
/// the file changed under it - stays pending, is counted as unreadable, and
/// is tried again on a later pass; the scan of that root will set it right
/// first if the file really changed.
/// </para>
/// <para>
/// It reads the library disk, so it takes the same lock as the scanner and
/// the thumbnail worker, a batch at a time, and steps aside when a scan was
/// refused the disk.
/// </para>
/// </remarks>
public sealed class HeaderBackfill(
    ContentRepository content,
    ExclusiveWork exclusive,
    IOptions<ScannerOptions> options,
    ILogger<HeaderBackfill> log) : BackgroundService
{
    private const int BatchSize = 200;
    private const string Holder = "headers";
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);

    /// <summary>After a pass that could read nothing, there is no point asking the same absent drive every minute.</summary>
    private static readonly TimeSpan AfterNothingReadable = TimeSpan.FromMinutes(10);

    /// <summary>How long a date count is reused. Counting 200,000 contents by source costs about 120 ms.</summary>
    private static readonly TimeSpan CountsFor = TimeSpan.FromSeconds(10);

    private long _unreadable;
    private long _batches;
    private (DateCounts Counts, long At, DateTimeOffset? ScanFinished, long Batches)? _counts;

    /// <summary>Contents the latest pass could not read (root not mounted, file changed); retried later.</summary>
    public long Unreadable => Interlocked.Read(ref _unreadable);

    /// <summary>
    /// Where the timeline's dates come from. The Library page polls every two
    /// seconds while a scan runs, and the count is one pass over every
    /// content, so while things change it is taken at most every
    /// <see cref="CountsFor"/>; once a scan finishes or a backfill batch
    /// lands it is taken fresh, so a settled library never shows a stale one.
    /// </summary>
    /// <param name="latestScanFinished">The newest finished scan run, from the caller's own read of them.</param>
    public async Task<DateCounts> CountsAsync(DateTimeOffset? latestScanFinished, CancellationToken ct)
    {
        var batches = Interlocked.Read(ref _batches);
        if (_counts is { } c && c.ScanFinished == latestScanFinished && c.Batches == batches &&
            Stopwatch.GetElapsedTime(c.At) < CountsFor)
        {
            return c.Counts;
        }

        var fresh = await content.DateCountsAsync(ct);
        _counts = (fresh, Stopwatch.GetTimestamp(), latestScanFinished, batches);
        return fresh;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host start and the first scan take the disk.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pass = await PassAsync(stoppingToken);
                await Task.Delay(pass is { Read: 0, Unreadable: > 0 } ? AfterNothingReadable : Idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A loop that dies takes the backfill with it silently.
                log.BackfillFailed(ex);
                await Task.Delay(Idle, stoppingToken);
            }
        }
    }

    /// <summary>One walk over everything pending, in content id order.</summary>
    public async Task<BackfillPass> PassAsync(CancellationToken ct)
    {
        var zone = options.Value.LocalZone;
        var sw = Stopwatch.StartNew();
        long cursor = 0, read = 0, retimed = 0, unreadable = 0;

        while (true)
        {
            var batch = await content.MetadataPendingAsync(cursor, BatchSize, ct);
            if (batch.Count == 0)
            {
                break;
            }

            cursor = batch[^1].ContentId;

            if (exclusive.Wanted)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }

            var reads = new List<MetadataRead>(batch.Count);
            await using (var disk = await exclusive.TryTakeArchiveAsync(Holder, TimeSpan.FromSeconds(30), ct))
            {
                if (disk is null)
                {
                    // A scan holds the disk; it reads headers itself. Later.
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                var mounted = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var p in batch)
                {
                    if (!mounted.TryGetValue(p.RootPath, out var present))
                    {
                        mounted[p.RootPath] = present = Directory.Exists(p.RootPath);
                    }

                    if (present && Read(p, zone, now) is { } r)
                    {
                        reads.Add(r);
                    }
                    else
                    {
                        unreadable++;
                    }
                }
            }

            retimed += await content.ApplyMetadataAsync(reads, ct);
            read += reads.Count;
            Interlocked.Increment(ref _batches);
        }

        Interlocked.Exchange(ref _unreadable, unreadable);
        if (read > 0 || unreadable > 0)
        {
            log.BackfillPass(read, retimed, unreadable, sw.ElapsedMilliseconds);
        }

        return new BackfillPass(read, retimed, unreadable);
    }

    /// <summary>The headers of one pending content, or null when its file cannot be read as those bytes now.</summary>
    private static MetadataRead? Read(MetadataPending p, TimeZoneInfo zone, DateTimeOffset now)
    {
        var abs = PathRules.Canonical($"{p.RootPath}/{p.RelPath}");
        try
        {
            // A different size is different bytes: the next scan re-hashes
            // it, and reading these headers into that content would be wrong.
            var info = new FileInfo(abs);
            if (!info.Exists || info.Length != p.ByteSize)
            {
                return null;
            }

            var h = MediaHeaders.Read(abs, zone, now);

            // The file time it has is the one the scanner gave it at first
            // sight; only a header or a name can improve on that.
            var (at, src) = h.TakenAt is { } t
                ? (t, TakenSource.Exif)
                : CaptureDates.FromFileName(p.RelPath, zone, now) is { } n
                    ? (n, TakenSource.FileName)
                    : ((DateTimeOffset?)null, (string?)null);

            return new MetadataRead(p.ContentId, at, src, h.Width, h.Height, h.DurationMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
