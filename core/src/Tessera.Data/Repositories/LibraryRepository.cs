using Dapper;
using Tessera.Core;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>A root as the Library page sees it: the folder and its numbers.</summary>
public sealed record RootSummary
{
    public long RootId { get; init; }
    public string Label { get; init; } = "";
    public string AbsPath { get; init; } = "";
    public bool Enabled { get; init; }
    public long Files { get; init; }
    public long Contents { get; init; }
    public long Trashed { get; init; }
}

/// <summary>One live copy of a duplicated content.</summary>
public sealed record DuplicateLocation(long LocationId, long RootId, string RelPath);

/// <summary>A content with two or more live locations.</summary>
public sealed record DuplicateSet(long ContentId, byte[] Sha256, long ByteSize, IReadOnlyList<DuplicateLocation> Locations);

/// <summary>Why a path cannot become a root.</summary>
public enum RootConflict { None, AlreadyARoot, InsideARoot, ContainsARoot }

/// <summary>
/// The library: its roots, their counts, their scans, and the duplicates
/// view. Everything the Library page and the free-tier rule ask.
/// </summary>
public sealed class LibraryRepository(Db db)
{
    public async Task<IReadOnlyList<RootSummary>> RootsAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<RootSummary>(new CommandDefinition(
            """
            SELECT r.root_id AS RootId, r.label AS Label, r.abs_path AS AbsPath, r.enabled AS Enabled,
                   (SELECT count(*) FROM location l
                     WHERE l.root_id = r.root_id AND l.gone_at IS NULL AND l.trashed_at IS NULL) AS Files,
                   (SELECT count(DISTINCT l.content_id) FROM location l
                     WHERE l.root_id = r.root_id AND l.gone_at IS NULL AND l.trashed_at IS NULL) AS Contents,
                   (SELECT count(*) FROM location l
                     WHERE l.root_id = r.root_id AND l.gone_at IS NULL AND l.trashed_at IS NOT NULL) AS Trashed
              FROM scan_root r
             WHERE r.account_id = @AccountId
             ORDER BY r.root_id
            """,
            new { AccountId = accountId }, cancellationToken: ct))).ToList();
    }

    public async Task<ScanRoot?> RootAsync(long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long RootId, long AccountId, string Label, string AbsPath, bool Enabled, string Exclude, string? AlbumFolder)?>(
            new CommandDefinition(
                "SELECT root_id, account_id, label, abs_path, enabled, exclude, album_folder FROM scan_root WHERE root_id = @RootId",
                new { RootId = rootId }, cancellationToken: ct));
        return row is null ? null : new ScanRoot
        {
            RootId = row.Value.RootId,
            AccountId = row.Value.AccountId,
            Label = row.Value.Label,
            AbsPath = row.Value.AbsPath,
            Enabled = row.Value.Enabled,
            Exclude = FolderRepository.ParseExclude(row.Value.Exclude),
            AlbumFolder = row.Value.AlbumFolder,
        };
    }

    /// <summary>
    /// Whether a canonical path may become a root: not one already, not under
    /// one, not over one. Two roots over the same files would index them
    /// twice - which once registered 55,370 files twice on the HomeLab.
    /// </summary>
    public async Task<(RootConflict Conflict, string? Other)> ConflictAsync(string canonicalPath, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var existing = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT abs_path FROM scan_root", cancellationToken: ct));

        foreach (var other in existing)
        {
            if (string.Equals(other, canonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return (RootConflict.AlreadyARoot, other);
            }

            if (PathRules.RelativeTo(other, canonicalPath) is not null)
            {
                return (RootConflict.InsideARoot, other);
            }

            if (PathRules.RelativeTo(canonicalPath, other) is not null)
            {
                return (RootConflict.ContainsARoot, other);
            }
        }

        return (RootConflict.None, null);
    }

    public async Task<long> AddRootAsync(long accountId, string canonicalPath, string label, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO scan_root (account_id, label, abs_path, album_folder)
            VALUES (@AccountId, @Label, @AbsPath, '')
            RETURNING root_id
            """,
            new { AccountId = accountId, Label = label, AbsPath = canonicalPath }, cancellationToken: ct));
    }

    /// <summary>
    /// Forget a root. Its location rows go (and with them its timeline rows,
    /// by trigger); content rows stay, so albums keep their members. Nothing
    /// on disk is touched.
    /// </summary>
    public async Task<bool> RemoveRootAsync(long accountId, long rootId, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);

        // Locations are deleted by statement, not by cascade, so the row
        // triggers that keep timeline_entry in step are certain to fire.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM location WHERE root_id = @RootId",
            new { RootId = rootId }, tx, cancellationToken: ct));
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scan_root WHERE root_id = @RootId AND account_id = @AccountId",
            new { RootId = rootId, AccountId = accountId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return removed > 0;
    }

    /// <summary>Contents with two or more live locations, most copies first.</summary>
    public async Task<IReadOnlyList<DuplicateSet>> DuplicatesAsync(long accountId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long ContentId, byte[] Sha256, long ByteSize, long LocationId, long RootId, string RelPath)>(
            new CommandDefinition(
                """
                WITH dup AS (
                    SELECT content_id FROM location
                     WHERE account_id = @AccountId AND gone_at IS NULL AND trashed_at IS NULL
                     GROUP BY content_id HAVING count(*) > 1
                     ORDER BY count(*) DESC, content_id
                     LIMIT @Take
                )
                SELECT c.content_id, c.sha256, c.byte_size, l.location_id, l.root_id, l.rel_path
                  FROM dup d
                  JOIN content c ON c.content_id = d.content_id
                  JOIN location l ON l.content_id = d.content_id
                                 AND l.account_id = @AccountId AND l.gone_at IS NULL AND l.trashed_at IS NULL
                 ORDER BY c.content_id, l.location_id
                """,
                new { AccountId = accountId, Take = Math.Clamp(limit, 1, 5000) }, cancellationToken: ct));

        return rows
            .GroupBy(r => r.ContentId)
            .Select(g => new DuplicateSet(
                g.Key, g.First().Sha256, g.First().ByteSize,
                g.Select(r => new DuplicateLocation(r.LocationId, r.RootId, r.RelPath)).ToList()))
            .OrderByDescending(s => s.Locations.Count)
            .ThenBy(s => s.ContentId)
            .ToList();
    }

    /// <summary>Distinct content across live locations: the number the free tier is measured on.</summary>
    public async Task<long> IndexedCountAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(DISTINCT content_id) FROM location WHERE gone_at IS NULL", cancellationToken: ct));
    }

    /// <summary>
    /// The free tier's boundary: the id of the cap-th content, counted in the
    /// order they were indexed. Content with a higher id is beyond the cap.
    /// Null when the library is within the cap.
    /// </summary>
    public async Task<long?> CapBoundaryAsync(int cap, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var indexed = await IndexedCountAsync(ct);
        if (indexed <= cap)
        {
            return null;
        }

        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT content_id FROM (
                SELECT DISTINCT content_id FROM location WHERE gone_at IS NULL
                 ORDER BY content_id LIMIT 1 OFFSET @Offset)
            """,
            new { Offset = cap - 1 }, cancellationToken: ct));
    }

    // ------------------------------------------------------------- scan runs

    /// <summary>Open a run row before the scan starts, so the run id exists the moment it is queued.</summary>
    public async Task<long> CreateRunAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO scan_run (root_id, folder) VALUES (@RootId, @Folder) RETURNING run_id",
            new { RootId = rootId, Folder = folder }, cancellationToken: ct));
    }

    public async Task<ScanRun?> RunAsync(long runId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ScanRun>(new CommandDefinition(
            RunColumns + " WHERE run_id = @RunId", new { RunId = runId }, cancellationToken: ct));
    }

    /// <summary>The newest run per root, running or finished.</summary>
    public async Task<Dictionary<long, ScanRun>> LatestRunsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScanRun>(new CommandDefinition(
            RunColumns + """

             WHERE run_id IN (SELECT max(run_id) FROM scan_run GROUP BY root_id)
            """, cancellationToken: ct));
        return rows.ToDictionary(r => r.RootId);
    }

    /// <summary>Roots with a run that has not finished: running, or queued.</summary>
    public async Task<HashSet<long>> ScanningRootsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT root_id FROM scan_run WHERE finished_at IS NULL", cancellationToken: ct))).ToHashSet();
    }

    /// <summary>
    /// At start: a run left unfinished belonged to a process that died. Say
    /// so, rather than show a scan that has been "running" since last week.
    /// </summary>
    public async Task<int> MarkInterruptedRunsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE scan_run SET finished_at = {Db.NowMs}, cancelled = 1,
                   error_sample = coalesce(error_sample, 'interrupted: the core stopped before this scan finished')
             WHERE finished_at IS NULL
            """, cancellationToken: ct));
    }

    private const string RunColumns = """
        SELECT run_id AS RunId, root_id AS RootId, started_at AS StartedAt, finished_at AS FinishedAt,
               files_seen AS FilesSeen, added AS Added, moved AS Moved, copies AS Copies, unchanged AS Unchanged,
               marked_gone AS MarkedGone, errors AS Errors, error_sample AS ErrorSample, cancelled AS Cancelled
          FROM scan_run
        """;
}
