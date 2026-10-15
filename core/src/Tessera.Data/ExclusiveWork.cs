using Dapper;
using Npgsql;

namespace Tessera.Data;

/// <summary>
/// Stops two heavy jobs from running at once, structurally.
/// </summary>
/// <remarks>
/// <para>
/// The thumbnail worker reads originals off the archive disk and the library
/// scan walks and hashes the same disk. Run together they do not go twice as
/// fast, they halve each other - which is why this machine's standing rule is
/// one heavy batch job at a time. A rule stated in a document gets followed
/// until someone adds a third job; a lock gets followed always.
/// </para>
/// <para>
/// A Postgres ADVISORY LOCK, not a row or a file. It is held by the database
/// session, so a worker that crashes, is killed, or has its container removed
/// releases it the moment the connection drops. A lock table would still be
/// holding it, and somebody would have to find that out and clear it by hand -
/// at which point the lock has become the outage it was meant to prevent.
/// </para>
/// <para>
/// Try, do not block on the database: a job that cannot have the disk may poll
/// for a short while, but never holds a connection open waiting for the
/// length of someone else's scan.
/// </para>
/// <para>
/// FAIRNESS. The thumbnail worker takes the lock batch after batch with only a
/// moment between, and a scan that merely tries once never lands in that
/// moment - the watch asked six times in half an hour and lost every time
/// while two thousand thumbnails queued behind the scan it could not run.
/// So a refusal is remembered (<see cref="Wanted"/>), and a long-running
/// holder that sees someone wanted the disk recently steps aside between
/// batches long enough for a poller to take it.
/// </para>
/// </remarks>
public sealed class ExclusiveWork(Db db)
{
    /// <summary>Fixed and ours. Anything reading the archive in bulk takes this.</summary>
    private const long ArchiveDiskKey = 8_147_203_591_004_777L;

    private long _wantedTicks;

    /// <summary>Somebody was refused the disk within the last few minutes.</summary>
    public bool Wanted =>
        Interlocked.Read(ref _wantedTicks) is var t && t != 0 &&
        DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) < TimeSpan.FromMinutes(3);

    /// <summary>
    /// Take the archive lock, polling for up to <paramref name="wait"/> if it
    /// is held; null if it still is not free by then.
    /// </summary>
    public async Task<Holding?> TryTakeArchiveAsync(string holder, TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (await TryTakeArchiveAsync(holder, ct) is { } held)
            {
                return held;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>
    /// Take the archive lock, or return null if another job holds it.
    /// </summary>
    /// <remarks>
    /// The returned handle owns its own connection, because an advisory lock
    /// lives on the session that took it. Dispose it - or die - and the lock
    /// is gone.
    /// </remarks>
    public async Task<Holding?> TryTakeArchiveAsync(string holder, CancellationToken ct)
    {
        var conn = await db.OpenAsync(ct);
        try
        {
            var got = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(@Key)", new { Key = ArchiveDiskKey }, cancellationToken: ct));

            if (!got)
            {
                Interlocked.Exchange(ref _wantedTicks, DateTime.UtcNow.Ticks);
                await conn.DisposeAsync();
                return null;
            }

            return new Holding(conn, ArchiveDiskKey, holder);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>A held lock. Disposing releases it; so does losing the connection.</summary>
    public sealed class Holding(NpgsqlConnection conn, long key, string holder) : IAsyncDisposable
    {
        public string Holder { get; } = holder;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await conn.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = key });
            }
            catch (NpgsqlException)
            {
                // The connection is already gone, which released the lock. The
                // whole point of using a session lock is that this case needs
                // no cleanup.
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
