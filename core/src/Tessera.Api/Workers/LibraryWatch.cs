using Microsoft.Extensions.Options;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Notices folders that appear or change under a root, and scans the small
/// ones itself.
/// </summary>
/// <remarks>
/// <para>
/// A person copies a folder into their library and expects an album. Immich
/// walked the whole library on a timer for this; here a root is half a
/// million files on an archive disk, so the watch never walks. It reads
/// directory metadata, and as little of it as possible:
/// </para>
/// <list type="bullet">
/// <item><b>One stat per root per tick</b> on the parent folder. NTFS moves a
/// directory's mtime when a child entry is added, removed or renamed, and
/// nothing else does - a copied-in folder keeps its own old mtime, but its
/// parent's moves. When it has, one names-only listing (37 ms for 10,636
/// entries) says which folders are new.</item>
/// <item><b>A few hundred child stats per tick</b>, round-robin, for changes
/// INSIDE folders the watch already knows. Each stat over the bind mount is
/// 4-8 ms, so all ten thousand at once would be a minute of I/O every tick;
/// spread out, a change inside an existing folder is noticed within a few
/// hours, and the album's own index button is the immediate path.</item>
/// </list>
/// <para>
/// A changed folder is scanned once it has <b>settled</b> - its entry count
/// unchanged across a settle period - and only if it is small enough to
/// scan unasked (<see cref="WatchOptions.AutoScanMaxEntries"/>). Anything
/// bigger waits for a person, visibly (<c>/api/scan/folders</c>): the house
/// rule is that heavy work is queued by tools and started by a human, and a
/// folder of forty thousand files is heavy. The scan itself makes the album
/// (<see cref="FolderRepository.ReconcileAlbumsAsync"/>) and queues the
/// thumbnails. Faces and vectors are not started from here - they are GPU
/// work a person starts, from the album page.
/// </para>
/// </remarks>
public sealed class LibraryWatch(
    IServiceProvider services,
    Scanner scanner,
    IOptions<WatchOptions> options,
    ILogger<LibraryWatch> log) : BackgroundService
{
    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.WatchTickFailed(ex);
            }

            await Task.Delay(options.Value.Every, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var folders = scope.ServiceProvider.GetRequiredService<FolderRepository>();
        var opts = options.Value;

        foreach (var root in await folders.RootsAsync(ct))
        {
            // A missing mount looks exactly like an empty folder. The scanner
            // refuses to walk one; the watch refuses to believe one.
            var parentAbs = root.Parent.Length == 0 ? root.AbsPath : PathRules.Canonical($"{root.AbsPath}/{root.Parent}");
            if (!Directory.Exists(root.AbsPath) || !Directory.Exists(parentAbs))
            {
                continue;
            }

            await NoticeNewFoldersAsync(folders, root, parentAbs, ct);
            await SweepAsync(folders, root, opts.SweepPerTick, ct);
        }

        // The jobs page's "Library scans" pause. Noticing folders is metadata
        // and always runs; STARTING scans is what a person pauses - the
        // folders keep collecting on the page until it is resumed.
        var jobs = scope.ServiceProvider.GetRequiredService<JobRepository>();
        if (await jobs.IsPausedAsync(JobNames.LibraryScan, ct))
        {
            return;
        }

        await ScanSettledAsync(folders, opts, ct);
    }

    /// <summary>The parent's mtime moved: list its children by name and record the ones not seen before.</summary>
    private async Task NoticeNewFoldersAsync(FolderRepository folders, FolderRepository.WatchedRoot root, string parentAbs, CancellationToken ct)
    {
        var parentMtime = Mtime(parentAbs);
        var parentRow = await folders.GetAsync(root.RootId, "", ct);
        var known = await folders.KnownFoldersAsync(root.RootId, ct);

        if (parentRow is null && known.Count == 0)
        {
            var names = ChildFolders(parentAbs, root.Exclude);
            var news = await folders.BootstrapAsync(root.RootId, root.Parent, names, ct);
            await folders.ObservedAsync(root.RootId, "", parentMtime, names.Count, changed: false, ct);
            log.WatchBootstrapped(root.Label, names.Count, news);
            return;
        }

        if (parentRow?.Mtime == parentMtime)
        {
            return;
        }

        var current = ChildFolders(parentAbs, root.Exclude);
        var currentSet = current.Select(n => FolderRepository.Join(root.Parent, n)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in current)
        {
            var folder = FolderRepository.Join(root.Parent, name);
            if (known.Contains(folder))
            {
                continue;
            }

            var entries = CountEntries(PathRules.Canonical($"{root.AbsPath}/{folder}"));
            await folders.NoticeAsync(root.RootId, folder, entries, ct);
            log.WatchNoticed(root.Label, folder, entries);
        }

        foreach (var folder in known.Where(k => !currentSet.Contains(k)))
        {
            // The folder is gone. Its files are marked gone by the next scan
            // of the root - a scoped scan cannot walk what is not there.
            await folders.ForgetAsync(root.RootId, folder, ct);
            log.WatchFolderGone(root.Label, folder);
        }

        await folders.ObservedAsync(root.RootId, "", parentMtime, current.Count, changed: false, ct);
    }

    /// <summary>A few hundred stats on folders the watch already knows, least recently looked at first.</summary>
    private async Task SweepAsync(FolderRepository folders, FolderRepository.WatchedRoot root, int take, CancellationToken ct)
    {
        foreach (var row in await folders.SweepBatchAsync(root.RootId, take, ct))
        {
            var abs = PathRules.Canonical($"{root.AbsPath}/{row.Folder}");
            if (!Directory.Exists(abs))
            {
                await folders.ForgetAsync(root.RootId, row.Folder, ct);
                log.WatchFolderGone(root.Label, row.Folder);
                continue;
            }

            var mtime = Mtime(abs);
            // The first stat of a bootstrapped folder records its mtime; only
            // a LATER difference is a change.
            var changed = row.Mtime is { } seen && seen != mtime;
            await folders.ObservedAsync(root.RootId, row.Folder, mtime, changed ? CountEntries(abs) : null, changed, ct);
            if (changed)
            {
                log.WatchNoticed(root.Label, row.Folder, null);
            }
        }
    }

    /// <summary>Scan the changed folders that have settled and are small enough; leave the rest visible.</summary>
    private async Task ScanSettledAsync(FolderRepository folders, WatchOptions opts, CancellationToken ct)
    {
        var roots = (await folders.RootsAsync(ct)).ToDictionary(r => r.RootId);
        var started = 0;

        foreach (var row in await folders.ChangedAsync(null, ct))
        {
            if (!roots.TryGetValue(row.RootId, out var root))
            {
                continue;
            }

            var abs = PathRules.Canonical($"{root.AbsPath}/{row.Folder}");
            if (!Directory.Exists(abs))
            {
                await folders.ForgetAsync(row.RootId, row.Folder, ct);
                continue;
            }

            // Settling: the entry count must hold still for the whole settle
            // period, so a copy in progress is not hashed half-written.
            var entries = CountEntries(abs);
            if (row.Entries != entries)
            {
                await folders.StillChangingAsync(row.RootId, row.Folder, entries, ct);
                continue;
            }

            if (DateTimeOffset.UtcNow - row.ChangedAt < opts.Settle)
            {
                continue;
            }

            if (entries == 0)
            {
                // Nothing to hash and nothing to make an album of. In step.
                await folders.ScannedAsync(row.RootId, row.Folder, ct);
                continue;
            }

            if (entries > opts.AutoScanMaxEntries)
            {
                // Waits for a person. Said once, when it becomes ready.
                if (row.ScannedAt is null || row.ScannedAt < row.ChangedAt)
                {
                    log.WatchWaitingForHuman(root.Label, row.Folder, entries, opts.AutoScanMaxEntries);
                }

                continue;
            }

            if (started >= opts.MaxScansPerTick || scanner.IsRunning)
            {
                break;
            }

            try
            {
                log.WatchScanning(root.Label, row.Folder, entries);
                await scanner.ScanAsync(row.RootId, row.Folder, ct);
                started++;
            }
            catch (InvalidOperationException)
            {
                // Another job holds the archive. Next tick.
                break;
            }
        }
    }

    /// <summary>
    /// A directory's mtime at the precision the database keeps (microseconds).
    /// Compared against a stored value, so both sides must round the same way
    /// or every folder looks changed every sweep.
    /// </summary>
    private static DateTimeOffset Mtime(string abs)
    {
        var ticks = Directory.GetLastWriteTimeUtc(abs).Ticks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    private static List<string> ChildFolders(string parentAbs, string[] exclude)
    {
        var names = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(parentAbs, "*", Shallow))
        {
            if (exclude.Any(x => dir.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            names.Add(Path.GetFileName(dir));
        }

        return names;
    }

    private static int CountEntries(string abs)
    {
        var n = 0;
        foreach (var _ in Directory.EnumerateFileSystemEntries(abs, "*", Shallow))
        {
            n++;
        }

        return n;
    }
}

public sealed class WatchOptions
{
    public const string Section = "Tessera:Watch";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the watch looks. One stat per root plus a few hundred child stats.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a changed folder's entry count must hold still before it is scanned.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The most entries a folder may have and still be scanned unasked. A
    /// scan hashes every new file; two thousand is minutes on the archive
    /// disk, forty thousand is an afternoon and a person's decision.
    /// </summary>
    public int AutoScanMaxEntries { get; set; } = 2000;

    /// <summary>Child folders stat-ed per root per tick, round-robin.</summary>
    public int SweepPerTick { get; set; } = 300;

    public int MaxScansPerTick { get; set; } = 10;
}
