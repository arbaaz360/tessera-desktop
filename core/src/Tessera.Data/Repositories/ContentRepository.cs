using System.Text.Json;
using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>What one file on disk looks like to the scanner.</summary>
public readonly record struct ScannedFile(
    byte[] Sha256,
    long ByteSize,
    string Mime,
    string RelPath,
    DateTimeOffset FileMtime,
    DateTimeOffset? TakenAt,
    string? TakenSrc,
    int? Width,
    int? Height);

/// <summary>The outcome of reconciling one file against what was recorded.</summary>
public enum Reconciled { Added, Moved, Unchanged }

public sealed class ContentRepository(Db db)
{
    /// <summary>
    /// Ensure a content row exists for these bytes and return its id.
    /// </summary>
    /// <remarks>
    /// ON CONFLICT rather than select-then-insert: two scanner threads meeting
    /// the same content at once is normal, not exceptional, and a check-then-act
    /// would race. The DO UPDATE is a no-op that exists so RETURNING always
    /// yields a row.
    /// </remarks>
    public async Task<long> UpsertAsync(ScannedFile f, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO content (sha256, byte_size, mime, width, height, taken_at, taken_src)
            VALUES (@Sha256, @ByteSize, @Mime, @Width, @Height, @TakenAt, @TakenSrc)
            ON CONFLICT (sha256) DO UPDATE
                SET taken_at  = COALESCE(content.taken_at, excluded.taken_at),
                    taken_src = COALESCE(content.taken_src, excluded.taken_src),
                    width     = COALESCE(content.width, excluded.width),
                    height    = COALESCE(content.height, excluded.height)
            RETURNING content_id
            """,
            new
            {
                f.Sha256, f.ByteSize, f.Mime, f.Width, f.Height,
                TakenAt = Db.ToMs(f.TakenAt), f.TakenSrc,
            },
            cancellationToken: ct));
    }

    /// <summary>
    /// Record where these bytes are, and say whether that was new, a move, or
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The distinction that matters: a file appearing at a new path whose
    /// content is already known is a MOVE, not an import. Immich treated it as
    /// a new asset and abandoned the old one's thumbnails and vectors -
    /// 122,000 of them in one afternoon. Here it updates a row and nothing
    /// downstream is touched.
    /// </remarks>
    public async Task<Reconciled> RecordLocationAsync(
        long contentId, long accountId, long rootId, ScannedFile f, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await db.OpenAsync(ct);

        // Same path, same content: just a heartbeat.
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE location
               SET last_seen = @Now, gone_at = NULL, file_mtime = @Mtime
             WHERE root_id = @RootId AND rel_path = @RelPath AND content_id = @ContentId
            """,
            new { RootId = rootId, f.RelPath, ContentId = contentId, Mtime = Db.ToMs(f.FileMtime), Now = Db.ToMs(now) },
            cancellationToken: ct));

        if (updated > 0)
        {
            return Reconciled.Unchanged;
        }

        // The path is known but now holds different bytes (edited or replaced),
        // or the path is new. Either way one row per (root, rel_path).
        var wasKnownElsewhere = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM location
                            WHERE content_id = @ContentId AND account_id = @AccountId
                              AND gone_at IS NULL)
            """,
            new { ContentId = contentId, AccountId = accountId },
            cancellationToken: ct)) == 1;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO location (content_id, account_id, root_id, rel_path, file_mtime, first_seen, last_seen)
            VALUES (@ContentId, @AccountId, @RootId, @RelPath, @Mtime, @Now, @Now)
            ON CONFLICT (root_id, rel_path) DO UPDATE
                SET content_id = excluded.content_id,
                    file_mtime = excluded.file_mtime,
                    last_seen  = excluded.last_seen,
                    gone_at    = NULL
            """,
            new
            {
                ContentId = contentId, AccountId = accountId, RootId = rootId,
                f.RelPath, Mtime = Db.ToMs(f.FileMtime), Now = Db.ToMs(now),
            },
            cancellationToken: ct));

        return wasKnownElsewhere ? Reconciled.Moved : Reconciled.Added;
    }

    /// <summary>A file seen again, unchanged: stamp it so the scan does not mark it gone.</summary>
    public async Task TouchAsync(long rootId, string relPath, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
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
        await using var conn = await db.OpenAsync(ct);
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

        await using var conn = await db.OpenAsync(ct);
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
