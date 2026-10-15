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
/// worker and the music worker used to each carry their own copy of the lease
/// and completion statements; two copies of "what does it mean to take a job"
/// is how one of them ends up retrying for ever while the other gives up.
/// </para>
/// <para>
/// Work is LEASED, not claimed: a row is marked running with a deadline, so a
/// worker that dies mid-job releases it instead of parking it for ever.
/// <c>SKIP LOCKED</c> lets several workers drain one queue without handing out
/// the same row twice. A job fails for good after <see cref="MaxAttempts"/>.
/// </para>
/// </remarks>
public sealed class WorkItems(Db db)
{
    public const int MaxAttempts = 3;

    /// <summary>Take up to <paramref name="take"/> pending items of one kind.</summary>
    public async Task<IReadOnlyList<Leased>> LeaseAsync(string kind, int take, TimeSpan lease, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Leased>(new CommandDefinition(
            """
            WITH claimed AS (
                SELECT work_id FROM work_item
                 WHERE kind = @Kind
                   AND (state = 'pending' OR (state = 'running' AND leased_until < now()))
                 ORDER BY enqueued_at
                 LIMIT @Take
                 FOR UPDATE SKIP LOCKED
            )
            UPDATE work_item w
               SET state = 'running', attempts = w.attempts + 1, leased_until = now() + @Lease
              FROM claimed
              JOIN content c ON c.content_id = (SELECT content_id FROM work_item x WHERE x.work_id = claimed.work_id)
             WHERE w.work_id = claimed.work_id
            RETURNING w.work_id AS WorkId, w.content_id AS ContentId, c.sha256 AS Sha256,
                      c.mime AS Mime, w.attempts AS Attempts,
                      (SELECT r.abs_path FROM location l JOIN scan_root r ON r.root_id = l.root_id
                        WHERE l.content_id = w.content_id AND l.gone_at IS NULL
                        ORDER BY l.location_id LIMIT 1) AS Root,
                      (SELECT l.rel_path FROM location l
                        WHERE l.content_id = w.content_id AND l.gone_at IS NULL
                        ORDER BY l.location_id LIMIT 1) AS Rel
            """,
            new { Kind = kind, Take = take, Lease = lease },
            cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Record the outcome. A null error is success; an error is retried until
    /// the attempts run out, then kept as failed with the last message.
    /// </summary>
    public async Task CompleteAsync(long workId, string? error, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE work_item
               SET state = CASE WHEN @Error IS NULL THEN 'done'
                                WHEN attempts >= @Max THEN 'failed'
                                ELSE 'pending' END,
                   last_error = @Error,
                   leased_until = NULL,
                   finished_at = CASE WHEN @Error IS NULL OR attempts >= @Max THEN now() END
             WHERE work_id = @WorkId
            """,
            new { WorkId = workId, Error = error, Max = MaxAttempts },
            cancellationToken: ct));
    }

    /// <summary>
    /// Hand an item back untouched: the worker never got to try, so the
    /// attempt does not count. For "nobody can do this right now" - every
    /// identifier lane resting - as opposed to "this item failed".
    /// </summary>
    public async Task ReleaseAsync(long workId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE work_item
               SET state = 'pending', attempts = greatest(0, attempts - 1), leased_until = NULL
             WHERE work_id = @WorkId AND state = 'running'
            """,
            new { WorkId = workId },
            cancellationToken: ct));
    }

    /// <summary>
    /// Give up on an item without retrying - the answer is known and it is
    /// "cannot": the file is gone, the format has no audio. Kept as failed
    /// with the reason, so the number on the jobs page is honest.
    /// </summary>
    public async Task AbandonAsync(long workId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE work_item
               SET state = 'failed', last_error = @Reason, leased_until = NULL, finished_at = now()
             WHERE work_id = @WorkId
            """,
            new { WorkId = workId, Reason = reason },
            cancellationToken: ct));
    }
}
