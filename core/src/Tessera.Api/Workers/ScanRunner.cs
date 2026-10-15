using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Dapper;
using Tessera.Data;
using Tessera.Data.Repositories;

namespace Tessera.Api.Workers;

/// <summary>
/// Scans, one after another.
/// </summary>
/// <remarks>
/// <para>
/// A root is scanned when a person adds it or asks, and a folder when the
/// watch sees it change. Both are queued here and run in order, because the
/// scanner walks one disk at a time (<see cref="Scanner"/>). The run row is
/// opened the moment a scan is queued, so the Library page shows it as
/// running from the first second and the caller gets its id straight back
/// (CONTRACT §4: <c>202 {"runId": N}</c>).
/// </para>
/// <para>
/// One scan per (root, folder) at a time: asking again while one is queued or
/// running is a 409, not a second walk.
/// </para>
/// </remarks>
public sealed class ScanRunner(Scanner scanner, LibraryRepository library, Db db, ILogger<ScanRunner> log) : BackgroundService
{
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, long> _inFlight = new(StringComparer.Ordinal);

    private sealed record Request(long RootId, string? Folder, long RunId);

    /// <summary>A scan of exactly this root (whole root, not a folder) is queued or running.</summary>
    public bool IsRootScanning(long rootId) => _inFlight.ContainsKey(Key(rootId, null));

    /// <summary>Any scan touching this root - whole or one folder - is queued or running.</summary>
    public bool IsRootBusy(long rootId)
    {
        var prefix = rootId.ToString(CultureInfo.InvariantCulture) + "|";
        return _inFlight.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    public int Queued => _inFlight.Count;

    /// <summary>Queue a scan. Null when the same scan is already queued or running.</summary>
    public async Task<long?> EnqueueAsync(long rootId, string? folder, CancellationToken ct)
    {
        folder = Scanner.NormaliseFolder(folder);
        var key = Key(rootId, folder);
        if (!_inFlight.TryAdd(key, 0))
        {
            return null;
        }

        try
        {
            var runId = await library.CreateRunAsync(rootId, folder, ct);
            _inFlight[key] = runId;
            await _requests.Writer.WriteAsync(new Request(rootId, folder, runId), ct);
            return runId;
        }
        catch
        {
            _inFlight.TryRemove(key, out _);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var req in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await scanner.ScanAsync(req.RootId, req.Folder, req.RunId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await FailRunAsync(req.RunId, "interrupted: the core stopped", cancelled: true);
                break;
            }
            catch (Exception ex)
            {
                log.ScanFailed(ex, req.RootId);
                await FailRunAsync(req.RunId, ex.Message, cancelled: false);
            }
            finally
            {
                _inFlight.TryRemove(Key(req.RootId, req.Folder), out _);
            }
        }
    }

    /// <summary>A run that never got to walk still gets a finished row with the reason.</summary>
    private async Task FailRunAsync(long runId, string error, bool cancelled)
    {
        await using var conn = await db.OpenWriteAsync(CancellationToken.None);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE scan_run
               SET finished_at = {Db.NowMs}, errors = CASE WHEN @Cancelled THEN errors ELSE errors + 1 END,
                   error_sample = @Error, cancelled = @Cancelled
             WHERE run_id = @RunId AND finished_at IS NULL
            """,
            new { RunId = runId, Error = error, Cancelled = cancelled },
            cancellationToken: CancellationToken.None));
    }

    private static string Key(long rootId, string? folder) =>
        rootId.ToString(CultureInfo.InvariantCulture) + "|" + (folder ?? "");
}
