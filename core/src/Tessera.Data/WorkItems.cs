using System.Text.Json;
using Dapper;

namespace Tessera.Data;

/// <summary>One leased unit of work: which row, which content, and where a live copy is.</summary>
/// <param name="Root">A live location's root, or null when every copy is gone.</param>
/// <param name="Rel">That location's path under the root.</param>
public readonly record struct Leased(
    long WorkId, long ContentId, byte[] Sha256, string Mime, int Attempts, string? Root, string? Rel);

/// <summary>
/// The queue every background job draws from: lease, finish, count.
/// </summary>
/// <remarks>
/// <para>
/// One table, <c>work_item</c>, one row per (kind, content). The thumbnail
/// worker and the embedding worker share this one definition of "what does
/// it mean to take a job"; two copies is how one ends up retrying for ever
/// while the other gives up.
/// </para>
/// <para>
/// Work is LEASED, not claimed: a row is marked running with a deadline, so a
/// worker that dies mid-job releases it instead of parking it for ever. The
/// lease is one IMMEDIATE transaction, which is SQLite's SKIP LOCKED: there is
/// one writer, so two workers cannot be handed the same row. A job fails for
/// good after <see cref="MaxAttempts"/>.
/// </para>
/// </remarks>
public sealed class WorkItems(Db db)
{
    public const int MaxAttempts = 3;

    public const string ThumbnailKind = "thumbnail";
    public const string EmbeddingKind = "embedding";

    /// <summary>Take up to <paramref name="take"/> pending items of one kind.</summary>
    public async Task<IReadOnlyList<Leased>> LeaseAsync(string kind, int take, TimeSpan lease, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var conn = await db.OpenAsync(ct);
        await using var tx = conn.BeginTransaction(deferred: false);

        var claimed = (await conn.QueryAsync<long>(new CommandDefinition(
            """
            UPDATE work_item
               SET state = 'running', attempts = attempts + 1, leased_until = @Until
             WHERE work_id IN (SELECT work_id FROM work_item
                                WHERE kind = @Kind
                                  AND (state = 'pending' OR (state = 'running' AND leased_until < @Now))
                                ORDER BY enqueued_at
                                LIMIT @Take)
            RETURNING work_id
            """,
            new { Kind = kind, Take = take, Now = now, Until = now + lease },
            tx, cancellationToken: ct))).ToList();

        if (claimed.Count == 0)
        {
            await tx.CommitAsync(ct);
            return [];
        }

        var rows = (await conn.QueryAsync<Leased>(new CommandDefinition(
            """
            SELECT w.work_id AS WorkId, w.content_id AS ContentId, c.sha256 AS Sha256,
                   c.mime AS Mime, w.attempts AS Attempts,
                   (SELECT r.abs_path FROM location l JOIN scan_root r ON r.root_id = l.root_id
                     WHERE l.content_id = w.content_id AND l.gone_at IS NULL
                     ORDER BY l.location_id LIMIT 1) AS Root,
                   (SELECT l.rel_path FROM location l
                     WHERE l.content_id = w.content_id AND l.gone_at IS NULL
                     ORDER BY l.location_id LIMIT 1) AS Rel
              FROM work_item w
              JOIN content c ON c.content_id = w.content_id
             WHERE w.work_id IN (SELECT value FROM json_each(@Ids))
             ORDER BY w.enqueued_at
            """,
            new { Ids = JsonSerializer.Serialize(claimed) },
            tx, cancellationToken: ct))).ToList();

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Record the outcome. A null error is success; an error is retried until
    /// the attempts run out, then kept as failed with the last message.
    /// </summary>
    public async Task CompleteAsync(long workId, string? error, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE work_item
               SET state = CASE WHEN @Error IS NULL THEN 'done'
                                WHEN attempts >= @Max THEN 'failed'
                                ELSE 'pending' END,
                   last_error = @Error,
                   leased_until = NULL,
                   finished_at = CASE WHEN @Error IS NULL OR attempts >= @Max THEN {Db.NowMs} END
             WHERE work_id = @WorkId
            """,
            new { WorkId = workId, Error = error, Max = MaxAttempts },
            cancellationToken: ct));
    }

    /// <summary>
    /// Hand an item back untouched: the worker never got to try, so the
    /// attempt does not count.
    /// </summary>
    public async Task ReleaseAsync(long workId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE work_item
               SET state = 'pending', attempts = max(0, attempts - 1), leased_until = NULL
             WHERE work_id = @WorkId AND state = 'running'
            """,
            new { WorkId = workId },
            cancellationToken: ct));
    }

    /// <summary>
    /// Give up on an item without retrying - the answer is known and it is
    /// "cannot": the file is gone, the format is not supported here. Kept as
    /// failed with the reason, so the number on the Library page is honest.
    /// </summary>
    public async Task AbandonAsync(long workId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE work_item
               SET state = 'failed', last_error = @Reason, leased_until = NULL, finished_at = {Db.NowMs}
             WHERE work_id = @WorkId
            """,
            new { WorkId = workId, Reason = reason },
            cancellationToken: ct));
    }

    /// <summary>Queue one item, unless one is already open or has failed for good.</summary>
    public async Task<bool> EnqueueAsync(string kind, long contentId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT @Kind, @ContentId
             WHERE NOT EXISTS (SELECT 1 FROM work_item w
                                WHERE w.kind = @Kind AND w.content_id = @ContentId
                                  AND w.state IN ('pending', 'running', 'failed'))
            ON CONFLICT DO NOTHING
            """,
            new { Kind = kind, ContentId = contentId },
            cancellationToken: ct)) > 0;
    }
}
