namespace Tessera.Core.Domain;

/// <summary>
/// A distinct set of bytes. The identity of a picture in this system.
/// </summary>
/// <remarks>
/// Nothing here changes when a file moves, which is the whole design. Immich
/// identified an asset by its path, so a move minted a new asset and abandoned
/// everything derived from the old one.
/// </remarks>
public sealed record Content
{
    public long ContentId { get; init; }
    public required byte[] Sha256 { get; init; }
    public required long ByteSize { get; init; }
    public required string Mime { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationMs { get; init; }
    public DateTimeOffset? TakenAt { get; init; }
    public string? TakenSrc { get; init; }
}

/// <summary>Where a content's bytes currently sit on disk.</summary>
public sealed record Location
{
    public long LocationId { get; init; }
    public required long ContentId { get; init; }
    public required long AccountId { get; init; }
    public required long RootId { get; init; }
    public required string RelPath { get; init; }
    public DateTimeOffset? FileMtime { get; init; }
    public DateTimeOffset? TrashedAt { get; init; }
    public DateTimeOffset? GoneAt { get; init; }
}

/// <summary>
/// The one account. Every query is scoped to it, so repository code keeps its
/// <c>accountId</c> parameters and a second account is a data change, not a
/// rewrite (CONTRACT §2).
/// </summary>
public sealed record Account
{
    public const long BuiltInId = 1;

    public long AccountId { get; init; }
    public required string DisplayName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The row the schema seeds: id 1.</summary>
    public static Account BuiltIn { get; } = new() { AccountId = BuiltInId, DisplayName = "Library" };
}

public sealed record Album
{
    public long AlbumId { get; init; }
    public required long AccountId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public long? CoverContentId { get; init; }

    /// <summary>What to draw: the choice if there is one, else a stable live member.</summary>
    public long? ShownCoverContentId { get; init; }

    /// <summary>Live members: pictures with a location neither trashed nor gone.</summary>
    public int ItemCount { get; init; }

    /// <summary>"manual" (a person's) or "folder" (the folder rule's).</summary>
    public string Origin { get; init; } = AlbumOrigin.Manual;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Who made an album. Only the folder rule's are ever deleted by the app.</summary>
public static class AlbumOrigin
{
    public const string Manual = "manual";
    public const string Folder = "folder";
}

public sealed record ScanRoot
{
    public long RootId { get; init; }
    public required long AccountId { get; init; }
    public required string Label { get; init; }
    public required string AbsPath { get; init; }
    public bool Enabled { get; init; } = true;
    public string[] Exclude { get; init; } = [];

    /// <summary>
    /// Where this root's album folders sit, relative to the root: "" for the
    /// root's own children (the default: folders are albums), null for none.
    /// </summary>
    public string? AlbumFolder { get; init; } = "";

    /// <summary>A folder the app made for itself (the Tessera folder): the one place it writes.</summary>
    public bool AppOwned { get; init; }

    /// <summary>
    /// Other roots inside this one, as canonical absolute paths. Only an
    /// app-owned folder may sit inside a person's root; the scan and the
    /// watch leave it to its own root, so nothing is indexed twice.
    /// </summary>
    public string[] Nested { get; init; } = [];
}

/// <summary>
/// What one scan actually did. Recorded because the failures this system
/// replaces were silent: containers reported healthy for eighteen days while
/// every run died on startup.
/// </summary>
public sealed record ScanRun
{
    public long RunId { get; init; }
    public required long RootId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long FilesSeen { get; init; }
    public long Added { get; init; }
    public long Moved { get; init; }
    public long Copies { get; init; }
    public long Unchanged { get; init; }
    public long MarkedGone { get; init; }
    public long Errors { get; init; }
    public string? ErrorSample { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>One page of the photo grid.</summary>
public sealed record TimelinePage
{
    public required IReadOnlyList<TimelineItem> Items { get; init; }
    public DateTimeOffset? NextBefore { get; init; }
    public long? NextContentId { get; init; }
}

public sealed record TimelineItem
{
    public required long ContentId { get; init; }
    public required DateTimeOffset TakenAt { get; init; }
    public required string Sha256Hex { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public required string Mime { get; init; }
}

/// <summary>Bytes and counts for one album.</summary>
public sealed record AlbumStats(long Bytes, long Total, long Images, long Videos);
