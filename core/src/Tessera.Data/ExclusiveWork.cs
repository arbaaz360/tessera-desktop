namespace Tessera.Data;

/// <summary>
/// Stops two heavy jobs from reading the library disk at once, structurally.
/// </summary>
/// <remarks>
/// <para>
/// The thumbnail worker reads originals and the scanner walks and hashes the
/// same disk. On a spinning disk run together they do not go twice as fast,
/// they halve each other. A rule stated in a document gets followed until
/// someone adds a third job; a lock gets followed always.
/// </para>
/// <para>
/// One process owns one library, so this is an in-process semaphore where the
/// HomeLab service used a Postgres advisory lock. A holder that dies takes
/// the process with it, which releases everything; nothing is left to clear
/// by hand.
/// </para>
/// <para>
/// FAIRNESS. The thumbnail worker takes the disk batch after batch with only
/// a moment between, and a scan that merely tries once never lands in that
/// moment. So a refusal is remembered (<see cref="Wanted"/>), and the long
/// holder steps aside between batches long enough for a poller to take it.
/// </para>
/// </remarks>
public sealed class ExclusiveWork : IDisposable
{
    private readonly SemaphoreSlim _disk = new(1, 1);
    private long _wantedTicks;

    /// <summary>Somebody was refused the disk within the last few minutes.</summary>
    public bool Wanted =>
        Interlocked.Read(ref _wantedTicks) is var t && t != 0 &&
        DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) < TimeSpan.FromMinutes(3);

    /// <summary>Who holds the disk right now, or null.</summary>
    public string? Holder { get; private set; }

    /// <summary>
    /// Take the disk, polling for up to <paramref name="wait"/> if it is held;
    /// null if it still is not free by then.
    /// </summary>
    public async Task<Holding?> TryTakeArchiveAsync(string holder, TimeSpan wait, CancellationToken ct)
    {
        if (await _disk.WaitAsync(wait, ct))
        {
            Holder = holder;
            return new Holding(this, holder);
        }

        Interlocked.Exchange(ref _wantedTicks, DateTime.UtcNow.Ticks);
        return null;
    }

    /// <summary>Take the disk now, or return null if another job holds it.</summary>
    public Task<Holding?> TryTakeArchiveAsync(string holder, CancellationToken ct) =>
        TryTakeArchiveAsync(holder, TimeSpan.Zero, ct);

    private void Release()
    {
        Holder = null;
        _disk.Release();
    }

    public void Dispose() => _disk.Dispose();

    /// <summary>A held lock. Disposing releases it, once.</summary>
    public sealed class Holding(ExclusiveWork owner, string holder) : IAsyncDisposable
    {
        private int _released;

        public string Holder { get; } = holder;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
