using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Tessera.Data.Repositories;

namespace Tessera.Api.Import;

/// <summary>One thing to import: a path (file or folder) or a link.</summary>
public sealed record ImportInput(string Kind, string Value);

/// <summary>
/// Imports, one after another, each a job with progress and a per-item
/// answer (<c>POST /api/import</c>, <c>GET /api/import/{id}</c>).
/// </summary>
/// <remarks>
/// <para>
/// A drop returns at once with the job; the items are worked through here,
/// in the order given, and the job is read back while it runs. Jobs run one
/// at a time because a folder in a drop waits for its scan, and scans run one
/// at a time anyway. The counts go to <c>import_run</c> after every item, so
/// <c>GET /api/library</c> can show the last import's numbers even after a
/// restart; the per-item answers live in memory for the newest jobs.
/// </para>
/// <para>
/// Bytes handed over by the webview (<see cref="RunBytesAsync"/>) do not
/// queue: the request carries them, so the request does the work.
/// </para>
/// </remarks>
public sealed class ImportRunner(Importer importer, ImportRepository runs, ILogger<ImportRunner> log) : BackgroundService
{
    private const int Kept = 50;

    private readonly Channel<(ImportJob Job, IReadOnlyList<ImportInput> Inputs)> _queue =
        Channel.CreateUnbounded<(ImportJob, IReadOnlyList<ImportInput>)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<long, ImportJob> _jobs = new();

    /// <summary>Jobs queued or running.</summary>
    public int Running => _jobs.Values.Count(j => !j.Finished);

    public ImportJob? Get(long id) => _jobs.GetValueOrDefault(id);

    /// <summary>Queue an import. Returns the job straight away, every item pending.</summary>
    public async Task<ImportJob> EnqueueAsync(
        long accountId, long? albumId, string folderName, IReadOnlyList<ImportInput> inputs, CancellationToken ct)
    {
        var id = await runs.CreateAsync(albumId, inputs.Count, ct);
        var job = new ImportJob(id, accountId, albumId, folderName, inputs.Select(i => (i.Value, i.Kind)).ToList());
        Keep(job);
        await _queue.Writer.WriteAsync((job, inputs), ct);
        log.ImportQueued(id, inputs.Count, albumId);
        return job;
    }

    /// <summary>Bytes from the webview: one item, done in the caller's request.</summary>
    public async Task<ImportJob> RunBytesAsync(
        long accountId, long? albumId, string folderName, Stream body, string? name, CancellationToken ct)
    {
        var id = await runs.CreateAsync(albumId, 1, ct);
        var job = new ImportJob(id, accountId, albumId, folderName, [(name ?? "", ImportKind.Upload)]);
        Keep(job);
        var target = new ImportTarget(accountId, albumId, folderName);
        try
        {
            var result = await importer.BytesAsync(body, name, target, ct);
            var (done, counts) = job.Record(0, result);
            job.Finish(cancelled: false);
            await runs.UpdateAsync(id, done, counts, finished: true, cancelled: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            job.Finish(cancelled: true);
            await runs.UpdateAsync(id, 0, default, finished: true, cancelled: true, CancellationToken.None);
            throw;
        }

        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (job, inputs) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await RunAsync(job, inputs, stoppingToken);
        }
    }

    private async Task RunAsync(ImportJob job, IReadOnlyList<ImportInput> inputs, CancellationToken ct)
    {
        var target = new ImportTarget(job.AccountId, job.AlbumId, job.FolderName);
        var clock = Stopwatch.StartNew();
        var done = 0;
        ImportCounts counts = default;
        var cancelled = false;

        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var result = input.Kind == ImportKind.Url
                    ? await importer.UrlAsync(input.Value, target, ct)
                    : await importer.PathAsync(input.Value, target, ct);

                (done, counts) = job.Record(i, result);
                await runs.UpdateAsync(job.Id, done, counts, finished: false, cancelled: false, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            job.Finish(cancelled);
            await runs.UpdateAsync(job.Id, done, counts, finished: true, cancelled, CancellationToken.None);
            log.ImportFinished(job.Id, counts.Added, counts.Already, counts.Copied, counts.Skipped, counts.Folders,
                               cancelled, clock.ElapsedMilliseconds);
        }
    }

    private void Keep(ImportJob job)
    {
        _jobs[job.Id] = job;
        foreach (var old in _jobs.Keys.OrderByDescending(k => k).Skip(Kept))
        {
            if (_jobs.TryGetValue(old, out var j) && j.Finished)
            {
                _jobs.TryRemove(old, out _);
            }
        }
    }
}
