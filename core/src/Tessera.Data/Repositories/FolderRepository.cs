using System.Text.Json;
using Dapper;

namespace Tessera.Data.Repositories;

/// <summary>
/// Folders: the albums they imply, and what the watch knows about them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A folder under a root's album folder is an album of that name</b>, and
/// every content located under it is a member. That is the whole rule, it is
/// applied as two set-based statements at the end of any scan that could
/// have changed the answer, and it is additive only: a picture put in an
/// album by hand, or one whose file has since moved, stays where a person or
/// the past left it, because membership is about content and content does
/// not move.
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
    // onto settable properties.
    public sealed record WatchedRoot
    {
        public long RootId { get; init; }
        public long AccountId { get; init; }
        public string Label { get; init; } = "";
        public string AbsPath { get; init; } = "";
        public string? AlbumFolder { get; init; }
        public string ExcludeJson { get; init; } = "[]";

        public string[] Exclude => ParseExclude(ExcludeJson);

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

    public static string[] ParseExclude(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
        const string named = """
            WITH scoped AS (
                SELECT l.content_id, r.account_id,
                       substr(l.rel_path, length(p.prefix) + 1) AS rest
                  FROM location l
                  JOIN scan_root r ON r.root_id = l.root_id
                  JOIN (SELECT root_id,
                               CASE WHEN album_folder = '' THEN '' ELSE album_folder || '/' END AS prefix
                          FROM scan_root) p ON p.root_id = r.root_id
                 WHERE l.root_id = @RootId
                   AND l.gone_at IS NULL
                   AND r.album_folder IS NOT NULL
                   AND substr(l.rel_path, 1, length(p.prefix)) = p.prefix
                   AND (@Folder IS NULL OR substr(l.rel_path, 1, length(@Folder) + 1) = @Folder || '/')
            ),
            named AS (
                SELECT content_id, account_id, substr(rest, 1, instr(rest, '/') - 1) AS name
                  FROM scoped
                 WHERE instr(rest, '/') > 0
            )
            """;

        var albums = await conn.ExecuteAsync(new CommandDefinition(
            named + """

            INSERT INTO album (account_id, name)
            SELECT DISTINCT account_id, name FROM named WHERE true
            ON CONFLICT (account_id, name) DO NOTHING
            """,
            new { RootId = rootId, Folder = folder }, commandTimeout: 600, cancellationToken: ct));

        var members = await conn.ExecuteAsync(new CommandDefinition(
            named + """

            INSERT INTO album_item (album_id, content_id)
            SELECT a.album_id, n.content_id
              FROM named n
              JOIN album a ON a.account_id = n.account_id AND a.name = n.name
             WHERE true
            ON CONFLICT DO NOTHING
            """,
            new { RootId = rootId, Folder = folder }, commandTimeout: 600, cancellationToken: ct));

        return (albums, members);
    }

    /// <summary>
    /// Queue thumbnails for what a scan just brought in, under one folder of
    /// one root. Images only; a video poster needs a frame extractor this
    /// build does not ship.
    /// </summary>
    /// <param name="maxContentId">
    /// The free tier's boundary: content with a higher id is beyond the cap
    /// and gets no thumbnail (CONTRACT §4). Null when there is no cap.
    /// </param>
    public async Task<int> QueueThumbnailsUnderAsync(long rootId, string? folder, long? maxContentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT DISTINCT 'thumbnail', l.content_id
              FROM location l
              JOIN content c ON c.content_id = l.content_id
             WHERE l.root_id = @RootId AND l.gone_at IS NULL
               AND c.mime LIKE 'image/%'
               AND (@Folder IS NULL OR substr(l.rel_path, 1, length(@Folder) + 1) = @Folder || '/')
               AND (@MaxId IS NULL OR l.content_id <= @MaxId)
               AND NOT EXISTS (SELECT 1 FROM thumbnail t WHERE t.content_id = l.content_id AND t.kind = 'thumb')
               AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = l.content_id AND w.kind = 'thumbnail'
                                                          AND w.state IN ('pending', 'running', 'failed'))
            ON CONFLICT DO NOTHING
            """,
            new { RootId = rootId, Folder = folder, MaxId = maxContentId }, commandTimeout: 600, cancellationToken: ct));
    }

    // ------------------------------------------------------------ the ledger

    public async Task<IReadOnlyList<WatchedRoot>> RootsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<WatchedRoot>(new CommandDefinition(
            """
            SELECT root_id AS RootId, account_id AS AccountId, label AS Label, abs_path AS AbsPath,
                   album_folder AS AlbumFolder, exclude AS ExcludeJson
              FROM scan_root WHERE enabled = 1 ORDER BY root_id
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
    /// must not turn ten thousand known folders into ten thousand scans.
    /// </summary>
    public async Task<int> BootstrapAsync(long rootId, string parent, IEnumerable<string> folders, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);
        var rows = folders.Select(f => new { RootId = rootId, Folder = Join(parent, f) }).ToList();
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO scan_folder (root_id, folder, changed_at)
            VALUES (@RootId, @Folder,
                    CASE WHEN EXISTS (SELECT 1 FROM location l
                                       WHERE l.root_id = @RootId AND l.gone_at IS NULL
                                         AND substr(l.rel_path, 1, length(@Folder) + 1) = @Folder || '/')
                         THEN NULL ELSE {Db.NowMs} END)
            ON CONFLICT DO NOTHING
            """, rows, tx, cancellationToken: ct));
        var news = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM scan_folder WHERE root_id = @RootId AND folder <> '' AND changed_at IS NOT NULL AND scanned_at IS NULL",
            new { RootId = rootId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return news;
    }

    /// <summary>A folder the watch had not seen before: news, to be scanned once it settles.</summary>
    public async Task NoticeAsync(long rootId, string folder, int? entries, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO scan_folder (root_id, folder, entries, checked_at, changed_at)
            VALUES (@RootId, @Folder, @Entries, {Db.NowMs}, {Db.NowMs})
            ON CONFLICT (root_id, folder) DO UPDATE
               SET entries = excluded.entries, checked_at = {Db.NowMs},
                   changed_at = coalesce(scan_folder.changed_at, {Db.NowMs})
            """,
            new { RootId = rootId, Folder = folder, Entries = entries }, cancellationToken: ct));
    }

    /// <summary>What a stat said. A different mtime than last time is a change.</summary>
    public async Task ObservedAsync(long rootId, string folder, DateTimeOffset mtime, int? entries, bool changed, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO scan_folder (root_id, folder, mtime, entries, checked_at, changed_at)
            VALUES (@RootId, @Folder, @Mtime, @Entries, {Db.NowMs}, CASE WHEN @Changed THEN {Db.NowMs} END)
            ON CONFLICT (root_id, folder) DO UPDATE
               SET mtime = excluded.mtime,
                   entries = coalesce(excluded.entries, scan_folder.entries),
                   checked_at = {Db.NowMs},
                   changed_at = CASE WHEN @Changed THEN {Db.NowMs} ELSE scan_folder.changed_at END
            """,
            new { RootId = rootId, Folder = folder, Mtime = Db.ToMs(mtime), Entries = entries, Changed = changed },
            cancellationToken: ct));
    }

    /// <summary>The entry count moved since last look: still being written to. Settle again.</summary>
    public async Task StillChangingAsync(long rootId, string folder, int entries, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE scan_folder SET entries = @Entries, checked_at = {Db.NowMs}, changed_at = {Db.NowMs} WHERE root_id = @RootId AND folder = @Folder",
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
            $"""
            UPDATE scan_folder SET changed_at = NULL, scanned_at = {Db.NowMs}
             WHERE root_id = @RootId AND folder <> ''
               AND (@Folder IS NULL OR folder = @Folder OR substr(folder, 1, length(@Folder) + 1) = @Folder || '/')
            """,
            new { RootId = rootId, Folder = folder }, cancellationToken: ct));
    }

    /// <summary>Folders whose change has been noticed and not yet scanned, oldest first.</summary>
    public async Task<IReadOnlyList<FolderRow>> ChangedAsync(long? rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<FolderRow>(new CommandDefinition(
            "SELECT " + Columns.Replace("root_id AS", "f.root_id AS", StringComparison.Ordinal) + """
              FROM scan_folder f
             WHERE f.changed_at IS NOT NULL AND f.folder <> ''
               AND (@RootId IS NULL OR f.root_id = @RootId)
             ORDER BY f.changed_at
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

    /// <summary>Changed folders not yet scanned: settling, or queued. Shown on the Library page.</summary>
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
