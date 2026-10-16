using System.Text.Json;
using Dapper;
using Tessera.Core;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>What one file on disk looks like to the scanner.</summary>
/// <param name="MetaVersion">The header reader that produced TakenAt/Width/Height (<see cref="CaptureDates.ReaderVersion"/>), or 0 when the headers were not read.</param>
public readonly record struct ScannedFile(
    byte[] Sha256,
    long ByteSize,
    string Mime,
    string RelPath,
    DateTimeOffset FileMtime,
    DateTimeOffset? TakenAt,
    string? TakenSrc,
    int? Width,
    int? Height,
    int? DurationMs = null,
    int MetaVersion = 0);

/// <summary>The outcome of reconciling one file against what was recorded.</summary>
/// <remarks>
/// <see cref="Copied"/> and <see cref="Moved"/> are both "known bytes at a
/// new path"; what tells them apart is whether another live location of the
/// bytes is still on disk. Two byte-identical files in a fresh folder are one
/// Added and one Copied - nothing moved.
/// </remarks>
public enum Reconciled { Added, Moved, Unchanged, Copied }

/// <summary>Another live location of the same bytes, as the copy-or-move decision sees it.</summary>
public readonly record struct OtherLocation(long LocationId, string RootPath, string RelPath, long ByteSize);

/// <summary>A content the header backfill has yet to read, and one live place to read it from.</summary>
public readonly record struct MetadataPending(long ContentId, long ByteSize, string RootPath, string RelPath);

/// <summary>What the backfill read for one content. A null TakenSrc leaves the date alone.</summary>
public readonly record struct MetadataRead(
    long ContentId, DateTimeOffset? TakenAt, string? TakenSrc, int? Width, int? Height, int? DurationMs);

/// <summary>Live contents by where their date came from, and how many the backfill has not read yet.</summary>
public readonly record struct DateCounts(long Pending, long Exif, long FileName, long Mtime)
{
    public long Done => Exif + FileName + Mtime;
}

public sealed class ContentRepository(Db db)
{
    /// <summary>SQL for <see cref="TakenSource.Rank"/>; the two must agree.</summary>
    private static string Rank(string column) =>
        $"(CASE {column} WHEN '{TakenSource.Exif}' THEN 3 WHEN '{TakenSource.FileName}' THEN 2 WHEN '{TakenSource.Mtime}' THEN 1 ELSE 0 END)";

    /// <summary>
    /// Ensure a content row exists for these bytes and return its id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ON CONFLICT rather than select-then-insert: two scanner threads meeting
    /// the same content at once is normal, not exceptional, and a check-then-act
    /// would race. The DO UPDATE exists so RETURNING always yields a row.
    /// </para>
    /// <para>
    /// A second sighting replaces the date only with a strictly better one
    /// (<see cref="TakenSource.Rank"/>): a copy named IMG_20240101_123456
    /// improves a content first seen as "export.jpg", but two file times never
    /// fight, and a header date is never overwritten. The trigger on
    /// <c>content.taken_at</c> moves the timeline row.
    /// </para>
    /// </remarks>
    public async Task<long> UpsertAsync(ScannedFile f, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        var better = $"(content.taken_at IS NULL OR {Rank("excluded.taken_src")} > {Rank("content.taken_src")})";
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            INSERT INTO content (sha256, byte_size, mime, width, height, duration_ms, taken_at, taken_src, meta_version)
            VALUES (@Sha256, @ByteSize, @Mime, @Width, @Height, @DurationMs, @TakenAt, @TakenSrc, @MetaVersion)
            ON CONFLICT (sha256) DO UPDATE
                SET taken_at     = CASE WHEN {better} THEN excluded.taken_at ELSE content.taken_at END,
                    taken_src    = CASE WHEN {better} THEN excluded.taken_src ELSE content.taken_src END,
                    width        = COALESCE(content.width, excluded.width),
                    height       = COALESCE(content.height, excluded.height),
                    duration_ms  = COALESCE(content.duration_ms, excluded.duration_ms),
                    meta_version = max(content.meta_version, excluded.meta_version)
            RETURNING content_id
            """,
            new
            {
                f.Sha256, f.ByteSize, f.Mime, f.Width, f.Height, f.DurationMs,
                TakenAt = Db.ToMs(f.TakenAt), f.TakenSrc, f.MetaVersion,
            },
            cancellationToken: ct));
    }

    /// <summary>
    /// Record where these bytes are, taking every other live location of
    /// them to be still on disk: a known content at a new path is then always
    /// a copy. The scanner uses the overload that looks.
    /// </summary>
    public Task<Reconciled> RecordLocationAsync(
        long contentId, long accountId, long rootId, ScannedFile f, CancellationToken ct) =>
        RecordLocationAsync(contentId, accountId, rootId, f, stillThere: null, ct);

    /// <summary>
    /// Record where these bytes are, and say whether that was new, a move, a
    /// copy, or nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction that matters most: a file appearing at a new path
    /// whose content is already known is not an import. Immich treated it as
    /// a new asset and abandoned the old one's thumbnails and vectors -
    /// 122,000 of them in one afternoon. Here it is a location row and
    /// nothing downstream is touched.
    /// </para>
    /// <para>
    /// MOVE OR COPY. Known bytes at a new path are a copy while another live
    /// location of them is still on disk, and a move when none is. A walk
    /// cannot answer that from what it has seen so far - the old path may
    /// simply not have been reached yet - so it asks the disk:
    /// <paramref name="stillThere"/> stats the other locations. A move retires
    /// the old row on the spot (gone, with the evidence in hand) and carries
    /// its trash mark to the new one; the end-of-scan sweep then counts only
    /// what really went. A rename is therefore moved 1, gone 0.
    /// </para>
    /// <para>
    /// The look happens inside the single writer, so two threads meeting two
    /// copies of one new content cannot both decide they were first: one is
    /// Added, the other Copied.
    /// </para>
    /// </remarks>
    /// <param name="stillThere">
    /// Whether another location's file is still there; null takes them all
    /// to be. Called only for known bytes at a new path, never per file.
    /// </param>
    public async Task<Reconciled> RecordLocationAsync(
        long contentId, long accountId, long rootId, ScannedFile f, Func<OtherLocation, bool>? stillThere, CancellationToken ct)
    {
        var now = Db.ToMs(DateTimeOffset.UtcNow);
        var mtime = Db.ToMs(f.FileMtime);
        await using var conn = await db.OpenWriteAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);

        // Same path, same content: just a heartbeat.
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE location
               SET last_seen = @Now, gone_at = NULL, file_mtime = @Mtime
             WHERE root_id = @RootId AND rel_path = @RelPath AND content_id = @ContentId
            """,
            new { RootId = rootId, f.RelPath, ContentId = contentId, Mtime = mtime, Now = now },
            tx, cancellationToken: ct));

        if (updated > 0)
        {
            await tx.CommitAsync(ct);
            return Reconciled.Unchanged;
        }

        // The path is new, or known but now holding different bytes (edited
        // or replaced). Either way one row per (root, rel_path). First: where
        // else are these bytes recorded as living?
        var others = (await conn.QueryAsync<(long LocationId, string RootPath, string RelPath, long ByteSize, long? TrashedAt)>(
            new CommandDefinition(
                """
                SELECT l.location_id, r.abs_path, l.rel_path, c.byte_size, l.trashed_at
                  FROM location l
                  JOIN scan_root r ON r.root_id = l.root_id
                  JOIN content c ON c.content_id = l.content_id
                 WHERE l.content_id = @ContentId AND l.account_id = @AccountId AND l.gone_at IS NULL
                   AND NOT (l.root_id = @RootId AND l.rel_path = @RelPath)
                 ORDER BY l.location_id
                """,
                new { ContentId = contentId, AccountId = accountId, RootId = rootId, f.RelPath },
                tx, cancellationToken: ct))).ToList();

        var outcome = others.Count == 0 ? Reconciled.Added : Reconciled.Copied;
        (long LocationId, long? TrashedAt)? retire = null;
        if (others.Count > 0 && stillThere is not null &&
            others.All(o => !stillThere(new OtherLocation(o.LocationId, o.RootPath, o.RelPath, o.ByteSize))))
        {
            outcome = Reconciled.Moved;
            retire = (others[0].LocationId, others[0].TrashedAt);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO location (content_id, account_id, root_id, rel_path, file_mtime, first_seen, last_seen, trashed_at)
            VALUES (@ContentId, @AccountId, @RootId, @RelPath, @Mtime, @Now, @Now, @TrashedAt)
            ON CONFLICT (root_id, rel_path) DO UPDATE
                SET content_id = excluded.content_id,
                    file_mtime = excluded.file_mtime,
                    last_seen  = excluded.last_seen,
                    gone_at    = NULL,
                    trashed_at = CASE WHEN @Moving THEN excluded.trashed_at ELSE location.trashed_at END
            """,
            new
            {
                ContentId = contentId, AccountId = accountId, RootId = rootId,
                f.RelPath, Mtime = mtime, Now = now,
                TrashedAt = retire?.TrashedAt, Moving = retire is not null,
            },
            tx, cancellationToken: ct));

        // After the new row, so the picture never has zero live locations and
        // its timeline row is never dropped and re-made.
        if (retire is { } old)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE location SET gone_at = @Now WHERE location_id = @LocationId",
                new { Now = now, old.LocationId }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return outcome;
    }

    // ------------------------------------------------------- header backfill

    /// <summary>
    /// Contents no header reader has looked at yet (indexed before dates were
    /// read at first sight), in id order after <paramref name="afterContentId"/>,
    /// each with one live location to read - an untrashed one when there is one.
    /// Contents with no live location are not pending: nothing shows them.
    /// </summary>
    public async Task<IReadOnlyList<MetadataPending>> MetadataPendingAsync(long afterContentId, int take, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<MetadataPending>(new CommandDefinition(
            $"""
            SELECT c.content_id AS ContentId, c.byte_size AS ByteSize, r.abs_path AS RootPath, l.rel_path AS RelPath
              FROM content c
              JOIN location l ON l.location_id = (
                       SELECT l2.location_id FROM location l2
                        WHERE l2.content_id = c.content_id AND l2.gone_at IS NULL
                        ORDER BY (l2.trashed_at IS NULL) DESC, l2.location_id
                        LIMIT 1)
              JOIN scan_root r ON r.root_id = l.root_id
             WHERE c.meta_version < {CaptureDates.ReaderVersion} AND c.content_id > @After
             ORDER BY c.content_id
             LIMIT @Take
            """,
            new { After = afterContentId, Take = take }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Write what the backfill read, one transaction per batch. A date only
    /// replaces a worse one; the trigger on <c>content.taken_at</c> retimes
    /// the timeline. Every content here is stamped read, whether or not it
    /// said anything, so it is never read again by this reader.
    /// </summary>
    /// <returns>How many contents got a better date.</returns>
    public async Task<int> ApplyMetadataAsync(IReadOnlyList<MetadataRead> reads, CancellationToken ct)
    {
        if (reads.Count == 0)
        {
            return 0;
        }

        var better = $"(@TakenSrc IS NOT NULL AND (taken_at IS NULL OR {Rank("@TakenSrc")} > {Rank("taken_src")}))";
        await using var conn = await db.OpenWriteAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);

        // RETURNING sees the row after the update, so "was it better" is
        // asked of the row before it, once per batch.
        var before = (await conn.QueryAsync<(long ContentId, string? TakenSrc, long? TakenAt)>(new CommandDefinition(
            "SELECT content_id, taken_src, taken_at FROM content WHERE content_id IN (SELECT value FROM json_each(@Ids))",
            new { Ids = JsonSerializer.Serialize(reads.Select(r => r.ContentId)) },
            tx, cancellationToken: ct))).ToDictionary(b => b.ContentId);

        var retimed = 0;
        foreach (var r in reads)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE content
                   SET taken_at     = CASE WHEN {better} THEN @TakenAt ELSE taken_at END,
                       taken_src    = CASE WHEN {better} THEN @TakenSrc ELSE taken_src END,
                       width        = COALESCE(width, @Width),
                       height       = COALESCE(height, @Height),
                       duration_ms  = COALESCE(duration_ms, @DurationMs),
                       meta_version = max(meta_version, {CaptureDates.ReaderVersion})
                 WHERE content_id = @ContentId
                """,
                new { r.ContentId, TakenAt = Db.ToMs(r.TakenAt), r.TakenSrc, r.Width, r.Height, r.DurationMs },
                tx, cancellationToken: ct));

            if (r.TakenSrc is not null && before.TryGetValue(r.ContentId, out var b) &&
                (b.TakenAt is null || TakenSource.Rank(r.TakenSrc) > TakenSource.Rank(b.TakenSrc)))
            {
                retimed++;
            }
        }

        await tx.CommitAsync(ct);
        return retimed;
    }

    /// <summary>Contents with a live location, by date source; the ones not yet read are counted apart.</summary>
    public async Task<DateCounts> DateCountsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long IsRead, string? Src, long N)>(new CommandDefinition(
            $"""
            SELECT c.meta_version >= {CaptureDates.ReaderVersion}, c.taken_src, count(*)
              FROM content c
             WHERE EXISTS (SELECT 1 FROM location l WHERE l.content_id = c.content_id AND l.gone_at IS NULL)
             GROUP BY 1, 2
            """, cancellationToken: ct));

        long pending = 0, exif = 0, name = 0, mtime = 0;
        foreach (var (isRead, src, n) in rows)
        {
            if (isRead == 0)
            {
                pending += n;
            }
            else if (src == TakenSource.Exif)
            {
                exif += n;
            }
            else if (src == TakenSource.FileName)
            {
                name += n;
            }
            else
            {
                mtime += n;
            }
        }

        return new DateCounts(pending, exif, name, mtime);
    }

    /// <summary>A file seen again, unchanged: stamp it so the scan does not mark it gone.</summary>
    public async Task TouchAsync(long rootId, string relPath, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE location SET last_seen = @Now WHERE root_id = @RootId AND rel_path = @RelPath",
            new { RootId = rootId, RelPath = relPath, Now = Db.ToMs(now) },
            cancellationToken: ct));
    }

    /// <summary>
    /// Mark everything under a root that this scan did not see.
    /// </summary>
    /// <remarks>
    /// Rows are never deleted, only stamped. A file that comes back is then
    /// recognised rather than re-imported, and the history of what was here is
    /// still answerable.
    /// </remarks>
    /// <param name="folder">
    /// Limit to one folder under the root (no leading slash), or null for the
    /// whole root. A scoped walk must only ever mark rows gone INSIDE what it
    /// walked; anything outside was not looked at and must not be judged.
    /// </param>
    /// <param name="enumerated">
    /// The extensions the walk looked at. A row whose file the scanner would
    /// not have enumerated cannot be judged gone by that scanner: "not seen"
    /// and "not looked for" must never be the same verdict.
    /// </param>
    public async Task<long> MarkGoneAsync(
        long rootId, DateTimeOffset scanStart, string? folder, IReadOnlyCollection<string> enumerated, CancellationToken ct)
    {
        var exts = enumerated.Select(e => e.TrimStart('.').ToLowerInvariant()).ToArray();
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE location SET gone_at = @Now
             WHERE root_id = @RootId AND gone_at IS NULL AND last_seen < @ScanStart
               AND (@Folder IS NULL OR substr(rel_path, 1, length(@Folder) + 1) = @Folder || '/')
               AND EXISTS (SELECT 1 FROM json_each(@Exts) e WHERE rel_path LIKE '%.' || e.value)
            """,
            new
            {
                RootId = rootId, ScanStart = Db.ToMs(scanStart), Now = Db.ToMs(DateTimeOffset.UtcNow),
                Folder = folder, Exts = JsonSerializer.Serialize(exts),
            },
            cancellationToken: ct));
    }

    /// <summary>Hashes already known, so a rescan need not re-read unchanged files.</summary>
    public async Task<Dictionary<string, (long ContentId, long Size, DateTimeOffset? Mtime)>>
        KnownUnderRootAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string RelPath, long ContentId, long Size, long? Mtime)>(
            new CommandDefinition(
                """
                SELECT l.rel_path, l.content_id, c.byte_size, l.file_mtime
                  FROM location l JOIN content c ON c.content_id = l.content_id
                 WHERE l.root_id = @RootId AND l.gone_at IS NULL
                   AND (@Folder IS NULL OR substr(l.rel_path, 1, length(@Folder) + 1) = @Folder || '/')
                """,
                new { RootId = rootId, Folder = folder },
                cancellationToken: ct));

        return rows.ToDictionary(
            r => r.RelPath,
            r => (r.ContentId, r.Size, Db.FromMs(r.Mtime)),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Bulk load. Batched multi-row INSERTs in one transaction - SQLite's
    /// answer to binary COPY, and well over 100k rows/s.
    /// </summary>
    /// <returns>Rows offered. Duplicates by hash are ignored, not counted twice.</returns>
    public async Task<long> BulkInsertAsync(IAsyncEnumerable<ScannedFile> files, CancellationToken ct)
    {
        const int batchSize = 500;

        await using var conn = await db.OpenWriteAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);

        long n = 0;
        var batch = new List<ScannedFile>(batchSize);
        await foreach (var f in files.WithCancellation(ct))
        {
            batch.Add(f);
            if (batch.Count == batchSize)
            {
                n += await FlushAsync(conn, tx, batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            n += await FlushAsync(conn, tx, batch, ct);
        }

        await tx.CommitAsync(ct);
        return n;

        static async Task<int> FlushAsync(
            System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, List<ScannedFile> batch, CancellationToken ct)
        {
            var sql = new System.Text.StringBuilder(
                "INSERT OR IGNORE INTO content (sha256, byte_size, mime, width, height, taken_at, taken_src) VALUES ");
            var p = new DynamicParameters();
            for (var i = 0; i < batch.Count; i++)
            {
                var f = batch[i];
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"(@s{i},@b{i},@m{i},@w{i},@h{i},@t{i},@src{i})");
                p.Add($"s{i}", f.Sha256);
                p.Add($"b{i}", f.ByteSize);
                p.Add($"m{i}", f.Mime);
                p.Add($"w{i}", f.Width);
                p.Add($"h{i}", f.Height);
                p.Add($"t{i}", Db.ToMs(f.TakenAt));
                p.Add($"src{i}", f.TakenSrc);
            }

            await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), p, tx, cancellationToken: ct));
            return batch.Count;
        }
    }
}
