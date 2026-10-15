using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Api.Licence;
using Tessera.Api.Search;
using Tessera.Core;
using Tessera.Data;
using Tessera.Data.Repositories;
using Tessera.Desktop.Search;

namespace Tessera.Api.Workers;

/// <summary>
/// Turns thumbnails into vectors.
/// </summary>
/// <remarks>
/// <para>
/// For each content with a thumbnail and no vector for the current model:
/// decode the thumbnail once, <see cref="IImageEmbedder.EmbedImage"/>,
/// <see cref="IVectorIndex.Upsert"/>, and record the content in the
/// <c>embedding</c> ledger. The index is saved to
/// <c>vectors/&lt;modelId&gt;.bin</c> every minute while it changes, and on
/// shutdown (CONTRACT §6).
/// </para>
/// <para>
/// It waits for three things and says so in the status: the model to be
/// ready, the search switch to be on, and mains power unless the person said
/// battery is fine.
/// </para>
/// </remarks>
public sealed class EmbeddingWorker(
    Db db,
    WorkItems queue,
    JobRepository jobs,
    IImageEmbedder embedder,
    IVectorIndex index,
    VectorStore store,
    ModelReadiness model,
    SettingsRepository settings,
    PowerSource power,
    FreeTier tier,
    IOptions<StorageOptions> storage,
    ILogger<EmbeddingWorker> log) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(60);

    private long _embedded;
    private long _embedMs;

    /// <summary>Vectors made since start, and the time spent in them.</summary>
    public (long Embedded, long Milliseconds) Rate => (Interlocked.Read(ref _embedded), Interlocked.Read(ref _embedMs));

    /// <summary>Why the worker is not embedding right now, for the status answer. Null when it is.</summary>
    public string? Waiting { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        await store.LoadAsync(stoppingToken);
        await BackfillAsync(stoppingToken);

        var lastSave = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var did = await StepAsync(stoppingToken);

                if (store.Dirty && DateTimeOffset.UtcNow - lastSave > SaveEvery)
                {
                    await store.SaveAsync(stoppingToken);
                    lastSave = DateTimeOffset.UtcNow;
                }

                if (did == 0)
                {
                    await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.EmbeddingLoopFailed(ex);
                await Task.Delay(Idle, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The loop has stopped; whatever it made since the last save goes to disk now.
        await store.SaveAsync(CancellationToken.None);
    }

    /// <summary>Queue everything that has a thumbnail and no vector, within the cap. Called at start and when the licence changes.</summary>
    public async Task<int> BackfillAsync(CancellationToken ct)
    {
        var maxId = await tier.MaxContentIdAsync(ct);
        var queued = await jobs.QueueEmbeddingsAsync(embedder.ModelId, maxId, ct);
        if (queued > 0)
        {
            log.EmbeddingQueued(queued, embedder.ModelId);
        }

        return queued;
    }

    private async Task<int> StepAsync(CancellationToken ct)
    {
        var s = await settings.ReadAsync(ct);
        if (!s.SearchEnabled)
        {
            Waiting = "search is switched off";
            return 0;
        }

        if (!s.EmbedOnBattery && power.OnBattery)
        {
            Waiting = "on battery";
            return 0;
        }

        if (!embedder.IsReady)
        {
            Waiting = model.LastError is { } err ? $"model not ready: {err}" : "model not ready";
            _ = model.EnsureAsync();
            return 0;
        }

        if (await jobs.IsPausedAsync(JobNames.Embeddings, ct))
        {
            Waiting = "paused";
            return 0;
        }

        Waiting = null;
        return await DrainAsync(ct);
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        var batch = await queue.LeaseAsync(WorkItems.EmbeddingKind, 32, Lease, ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var made = 0;
        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EmbedOneAsync(item, ct);
                await queue.CompleteAsync(item.WorkId, null, ct);
                made++;
            }
            catch (UnsupportedFormatException ex)
            {
                await queue.AbandonAsync(item.WorkId, ex.Message, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await queue.CompleteAsync(item.WorkId, ex.Message, CancellationToken.None);
                log.EmbeddingFailed(ex, item.ContentId);
            }
        }

        Interlocked.Add(ref _embedded, made);
        Interlocked.Add(ref _embedMs, sw.ElapsedMilliseconds);
        log.EmbeddingBatch(made, batch.Count, sw.ElapsedMilliseconds, index.Count);
        return batch.Count;
    }

    private async Task EmbedOneAsync(Leased item, CancellationToken ct)
    {
        var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, item.Sha256, "thumb", "webp");
        if (!File.Exists(path))
        {
            throw new UnsupportedFormatException("no thumbnail to embed from");
        }

        using var bitmap = SkiaFiles.Decode(path)
            ?? throw new UnsupportedFormatException("thumbnail could not be decoded");

        var vector = embedder.EmbedImage(bitmap);
        index.Upsert(item.ContentId, vector);
        store.MarkDirty();

        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO embedding (content_id, model, dims, embedded_at)
            VALUES (@ContentId, @Model, @Dims, {Db.NowMs})
            ON CONFLICT (content_id, model) DO UPDATE SET dims = excluded.dims, embedded_at = excluded.embedded_at
            """,
            new { ContentId = item.ContentId, Model = embedder.ModelId, Dims = vector.Length },
            cancellationToken: ct));
    }
}
