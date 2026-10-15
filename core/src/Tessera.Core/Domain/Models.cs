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

public sealed record Account
{
    public long AccountId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public bool IsAdmin { get; init; }
    public DateTimeOffset? DisabledAt { get; init; }

    /// <summary>"personal" or "corpus". See migration 0003 for what a corpus is denied.</summary>
    public string Kind { get; init; } = "personal";

    public bool IsCorpus => Kind == "corpus";
}

public sealed record Album
{
    public long AlbumId { get; init; }
    public required long AccountId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public long? CoverContentId { get; init; }

    /// <summary>What to draw: the choice if there is one, else a stable member.</summary>
    public long? ShownCoverContentId { get; init; }
    public int ItemCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ScanRoot
{
    public long RootId { get; init; }
    public required long AccountId { get; init; }
    public required string Label { get; init; }
    public required string AbsPath { get; init; }
    public bool Enabled { get; init; } = true;
    public string[] Exclude { get; init; } = [];
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

/// <summary>One of the caller's albums and a same-named album in another personal account.</summary>
public sealed record AlbumOverlap(
    string Name,
    long MyAlbumId, long MyImages, long MyVideos,
    long OtherAccountId, long OtherAlbumId, long OtherImages, long OtherVideos);

/// <param name="AlbumId">The album that now holds the pictures - the source, or the one it merged into.</param>
/// <param name="Merged">Whether a same-named album existed in the destination.</param>
/// <param name="Moved">Memberships that changed hands (a merge counts only those the destination lacked).</param>
public sealed record AlbumMove(long AlbumId, bool Merged, int Moved);
