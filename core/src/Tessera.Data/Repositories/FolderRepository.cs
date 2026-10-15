using Dapper;

namespace Tessera.Data.Repositories;

/// <summary>
/// Folders: the albums they imply, and what the watch knows about them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A folder under an account's album folder is an album of that name</b>,
/// and every content located under it is a member. That is the whole rule,
/// it is Tessera's to hold (albums are its table), and it is applied as two
/// set-based statements at the end of any scan that could have changed the
/// answer. Additive only: the rule adds, it never removes - a picture put in
/// an album by hand, or one whose file has since moved, stays where a person
/// or the past left it, because membership is about content and content
/// does not move.
/// </para>
/// <para>
/// The second half is the ledger the library watch keeps
/// (<c>scan_folder</c>): which folders exist, when each was last looked at,
/// and which ones have changed and not yet been scanned. It is a table
/// rather than memory so a restart does not turn every folder into news.
/// </para>
/// </remarks>
public sealed class FolderRepository(Db db)
{
    // Init-property records, not positional: Dapper maps the column aliases
    // onto settable properties, and a positional record's constructor does
    // not take them by that name.
    public sealed record WatchedRoot
    {
        public long RootId { get; init; }
        public long AccountId { get; init; }
        public string Label { get; init; } = "";
        public string AbsPath { get; init; } = "";
        public string? AlbumFolder { get; init; }
        public string[] Exclude { get; init; } = [];

        /// <summary>The folder whose children the watch minds, relative to the root ('' = the root).</summary>
        public string Parent => AlbumFolder ?? "";
    }

    public sealed record FolderRow
    {
        public long RootId { get; init; }
        public string Folder { get; init; } = "";
        public DateTimeOffset? Mtime { get; init; }
        public int? Entries { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        public DateTimeOffset? ChangedAt { get; init; }
        public DateTimeOffset? ScannedAt { get; init; }
    }

    public sealed record Waiting
    {
        public long RootId { get; init; }
        public string RootLabel { get; init; } = "";
        public long AccountId { get; init; }
        public string Folder { get; init; } = "";
        public int? Entries { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset ChangedAt { get; init; }
    }

    // ------------------------------------------------------------ the rule

    /// <summary>
    /// Make the albums a root's folders imply, and fill them. Scoped to one
    /// folder after a folder scan; the whole root after a root scan.
    /// </summary>
    /// <returns>(albums created, memberships added)</returns>
    public async Task<(int Albums, int Members)> ReconcileAlbumsAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // The prefix under which album folders live, with its trailing slash,
        // or '' when they are the root's own children. A root with no album
        // folder implies no albums, and both statements match nothing.
        const string scope = """
            FROM location l
            JOIN scan_root r ON r.root_id = l.root_id
            CROSS JOIN LATERAL (SELECT CASE WHEN r.album_folder = '' THEN '' ELSE r.album_folder || '/' END AS prefix) p
            CROSS JOIN LATERAL (SELECT split_part(substr(l.rel_path, length(p.prefix) + 1), '/', 1) AS name) n
            WHERE l.root_id = @RootId
              AND l.gone_at IS NULL
              AND r.album_folder IS NOT NULL
              AND left(l.rel_path, length(p.prefix)) = p.prefix
              AND position('/' IN substr(l.rel_path, length(p.prefix) + 1)) > 0
              AND (@Folder IS NULL OR left(l.rel_path, length(@Folder) + 1) = @Folder || '/')
            """;

        var albums = await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO album (account_id, name) SELECT DISTINCT r.account_id, n.name " + scope +
            " ON CONFLICT (account_id, name) DO NOTHING",
            new { RootId = rootId, Folder = folder }, commandTimeout: 600, cancellationToken: ct));

        var members = await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO album_item (album_id, content_id) SELECT a.album_id, l.content_id " +
            scope.Replace("WHERE l.root_id", "JOIN album a ON a.account_id = r.account_id AND a.name = n.name WHERE l.root_id") +
            " ON CONFLICT DO NOTHING",
            new { RootId = rootId, Folder = folder }, commandTimeout: 600, cancellationToken: ct));

        return (albums, members);
    }

    /// <summary>
    /// Queue thumbnails for what a scan just brought in, under one folder of
    /// one root. Personal accounts only - a corpus has none on purpose.
    /// </summary>
    public async Task<int> QueueThumbnailsUnderAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT DISTINCT 'thumbnail', l.content_id
              FROM location l
              JOIN scan_root r ON r.root_id = l.root_id
              JOIN account a  ON a.account_id = r.account_id
             WHERE l.root_id = @RootId AND l.gone_at IS NULL
               AND a.kind <> 'corpus'
               AND (@Folder IS NULL OR left(l.rel_path, length(@Folder) + 1) = @Folder || '/')
               AND NOT EXISTS (SELECT 1 FROM thumbnail t WHERE t.content_id = l.content_id AND t.kind = 'thumb')
               AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = l.content_id AND w.kind = 'thumbnail'
                                                          AND w.state IN ('pending', 'running'))
            ON CONFLICT DO NOTHING
            """,
            new { RootId = rootId, Folder = folder }, commandTimeout: 600, cancellationToken: ct));
    }

    // ------------------------------------------------------------ the ledger

    public async Task<IReadOnlyList<WatchedRoot>> RootsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<WatchedRoot>(new CommandDefinition(
            """
            SELECT root_id AS RootId, account_id AS AccountId, label AS Label, abs_path AS AbsPath,
                   album_folder AS AlbumFolder, exclude AS Exclude
              FROM scan_root WHERE enabled ORDER BY root_id
            """, cancellationToken: ct))).ToList();
    }

    public async Task<FolderRow?> GetAsync(long rootId, string folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FolderRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM scan_folder WHERE root_id = @RootId AND folder = @Folder",
            new { RootId = rootId, Folder = folder }, cancellationToken: ct));
    }

    public async Task<HashSet<string>> KnownFoldersAsync(long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT folder FROM scan_folder WHERE root_id = @RootId AND folder <> ''",
            new { RootId = rootId }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// First sight of a root: every child folder is written down as in step,
    /// except those Tessera holds nothing under - those are news. A restart
    /// or a first run must not turn ten thousand known folders into ten
    /// thousand scans.
    /// </summary>
    public async Task<int> BootstrapAsync(long rootId, string parent, IEnumerable<string> folders, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = folders.Select(f => new { RootId = rootId, Folder = Join(parent, f) }).ToList();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO scan_folder (root_id, folder, changed_at)
            VALUES (@RootId, @Folder,
                    CASE WHEN EXISTS (SELECT 1 FROM location l
                                       WHERE l.root_id = @RootId AND l.gone_at IS NULL
                                         AND left(l.rel_path, length(@Folder) + 1) = @Folder || '/')
                         THEN NULL ELSE now() END)
            ON CONFLICT DO NOTHING
            """, rows, cancellationToken: ct));
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM scan_folder WHERE root_id = @RootId AND folder <> '' AND changed_at IS NOT NULL AND scanned_at IS NULL",
            new { RootId = rootId }, cancellationToken: ct));
    }

    /// <summary>A folder the watch had not seen before: news, to be scanned once it settles.</summary>
    public async Task NoticeAsync(long rootId, string folder, int? entries, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO scan_folder (root_id, folder, entries, checked_at, changed_at)
            VALUES (@RootId, @Folder, @Entries, now(), now())
            ON CONFLICT (root_id, folder) DO UPDATE
               SET entries = EXCLUDED.entries, checked_at = now(),
                   changed_at = coalesce(scan_folder.changed_at, now())
            """,
            new { RootId = rootId, Folder = folder, Entries = entries }, cancellationToken: ct));
    }

    /// <summary>What a stat said. A different mtime than last time is a change.</summary>
    public async Task ObservedAsync(long rootId, string folder, DateTimeOffset mtime, int? entries, bool changed, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO scan_folder (root_id, folder, mtime, entries, checked_at, changed_at)
            VALUES (@RootId, @Folder, @Mtime, @Entries, now(), CASE WHEN @Changed THEN now() END)
            ON CONFLICT (root_id, folder) DO UPDATE
               SET mtime = EXCLUDED.mtime,
                   entries = coalesce(EXCLUDED.entries, scan_folder.entries),
                   checked_at = now(),
                   changed_at = CASE WHEN @Changed THEN now() ELSE scan_folder.changed_at END
            """,
            new { RootId = rootId, Folder = folder, Mtime = mtime, Entries = entries, Changed = changed },
            cancellationToken: ct));
    }

    /// <summary>The entry count moved since last look: still being written to. Settle again.</summary>
    public async Task StillChangingAsync(long rootId, string folder, int entries, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE scan_folder SET entries = @Entries, checked_at = now(), changed_at = now() WHERE root_id = @RootId AND folder = @Folder",
            new { RootId = rootId, Folder = folder, Entries = entries }, cancellationToken: ct));
    }

    /// <summary>
    /// A scan covered this folder (or, with a null folder, the whole root):
    /// whatever had changed is now in step.
    /// </summary>
    public async Task ScannedAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE scan_folder SET changed_at = NULL, scanned_at = now()
             WHERE root_id = @RootId AND folder <> ''
               AND (@Folder IS NULL OR folder = @Folder OR left(folder, length(@Folder) + 1) = @Folder || '/')
            """,
            new { RootId = rootId, Folder = folder }, cancellationToken: ct));
    }

    /// <summary>
    /// Folders whose change has been noticed and not yet scanned. A person's
    /// own library before a corpus, then oldest first: the folder someone
    /// just copied in is what they are waiting for, and a corpus that grew
    /// by nine hundred folders while nothing was watching can go behind it.
    /// </summary>
    public async Task<IReadOnlyList<FolderRow>> ChangedAsync(long? rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<FolderRow>(new CommandDefinition(
            "SELECT " + Columns.Replace("root_id AS", "f.root_id AS") + """
              FROM scan_folder f
              JOIN scan_root r ON r.root_id = f.root_id
              JOIN account a  ON a.account_id = r.account_id
             WHERE f.changed_at IS NOT NULL AND f.folder <> ''
               AND (@RootId IS NULL OR f.root_id = @RootId)
             ORDER BY (a.kind = 'corpus'), f.changed_at
            """,
            new { RootId = rootId }, cancellationToken: ct))).ToList();
    }

    /// <summary>The next few in-step folders the sweep should stat, least recently looked at first.</summary>
    public async Task<IReadOnlyList<FolderRow>> SweepBatchAsync(long rootId, int take, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<FolderRow>(new CommandDefinition(
            "SELECT " + Columns + """
              FROM scan_folder
             WHERE root_id = @RootId AND folder <> '' AND changed_at IS NULL
             ORDER BY checked_at NULLS FIRST
             LIMIT @Take
            """,
            new { RootId = rootId, Take = take }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Changed folders too big to scan unasked, waiting for a person - and
    /// the ones small enough that are merely settling. Both are shown; the
    /// caller tells them apart by <c>entries</c>.
    /// </summary>
    public async Task<IReadOnlyList<Waiting>> WaitingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Waiting>(new CommandDefinition(
            """
            SELECT f.root_id AS RootId, r.label AS RootLabel, r.account_id AS AccountId, f.folder AS Folder,
                   f.entries AS Entries, f.first_seen AS FirstSeen, f.changed_at AS ChangedAt
              FROM scan_folder f JOIN scan_root r ON r.root_id = f.root_id
             WHERE f.changed_at IS NOT NULL AND f.folder <> ''
             ORDER BY f.changed_at
            """, cancellationToken: ct))).ToList();
    }

    public async Task ForgetAsync(long rootId, string folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scan_folder WHERE root_id = @RootId AND folder = @Folder",
            new { RootId = rootId, Folder = folder }, cancellationToken: ct));
    }

    public static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";

    private const string Columns =
        "root_id AS RootId, folder AS Folder, mtime AS Mtime, entries AS Entries, first_seen AS FirstSeen, " +
        "checked_at AS CheckedAt, changed_at AS ChangedAt, scanned_at AS ScannedAt";
}
