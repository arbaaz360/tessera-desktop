using Dapper;

namespace Tessera.Data.Repositories;

/// <summary>What one import did, in numbers.</summary>
/// <param name="Added">New pictures indexed where they are (inside a root).</param>
/// <param name="Already">The same bytes were in the library already; nothing copied.</param>
/// <param name="Copied">Copied into the Tessera folder from outside every root.</param>
/// <param name="Skipped">Not a picture, unreadable, failed; each with its reason.</param>
/// <param name="Folders">Folders indexed in place, never copied.</param>
public readonly record struct ImportCounts(int Added, int Already, int Copied, int Skipped, int Folders);

/// <summary>An import as <c>import_run</c> records it.</summary>
public sealed record ImportRun
{
    public long ImportId { get; init; }
    public long? AlbumId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int Total { get; init; }
    public int Done { get; init; }
    public int Added { get; init; }
    public int Already { get; init; }
    public int Copied { get; init; }
    public int Skipped { get; init; }
    public int Folders { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>
/// The ledger of imports: one row per drop, pick or folder add, with its
/// counts, so the Library page can say what the last one did after a restart.
/// The per-file answers are the job's, in memory, while the core runs.
/// </summary>
public sealed class ImportRepository(Db db)
{
    public async Task<long> CreateAsync(long? albumId, int total, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO import_run (album_id, total) VALUES (@AlbumId, @Total) RETURNING import_id",
            new { AlbumId = albumId, Total = total }, cancellationToken: ct));
    }

    public async Task UpdateAsync(long importId, int done, ImportCounts c, bool finished, bool cancelled, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE import_run
               SET done = @Done, added = @Added, already = @Already, copied = @Copied,
                   skipped = @Skipped, folders = @Folders, cancelled = @Cancelled,
                   finished_at = CASE WHEN @Finished THEN {Db.NowMs} ELSE finished_at END
             WHERE import_id = @ImportId
            """,
            new
            {
                ImportId = importId, Done = done, c.Added, c.Already, c.Copied, c.Skipped, c.Folders,
                Finished = finished, Cancelled = cancelled,
            },
            cancellationToken: ct));
    }

    public async Task<ImportRun?> GetAsync(long importId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ImportRun>(new CommandDefinition(
            Columns + " WHERE import_id = @ImportId", new { ImportId = importId }, cancellationToken: ct));
    }

    /// <summary>The newest import, running or finished; null when there has been none.</summary>
    public async Task<ImportRun?> LatestAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ImportRun>(new CommandDefinition(
            Columns + " ORDER BY import_id DESC LIMIT 1", cancellationToken: ct));
    }

    /// <summary>At start: an import left unfinished belonged to a process that died.</summary>
    public async Task<int> MarkInterruptedAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE import_run SET finished_at = {Db.NowMs}, cancelled = 1 WHERE finished_at IS NULL",
            cancellationToken: ct));
    }

    private const string Columns = """
        SELECT import_id AS ImportId, album_id AS AlbumId, started_at AS StartedAt, finished_at AS FinishedAt,
               total AS Total, done AS Done, added AS Added, already AS Already, copied AS Copied,
               skipped AS Skipped, folders AS Folders, cancelled AS Cancelled
          FROM import_run
        """;
}
