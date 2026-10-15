using Microsoft.Extensions.Options;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Notices folders that appear or change under a root, and queues them for a scan.
/// </summary>
/// <remarks>
/// <para>
/// A person copies a folder into their library and expects an album. The
/// watch never walks the library for this; it reads directory metadata, and
/// as little of it as possible:
/// </para>
/// <list type="bullet">
/// <item><b>One stat per root per tick</b> on the parent folder. NTFS and
/// APFS move a directory's mtime when a child entry is added, removed or
/// renamed, and nothing else does - a copied-in folder keeps its own old
/// mtime, but its parent's moves. When it has, one names-only listing says
/// which folders are new.</item>
/// <item><b>A few hundred child stats per tick</b>, round-robin, for changes
/// INSIDE folders the watch already knows.</item>
/// </list>
/// <para>
/// A changed folder is scanned once it has <b>settled</b> - its entry count
/// unchanged across a settle period, so a copy in progress is not hashed
/// half-written. There is no size above which it waits for a person: the
/// buyer chose the folder, so the scan starts itself and its progress is on
/// the Library page. The scan makes the album
/// (<see cref="FolderRepository.ReconcileAlbumsAsync"/>) and queues the
/// thumbnails.
/// </para>
/// </remarks>
public sealed class LibraryWatch(
    FolderRepository folders,
    ScanRunner scans,
    IOptions<WatchOptions> options,
    ILogger<LibraryWatch> log) : BackgroundService
{
    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
    };

    /// <summary>When the watch last looked, for the Library page.</summary>
    public DateTimeOffset? LastTick { get; private set; }

    /// <summary>True while the loop is alive.</summary>
    public bool Running { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        Running = true;
        try
        {
            await Task.Delay(options.Value.InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                    LastTick = DateTimeOffset.UtcNow;
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
        finally
        {
            Running = false;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var opts = options.Value;

        foreach (var root in await folders.RootsAsync(ct))
        {
            // A missing drive looks exactly like an empty folder. The scanner
            // refuses to walk one; the watch refuses to believe one.
            var parentAbs = root.Parent.Length == 0 ? root.AbsPath : PathRules.Canonical($"{root.AbsPath}/{root.Parent}");
            if (!Directory.Exists(root.AbsPath) || !Directory.Exists(parentAbs))
            {
                continue;
            }

            await NoticeNewFoldersAsync(root, parentAbs, ct);
            await SweepAsync(root, opts.SweepPerTick, ct);
        }

        await ScanSettledAsync(opts, ct);
    }

    /// <summary>The parent's mtime moved: list its children by name and record the ones not seen before.</summary>
    private async Task NoticeNewFoldersAsync(FolderRepository.WatchedRoot root, string parentAbs, CancellationToken ct)
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
    private async Task SweepAsync(FolderRepository.WatchedRoot root, int take, CancellationToken ct)
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

    /// <summary>Queue a scan for every changed folder that has settled.</summary>
    private async Task ScanSettledAsync(WatchOptions opts, CancellationToken ct)
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

            if (started >= opts.MaxScansPerTick)
            {
                break;
            }

            if (await scans.EnqueueAsync(row.RootId, row.Folder, ct) is { } runId)
            {
                log.WatchScanning(root.Label, row.Folder, entries, runId);
                started++;
            }
        }
    }

    /// <summary>
    /// A directory's mtime at the precision the database keeps (milliseconds).
    /// Compared against a stored value, so both sides must round the same way
    /// or every folder looks changed every sweep.
    /// </summary>
    private static DateTimeOffset Mtime(string abs)
    {
        var ticks = Directory.GetLastWriteTimeUtc(abs).Ticks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
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
    public bool Enabled { get; set; } = true;

    /// <summary>Let the host, the migrations and the first scans settle before looking.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the watch looks. One stat per root plus a few hundred child stats.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a changed folder's entry count must hold still before it is scanned.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Child folders stat-ed per root per tick, round-robin.</summary>
    public int SweepPerTick { get; set; } = 300;

    public int MaxScansPerTick { get; set; } = 10;
}
