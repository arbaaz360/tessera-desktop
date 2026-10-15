using Dapper;
using Tessera.Api.Auth;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

public static class AdminEndpoints
{
    public sealed record NewApiKey(long AccountId, string Name);

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").WithTags("admin").RequireAdmin();

        g.MapGet("/accounts", async (AccountRepository accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListAsync(ct)));

        // An admin can mint a key for any account. The previous system made keys
        // self-service only, so connecting a service to an account required
        // knowing a person's password.
        g.MapPost("/api-keys", async (
            NewApiKey body, AccountRepository accounts, CancellationToken ct) =>
        {
            var secret = await accounts.CreateApiKeyAsync(body.AccountId, body.Name, ct);
            return Results.Ok(new { secret, note = "Shown once. It is stored hashed." });
        });

        g.MapGet("/roots", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await conn.QueryAsync(new CommandDefinition(
                """
                SELECT r.root_id AS id, r.label, r.abs_path, r.enabled, a.email AS account,
                       (SELECT count(*) FROM location l
                         WHERE l.root_id = r.root_id AND l.gone_at IS NULL) AS live_files
                  FROM scan_root r JOIN account a ON a.account_id = r.account_id
                 ORDER BY r.label
                """,
                cancellationToken: ct)));
        });

        g.MapGet("/scans", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await conn.QueryAsync(new CommandDefinition(
                """
                SELECT s.run_id, r.label, s.started_at, s.finished_at, s.files_seen,
                       s.added, s.moved, s.unchanged, s.marked_gone, s.errors,
                       s.error_sample, s.cancelled
                  FROM scan_run s JOIN scan_root r ON r.root_id = s.root_id
                 ORDER BY s.started_at DESC LIMIT 50
                """,
                cancellationToken: ct)));
        });

        // ------------------------------------------------------------- drift
        //
        // THE ENDPOINT THIS WHOLE PROJECT EXISTS FOR.
        //
        // Every expensive failure in the system this replaces was silent: four
        // containers reported healthy for eighteen days while every run died on
        // startup; a thumbnail cache drifted in both directions at once; three
        // stores held records for files deleted months earlier. None of them
        // could answer "am I in sync?" without someone writing a bespoke script.
        //
        // So the answer is a route. It reports NUMBERS, never a green tick: a
        // drift of 3 and a drift of 300,000 must not look the same.
        g.MapGet("/drift", async (Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var d = await conn.QuerySingleAsync(new CommandDefinition(
                """
                SELECT
                  (SELECT count(*) FROM content)                                     AS content_rows,
                  (SELECT count(*) FROM location WHERE gone_at IS NULL)              AS live_locations,
                  (SELECT count(*) FROM location WHERE gone_at IS NOT NULL)          AS gone_locations,
                  (SELECT count(*) FROM location WHERE trashed_at IS NOT NULL
                                                   AND gone_at IS NULL)              AS trashed,
                  (SELECT count(*) FROM timeline_entry)                              AS timeline_rows,
                  -- content that is live somewhere but has no thumbnail yet
                  (SELECT count(*) FROM content c
                    WHERE EXISTS (SELECT 1 FROM location l
                                   WHERE l.content_id = c.content_id AND l.gone_at IS NULL)
                      AND NOT EXISTS (SELECT 1 FROM thumbnail t
                                       WHERE t.content_id = c.content_id AND t.kind = 'thumb'))
                                                                                     AS missing_thumbnails,
                  -- a thumbnail whose content is gone entirely: the orphan case
                  (SELECT count(*) FROM thumbnail t
                    WHERE NOT EXISTS (SELECT 1 FROM content c
                                       WHERE c.content_id = t.content_id))           AS orphan_thumbnails,
                  (SELECT count(*) FROM work_item WHERE state = 'pending')           AS work_pending,
                  (SELECT count(*) FROM work_item WHERE state = 'failed')            AS work_failed,
                  (SELECT count(*) FROM duplicate_within_account)                    AS duplicate_sets_in_account,
                  (SELECT count(*) FROM duplicate_across_accounts)                   AS duplicate_sets_across_accounts
                """,
                cancellationToken: ct));

            return Results.Ok(d);
        });

        g.MapGet("/duplicates/within", async (Db db, int? limit, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await conn.QueryAsync(new CommandDefinition(
                """
                SELECT d.account_id, d.content_id, d.copies, d.paths
                  FROM duplicate_within_account d
                 ORDER BY d.copies DESC LIMIT @Take
                """,
                new { Take = Math.Clamp(limit ?? 200, 1, 5000) },
                cancellationToken: ct)));
        });

        g.MapGet("/duplicates/across", async (Db db, int? limit, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await conn.QueryAsync(new CommandDefinition(
                "SELECT content_id, accounts, copies FROM duplicate_across_accounts LIMIT @Take",
                new { Take = Math.Clamp(limit ?? 200, 1, 5000) },
                cancellationToken: ct)));
        });

        g.MapPost("/roots/{rootId:long}/sync-albums", async (
            long rootId, Db db, AlbumRepository albums, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            var accountId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT account_id FROM scan_root WHERE root_id = @RootId",
                new { RootId = rootId },
                cancellationToken: ct));

            if (accountId is null)
            {
                return Results.NotFound();
            }

            var (made, added) = await albums.SyncFoldersAsync(accountId.Value, rootId, ct);
            return Results.Ok(new { albums = made, itemsAdded = added });
        });
    }
}
