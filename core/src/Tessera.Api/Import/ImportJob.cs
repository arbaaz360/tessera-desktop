using Tessera.Data.Repositories;

namespace Tessera.Api.Import;

/// <summary>What one dropped or picked thing became.</summary>
public static class ImportOutcome
{
    public const string Pending = "pending";

    /// <summary>A new picture, indexed where it is (inside a root); for a folder, a new library folder.</summary>
    public const string Added = "added";

    /// <summary>The same bytes were in the library already: nothing copied, the picture there is used.</summary>
    public const string Already = "already";

    /// <summary>Copied into the Tessera folder, from outside every root.</summary>
    public const string Copied = "copied";

    /// <summary>Not added, with the reason.</summary>
    public const string Skipped = "skipped";
}

/// <summary>Where an item came from.</summary>
public static class ImportKind
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string Url = "url";
    public const string Upload = "upload";
}

/// <summary>The answer for one item.</summary>
/// <param name="Path">Where the picture is now (the copy, for a copy); for a folder, the folder.</param>
/// <param name="RootId">The library folder it is in.</param>
/// <param name="Pictures">For a folder: its live pictures after the scan.</param>
public sealed record ItemResult(
    string Outcome,
    string? Reason = null,
    long? ContentId = null,
    string? Path = null,
    long? RootId = null,
    long? Pictures = null,
    bool IsFolder = false);

/// <summary>One item of a job, as the web app is shown it.</summary>
public sealed record ImportItemView(string Source, string Kind, ItemResult Result);

/// <summary>A job at one moment.</summary>
public sealed record ImportSnapshot(
    long Id,
    long? AlbumId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Cancelled,
    int Total,
    int Done,
    ImportCounts Counts,
    IReadOnlyList<ImportItemView> Items)
{
    public bool Finished => FinishedAt is not null;
}

/// <summary>
/// One import: the items asked for, and what each became so far. Written by
/// the import worker, read by any request; the lock keeps a snapshot whole.
/// </summary>
public sealed class ImportJob
{
    private readonly Lock _lock = new();
    private readonly ImportItemView[] _items;
    private int _done;
    private ImportCounts _counts;
    private DateTimeOffset? _finishedAt;
    private bool _cancelled;

    public ImportJob(long id, long accountId, long? albumId, string folderName, IReadOnlyList<(string Source, string Kind)> items)
    {
        Id = id;
        AccountId = accountId;
        AlbumId = albumId;
        FolderName = folderName;
        StartedAt = DateTimeOffset.UtcNow;
        _items = items.Select(i => new ImportItemView(i.Source, i.Kind, new ItemResult(ImportOutcome.Pending))).ToArray();
    }

    public long Id { get; }
    public long AccountId { get; }
    public long? AlbumId { get; }

    /// <summary>The folder under the Tessera folder that copies go to: the album's name, or "Unsorted".</summary>
    public string FolderName { get; }

    public DateTimeOffset StartedAt { get; }

    public int Total => _items.Length;

    public ImportItemView Item(int index)
    {
        lock (_lock)
        {
            return _items[index];
        }
    }

    /// <summary>Record an item's answer and count it.</summary>
    public (int Done, ImportCounts Counts) Record(int index, ItemResult result)
    {
        lock (_lock)
        {
            _items[index] = _items[index] with { Result = result };
            _done++;
            var c = _counts;
            _counts = result switch
            {
                { Outcome: ImportOutcome.Skipped } => c with { Skipped = c.Skipped + 1 },
                { IsFolder: true } => c with { Folders = c.Folders + 1 },
                { Outcome: ImportOutcome.Added } => c with { Added = c.Added + 1 },
                { Outcome: ImportOutcome.Copied } => c with { Copied = c.Copied + 1 },
                _ => c with { Already = c.Already + 1 },
            };
            return (_done, _counts);
        }
    }

    public void Finish(bool cancelled)
    {
        lock (_lock)
        {
            _finishedAt = DateTimeOffset.UtcNow;
            _cancelled = cancelled;
        }
    }

    public bool Finished
    {
        get
        {
            lock (_lock)
            {
                return _finishedAt is not null;
            }
        }
    }

    public ImportSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ImportSnapshot(Id, AlbumId, StartedAt, _finishedAt, _cancelled, _items.Length, _done, _counts, _items.ToArray());
        }
    }
}
