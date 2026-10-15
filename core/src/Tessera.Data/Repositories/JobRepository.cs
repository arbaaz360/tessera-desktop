using Dapper;

namespace Tessera.Data.Repositories;

/// <summary>The background work this app actually does.</summary>
public static class JobNames
{
    /// <summary>Make the thumbnails that are missing.</summary>
    public const string Thumbnails = "thumbnailGeneration";

    /// <summary>Walk the roots and reconcile them against the database.</summary>
    public const string LibraryScan = "library";

    /// <summary>Turn thumbnails into vectors.</summary>
    public const string Embeddings = "embeddings";

    public static readonly IReadOnlyList<string> All = [Thumbnails, LibraryScan, Embeddings];
}

/// <param name="Active">Leased and being worked right now.</param>
/// <param name="Waiting">Queued and not yet started.</param>
/// <param name="Failed">Exhausted their attempts, or abandoned with a reason.</param>
/// <param name="Completed">Finished successfully.</param>
public readonly record struct JobStatistics(long Active, long Waiting, long Failed, long Completed);

public sealed record JobState(string Name, bool IsPaused, int Concurrency, JobStatistics Statistics);

/// <summary>What the Library page shows for one derived thing: how many are done, waiting, failed.</summary>
public readonly record struct DerivedCounts(long Pending, long Done, long Failed);

/// <summary>
/// Reads what the jobs are doing, holds the settings that steer them, and
/// queues work set-wise.
/// </summary>
/// <remarks>
/// The counts are DERIVED from the tables that hold the work, never
/// accumulated into counters beside it. A counter incremented next to the
/// work is a second copy of that work, and the two part company the first
/// moment a process dies between finishing a file and recording that it did.
/// </remarks>
public sealed class JobRepository(Db db)
{
    public async Task<IReadOnlyList<JobState>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        var controls = (await conn.QueryAsync<(string Name, bool IsPaused, int Concurrency)>(
            new CommandDefinition("SELECT name, is_paused, concurrency FROM job_control", cancellationToken: ct)))
            .ToDictionary(r => r.Name, r => (r.IsPaused, r.Concurrency), StringComparer.Ordinal);

        var thumbs = await QueueStatsAsync(conn, WorkItems.ThumbnailKind, ct);
        var embeddings = await QueueStatsAsync(conn, WorkItems.EmbeddingKind, ct);

        var scans = await conn.QuerySingleAsync<JobStatistics>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM scan_run WHERE finished_at IS NULL)                        AS Active,
                   0                                                                                AS Waiting,
                   (SELECT count(*) FROM scan_run WHERE errors > 0 AND finished_at IS NOT NULL)     AS Failed,
                   (SELECT count(*) FROM scan_run WHERE finished_at IS NOT NULL AND cancelled = 0)  AS Completed
            """, cancellationToken: ct));

        return
        [
            Build(JobNames.Thumbnails, controls, thumbs),
            Build(JobNames.LibraryScan, controls, scans),
            Build(JobNames.Embeddings, controls, embeddings),
        ];

        static async Task<JobStatistics> QueueStatsAsync(System.Data.Common.DbConnection conn, string kind, CancellationToken ct) =>
            await conn.QuerySingleAsync<JobStatistics>(new CommandDefinition(
                """
                SELECT count(*) FILTER (WHERE state = 'running') AS Active,
                       count(*) FILTER (WHERE state = 'pending') AS Waiting,
                       count(*) FILTER (WHERE state = 'failed')  AS Failed,
                       count(*) FILTER (WHERE state = 'done')    AS Completed
                  FROM work_item WHERE kind = @Kind
                """, new { Kind = kind }, cancellationToken: ct));

        static JobState Build(
            string name,
            Dictionary<string, (bool IsPaused, int Concurrency)> controls,
            JobStatistics stats) =>
            controls.TryGetValue(name, out var c)
                ? new JobState(name, c.IsPaused, c.Concurrency, stats)
                : throw new InvalidOperationException(
                    $"job_control has no row for '{name}'; the schema migrations have not run.");
    }

    public async Task<bool> IsPausedAsync(string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT coalesce((SELECT is_paused FROM job_control WHERE name = @Name), 0)",
            new { Name = name }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> SetPausedAsync(string name, bool paused, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE job_control SET is_paused = @Paused, updated_at = {Db.NowMs} WHERE name = @Name",
            new { Name = name, Paused = paused }, cancellationToken: ct)) > 0;
    }

    /// <summary>How many files a job may work on at once.</summary>
    public async Task<int> ConcurrencyAsync(string name, int fallback, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT coalesce((SELECT concurrency FROM job_control WHERE name = @Name), @Fallback)",
            new { Name = name, Fallback = fallback }, cancellationToken: ct));
    }

    /// <summary>Set how many files a job works on at once. Bounded by the CHECK on the column.</summary>
    public async Task<bool> SetConcurrencyAsync(string name, int concurrency, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE job_control SET concurrency = @Concurrency, updated_at = {Db.NowMs} WHERE name = @Name",
            new { Name = name, Concurrency = concurrency }, cancellationToken: ct)) > 0;
    }

    /// <summary>Forget failed work of one kind so it can be queued again.</summary>
    public async Task<int> ClearFailedAsync(string kind, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_item WHERE kind = @Kind AND state = 'failed'",
            new { Kind = kind }, cancellationToken: ct));
    }

    /// <summary>
    /// Queue a thumbnail for every live image that has none and is within
    /// the cap. Set-based: a library of a million is one statement.
    /// </summary>
    public async Task<int> QueueThumbnailsAsync(long? maxContentId, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT 'thumbnail', c.content_id
              FROM content c
             WHERE c.mime LIKE 'image/%'
               AND (@MaxId IS NULL OR c.content_id <= @MaxId)
               AND NOT EXISTS (SELECT 1 FROM thumbnail t WHERE t.content_id = c.content_id AND t.kind = 'thumb')
               AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = c.content_id AND l.gone_at IS NULL)
               AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = c.content_id AND w.kind = 'thumbnail'
                                                          AND w.state IN ('pending', 'running', 'failed'))
            ON CONFLICT DO NOTHING
            """,
            new { MaxId = maxContentId }, commandTimeout: 1800, cancellationToken: ct));
    }

    /// <summary>Queue an embedding for every content that has a thumbnail, no vector for this model, and is within the cap.</summary>
    public async Task<int> QueueEmbeddingsAsync(string model, long? maxContentId, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT 'embedding', t.content_id
              FROM thumbnail t
             WHERE t.kind = 'thumb'
               AND (@MaxId IS NULL OR t.content_id <= @MaxId)
               AND NOT EXISTS (SELECT 1 FROM embedding e WHERE e.content_id = t.content_id AND e.model = @Model)
               AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = t.content_id AND l.gone_at IS NULL)
               AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = t.content_id AND w.kind = 'embedding'
                                                          AND w.state IN ('pending', 'running', 'failed'))
            ON CONFLICT DO NOTHING
            """,
            new { Model = model, MaxId = maxContentId }, commandTimeout: 1800, cancellationToken: ct));
    }

    /// <summary>Thumbnails: how many live images have one, are waiting for one, or could not get one.</summary>
    public async Task<DerivedCounts> ThumbnailCountsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<DerivedCounts>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM content c
                     WHERE c.mime LIKE 'image/%'
                       AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = c.content_id AND l.gone_at IS NULL)
                       AND NOT EXISTS (SELECT 1 FROM thumbnail t WHERE t.content_id = c.content_id AND t.kind = 'thumb')
                       AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = c.content_id
                                        AND w.kind = 'thumbnail' AND w.state = 'failed')) AS Pending,
                   (SELECT count(*) FROM thumbnail t
                     WHERE t.kind = 'thumb'
                       AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = t.content_id AND l.gone_at IS NULL)) AS Done,
                   (SELECT count(*) FROM work_item w WHERE w.kind = 'thumbnail' AND w.state = 'failed') AS Failed
            """, cancellationToken: ct));
    }

    /// <summary>Embeddings for one model: done, waiting (has a thumbnail, no vector), failed.</summary>
    public async Task<DerivedCounts> EmbeddingCountsAsync(string model, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<DerivedCounts>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM thumbnail t
                     WHERE t.kind = 'thumb'
                       AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = t.content_id AND l.gone_at IS NULL)
                       AND NOT EXISTS (SELECT 1 FROM embedding e WHERE e.content_id = t.content_id AND e.model = @Model)
                       AND NOT EXISTS (SELECT 1 FROM work_item w WHERE w.content_id = t.content_id
                                        AND w.kind = 'embedding' AND w.state = 'failed')) AS Pending,
                   (SELECT count(*) FROM embedding e
                     WHERE e.model = @Model
                       AND EXISTS (SELECT 1 FROM location l WHERE l.content_id = e.content_id AND l.gone_at IS NULL)) AS Done,
                   (SELECT count(*) FROM work_item w WHERE w.kind = 'embedding' AND w.state = 'failed') AS Failed
            """, new { Model = model }, cancellationToken: ct));
    }
}
