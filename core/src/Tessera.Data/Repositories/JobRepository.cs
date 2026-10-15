using Dapper;
using Npgsql;

namespace Tessera.Data.Repositories;

/// <summary>The background work this system actually does.</summary>
/// <remarks>
/// Two, not nineteen. A control on a page that moves nothing is worse than an
/// absent one, because someone will press it and believe it worked.
/// </remarks>
public static class JobNames
{
    /// <summary>Make the thumbnails that are missing.</summary>
    public const string Thumbnails = "thumbnailGeneration";

    /// <summary>Walk the scan roots and reconcile them against the database.</summary>
    public const string LibraryScan = "library";

    /// <summary>Name the track playing in each video. docs/contracts/music.md.</summary>
    public const string Music = "music";

    /// <summary>FaceSearch's queued ingest requests, drained one at a time while switched on.</summary>
    public const string Faces = "faces";

    /// <summary>Foundation's resident runner (SigLIP2, DINOv3, observations) - a switch.</summary>
    public const string Embeddings = "embeddings";

    public static readonly IReadOnlyList<string> All = [Thumbnails, LibraryScan, Music, Faces, Embeddings];

    /// <summary>The <c>work_item.kind</c> each queued job uses.</summary>
    public const string ThumbnailKind = "thumbnail";
    public const string MusicKind = "music";

    public static bool IsKnown(string? name) =>
        name is not null && All.Contains(name, StringComparer.Ordinal);
}

/// <param name="Active">Leased and being worked right now.</param>
/// <param name="Waiting">Queued and not yet started.</param>
/// <param name="Failed">Exhausted their attempts.</param>
/// <param name="Completed">Finished successfully.</param>
public readonly record struct JobStatistics(long Active, long Waiting, long Failed, long Completed);

public sealed record JobState(string Name, bool IsPaused, int Concurrency, JobStatistics Statistics);

/// <summary>
/// Reads what the jobs are doing, and holds the settings that steer them.
/// </summary>
/// <remarks>
/// <para>
/// The counts are DERIVED from the tables that hold the work, never
/// accumulated into counters beside it. A counter incremented next to the work
/// is a second copy of that work, and the two part company the first moment a
/// process dies between finishing a file and recording that it did.
/// </para>
/// <para>
/// Only settings are stored: paused, and how many at once.
/// </para>
/// </remarks>
public sealed class JobRepository(Db db)
{
    public async Task<IReadOnlyList<JobState>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        var controls = (await conn.QueryAsync<(string Name, bool IsPaused, int Concurrency)>(
            new CommandDefinition("SELECT name, is_paused, concurrency FROM job_control", cancellationToken: ct)))
            .ToDictionary(r => r.Name, r => (r.IsPaused, r.Concurrency), StringComparer.Ordinal);

        var thumbs = await QueueStatsAsync(conn, JobNames.ThumbnailKind, ct);
        var music = await QueueStatsAsync(conn, JobNames.MusicKind, ct);

        // A scan has no queue of its own: it is running or it is not, and what
        // it did last time lives in scan_run. "Waiting" is the number of roots
        // enabled but not currently being walked, which is the only thing a
        // person could act on from that page.
        var scans = await conn.QuerySingleAsync<JobStatistics>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM scan_run WHERE finished_at IS NULL)                    AS Active,
                   0::bigint                                                                    AS Waiting,
                   (SELECT count(*) FROM scan_run WHERE errors > 0 AND finished_at IS NOT NULL) AS Failed,
                   (SELECT count(*) FROM scan_run WHERE finished_at IS NOT NULL AND NOT cancelled) AS Completed
            """, cancellationToken: ct));

        // Faces and embeddings hold no work here; their numbers are read from
        // FaceSearch and Foundation by the endpoint that lists these.
        var none = new JobStatistics(0, 0, 0, 0);
        return
        [
            Build(JobNames.Thumbnails, controls, thumbs),
            Build(JobNames.LibraryScan, controls, scans),
            Build(JobNames.Music, controls, music),
            Build(JobNames.Faces, controls, none),
            Build(JobNames.Embeddings, controls, none),
        ];

        static async Task<JobStatistics> QueueStatsAsync(NpgsqlConnection conn, string kind, CancellationToken ct) =>
            await conn.QuerySingleAsync<JobStatistics>(new CommandDefinition(
                """
                SELECT count(*) FILTER (WHERE state = 'running') AS Active,
                       count(*) FILTER (WHERE state = 'pending') AS Waiting,
                       count(*) FILTER (WHERE state = 'failed')  AS Failed,
                       count(*) FILTER (WHERE state = 'done')    AS Completed
                  FROM work_item WHERE kind = @Kind
                """, new { Kind = kind }, cancellationToken: ct));

        // The row is seeded by migration 0001; its absence means the schema
        // this build expects is not the schema it is running against. Saying
        // so beats inventing "not paused, one worker" and looking healthy.
        static JobState Build(
            string name,
            Dictionary<string, (bool IsPaused, int Concurrency)> controls,
            JobStatistics stats) =>
            controls.TryGetValue(name, out var c)
                ? new JobState(name, c.IsPaused, c.Concurrency, stats)
                : throw new InvalidOperationException(
                    $"job_control has no row for '{name}'; the schema migrations have not run.");
    }

    public async Task<bool> SetPausedAsync(string name, bool paused, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE job_control SET is_paused = @Paused, updated_at = now() WHERE name = @Name",
            new { Name = name, Paused = paused }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> IsPausedAsync(string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT coalesce((SELECT is_paused FROM job_control WHERE name = @Name), false)",
            new { Name = name }, cancellationToken: ct));
    }

    /// <summary>How many files a job may work on at once.</summary>
    public async Task<int> ConcurrencyAsync(string name, int fallback, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT coalesce((SELECT concurrency FROM job_control WHERE name = @Name), @Fallback)",
            new { Name = name, Fallback = fallback }, cancellationToken: ct));
    }

    /// <summary>
    /// Set how many files a job works on at once.
    /// </summary>
    /// <remarks>
    /// Bounded by the CHECK on the column rather than here, so the limit holds
    /// for anything writing the table and not only for callers that came
    /// through this method.
    /// </remarks>
    public async Task<bool> SetConcurrencyAsync(string name, int concurrency, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE job_control SET concurrency = @Concurrency, updated_at = now() WHERE name = @Name",
            new { Name = name, Concurrency = concurrency }, cancellationToken: ct)) > 0;
    }

    /// <summary>The queue kind behind a job, or null for a job with no queue (the scan).</summary>
    public static string? KindOf(string name) => name switch
    {
        JobNames.Thumbnails => JobNames.ThumbnailKind,
        JobNames.Music => JobNames.MusicKind,
        _ => null,
    };

    /// <summary>Forget failed work so it can be queued again.</summary>
    public async Task<int> ClearFailedAsync(string name, CancellationToken ct)
    {
        if (KindOf(name) is not { } kind)
        {
            return 0;
        }

        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_item WHERE kind = @Kind AND state = 'failed'",
            new { Kind = kind }, cancellationToken: ct));
    }

    /// <summary>Drop work that has not started. Anything running is left alone.</summary>
    public async Task<int> EmptyAsync(string name, CancellationToken ct)
    {
        if (KindOf(name) is not { } kind)
        {
            return 0;
        }

        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM work_item WHERE kind = @Kind AND state = 'pending'",
            new { Kind = kind }, cancellationToken: ct));
    }

    /// <summary>
    /// Queue a thumbnail for every content that has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="force"/> re-queues content that already HAS a thumbnail,
    /// which is the only honest meaning of force here: the file on disk is
    /// regenerated from the original rather than trusted.
    /// </para>
    /// <para>
    /// Content that lives only in a corpus account is left out. The corpus
    /// accounts have no thumbnails on purpose, and before account.kind existed
    /// this query would have made 852,227 of them - and on 2026-09-11 it did,
    /// from a test, until it was stopped. A picture pulled from a corpus into
    /// a personal album still gets one, through AlbumRepository.AddAsync.
    /// </para>
    /// </remarks>
    public async Task<int> QueueThumbnailsAsync(bool force, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO work_item (kind, content_id)
            SELECT 'thumbnail', c.content_id
              FROM content c
             WHERE (@Force OR NOT EXISTS (SELECT 1 FROM thumbnail t
                                           WHERE t.content_id = c.content_id AND t.kind = 'thumb'))
               AND EXISTS (SELECT 1 FROM location l
                             JOIN account a ON a.account_id = l.account_id
                            WHERE l.content_id = c.content_id AND l.gone_at IS NULL
                              AND a.kind <> 'corpus')
            ON CONFLICT DO NOTHING
            """,
            new { Force = force }, commandTimeout: 1800, cancellationToken: ct));
    }
}
