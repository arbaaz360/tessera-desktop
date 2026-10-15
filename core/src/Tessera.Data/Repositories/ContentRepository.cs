using Dapper;
using Npgsql;
using NpgsqlTypes;
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
                SET taken_at  = COALESCE(content.taken_at, EXCLUDED.taken_at),
                    taken_src = COALESCE(content.taken_src, EXCLUDED.taken_src),
                    width     = COALESCE(content.width, EXCLUDED.width),
                    height    = COALESCE(content.height, EXCLUDED.height)
            RETURNING content_id
            """,
            new
            {
                f.Sha256, f.ByteSize, f.Mime, f.Width, f.Height,
                TakenAt = f.TakenAt, f.TakenSrc,
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
    /// a new asset and abandoned the old one's thumbnails, faces and vectors -
    /// 122,000 of them in one afternoon. Here it updates a row and nothing
    /// downstream is touched.
    /// </remarks>
    public async Task<Reconciled> RecordLocationAsync(
        long contentId, long accountId, long rootId, ScannedFile f, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // Same path, same content: just a heartbeat.
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE location
               SET last_seen = now(), gone_at = NULL, file_mtime = @Mtime
             WHERE root_id = @RootId AND rel_path = @RelPath AND content_id = @ContentId
            """,
            new { RootId = rootId, f.RelPath, ContentId = contentId, Mtime = f.FileMtime },
            cancellationToken: ct));

        if (updated > 0)
        {
            return Reconciled.Unchanged;
        }

        // The path is known but now holds different bytes (edited or replaced),
        // or the path is new. Either way one row per (root, rel_path).
        var wasKnownElsewhere = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM location
                            WHERE content_id = @ContentId AND account_id = @AccountId
                              AND gone_at IS NULL)
            """,
            new { ContentId = contentId, AccountId = accountId },
            cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO location (content_id, account_id, root_id, rel_path, file_mtime)
            VALUES (@ContentId, @AccountId, @RootId, @RelPath, @Mtime)
            ON CONFLICT (root_id, rel_path) DO UPDATE
                SET content_id = EXCLUDED.content_id,
                    file_mtime = EXCLUDED.file_mtime,
                    last_seen  = now(),
                    gone_at    = NULL
            """,
            new
            {
                ContentId = contentId, AccountId = accountId, RootId = rootId,
                f.RelPath, Mtime = f.FileMtime,
            },
            cancellationToken: ct));

        return wasKnownElsewhere ? Reconciled.Moved : Reconciled.Added;
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
            UPDATE location SET gone_at = now()
             WHERE root_id = @RootId AND gone_at IS NULL AND last_seen < @ScanStart
               AND (@Folder::text IS NULL OR rel_path LIKE @Folder || '/%')
               AND lower(regexp_replace(rel_path, '^.*\.', '')) = ANY(@Exts)
            """,
            new { RootId = rootId, ScanStart = scanStart, Folder = folder, Exts = exts },
            cancellationToken: ct));
    }

    /// <summary>Hashes already known, so a rescan need not re-read unchanged files.</summary>
    public async Task<Dictionary<string, (long ContentId, long Size, DateTimeOffset? Mtime)>>
        KnownUnderRootAsync(long rootId, string? folder, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string RelPath, long ContentId, long Size, DateTimeOffset? Mtime)>(
            new CommandDefinition(
                """
                SELECT l.rel_path, l.content_id, c.byte_size, l.file_mtime
                  FROM location l JOIN content c ON c.content_id = l.content_id
                 WHERE l.root_id = @RootId AND l.gone_at IS NULL
                   AND (@Folder::text IS NULL OR l.rel_path LIKE @Folder || '/%')
                """,
                new { RootId = rootId, Folder = folder },
                cancellationToken: ct));

        return rows.ToDictionary(
            r => r.RelPath,
            r => (r.ContentId, r.Size, r.Mtime),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Bulk load, for the one-off import. Binary COPY, not a million INSERTs.
    /// </summary>
    public async Task<long> CopyContentAsync(
        IAsyncEnumerable<ScannedFile> files, CancellationToken ct)
    {
        await using var conn = await db.Source.OpenConnectionAsync(ct);

        // Staged into a temp table, then merged, because COPY cannot express
        // ON CONFLICT and the import re-runs after interruption.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TEMP TABLE stage_content (
                    sha256 bytea, byte_size bigint, mime text,
                    width int, height int, taken_at timestamptz, taken_src text
                ) ON COMMIT DROP
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        long n = 0;
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY stage_content (sha256, byte_size, mime, width, height, taken_at, taken_src) FROM STDIN (FORMAT BINARY)",
            ct))
        {
            await foreach (var f in files.WithCancellation(ct))
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(f.Sha256, NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(f.ByteSize, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(f.Mime, NpgsqlDbType.Text, ct);
                await WriteNullableIntAsync(writer, f.Width, ct);
                await WriteNullableIntAsync(writer, f.Height, ct);
                if (f.TakenAt is { } t)
                {
                    await writer.WriteAsync(t, NpgsqlDbType.TimestampTz, ct);
                }
                else
                {
                    await writer.WriteNullAsync(ct);
                }

                if (f.TakenSrc is { } src)
                {
                    await writer.WriteAsync(src, NpgsqlDbType.Text, ct);
                }
                else
                {
                    await writer.WriteNullAsync(ct);
                }

                n++;
            }

            await writer.CompleteAsync(ct);
        }

        await using (var merge = conn.CreateCommand())
        {
            merge.CommandText =
                """
                INSERT INTO content (sha256, byte_size, mime, width, height, taken_at, taken_src)
                SELECT DISTINCT ON (sha256) sha256, byte_size, mime, width, height, taken_at, taken_src
                  FROM stage_content
                 ORDER BY sha256, taken_at NULLS LAST
                ON CONFLICT (sha256) DO NOTHING
                """;
            await merge.ExecuteNonQueryAsync(ct);
        }

        return n;
    }

    private static async Task WriteNullableIntAsync(
        NpgsqlBinaryImporter w, int? value, CancellationToken ct)
    {
        if (value is { } v)
        {
            await w.WriteAsync(v, NpgsqlDbType.Integer, ct);
        }
        else
        {
            await w.WriteNullAsync(ct);
        }
    }
}
