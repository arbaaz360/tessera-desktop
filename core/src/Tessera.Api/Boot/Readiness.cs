namespace Tessera.Api.Boot;

/// <summary>
/// Whether the core may take requests: the database is migrated and the
/// workers are running. <c>/health/live</c> ignores it; <c>/health/ready</c>
/// is it.
/// </summary>
public sealed class Readiness
{
    private int _ready;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void MarkReady() => Volatile.Write(ref _ready, 1);
}
