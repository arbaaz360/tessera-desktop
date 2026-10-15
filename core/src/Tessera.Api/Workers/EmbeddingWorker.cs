using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using SkiaSharp;
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

    /// <summary>Thumbnails decoded and held at once for one EmbedImages call. The real model batches these onto the GPU: 116 against 17 images/s one at a time.</summary>
    public const int BatchSize = 8;

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        var leased = await queue.LeaseAsync(WorkItems.EmbeddingKind, BatchSize * 4, Lease, ct);
        if (leased.Count == 0)
        {
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var made = 0;
        foreach (var chunk in leased.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            made += await EmbedBatchAsync(chunk, ct);
        }

        Interlocked.Add(ref _embedded, made);
        Interlocked.Add(ref _embedMs, sw.ElapsedMilliseconds);
        log.EmbeddingBatch(made, leased.Count, sw.ElapsedMilliseconds, index.Count);
        return leased.Count;
    }

    /// <summary>
    /// Decode up to <see cref="BatchSize"/> thumbnails, embed them in one
    /// call, record each. A thumbnail that cannot be read is abandoned on
    /// its own; a failure of the model call fails the batch, and every item
    /// in it is retried.
    /// </summary>
    private async Task<int> EmbedBatchAsync(Leased[] chunk, CancellationToken ct)
    {
        var items = new List<Leased>(chunk.Length);
        var bitmaps = new List<SKBitmap>(chunk.Length);
        try
        {
            foreach (var item in chunk)
            {
                var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, item.Sha256, "thumb", "webp");
                if (!File.Exists(path))
                {
                    await queue.AbandonAsync(item.WorkId, "no thumbnail to embed from", CancellationToken.None);
                    continue;
                }

                var bitmap = SkiaFiles.Decode(path);
                if (bitmap is null)
                {
                    await queue.AbandonAsync(item.WorkId, "thumbnail could not be decoded", CancellationToken.None);
                    continue;
                }

                items.Add(item);
                bitmaps.Add(bitmap);
            }

            if (items.Count == 0)
            {
                return 0;
            }

            float[][] vectors;
            try
            {
                vectors = embedder.EmbedImages(bitmaps);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var item in items)
                {
                    await queue.CompleteAsync(item.WorkId, ex.Message, CancellationToken.None);
                    log.EmbeddingFailed(ex, item.ContentId);
                }

                return 0;
            }

            for (var i = 0; i < items.Count; i++)
            {
                index.Upsert(items[i].ContentId, vectors[i]);
            }

            store.MarkDirty();

            await using (var conn = await db.OpenWriteAsync(ct))
            await using (var tx = conn.BeginTransaction(deferred: false))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $"""
                        INSERT INTO embedding (content_id, model, dims, embedded_at)
                        VALUES (@ContentId, @Model, @Dims, {Db.NowMs})
                        ON CONFLICT (content_id, model) DO UPDATE SET dims = excluded.dims, embedded_at = excluded.embedded_at
                        """,
                        new { items[i].ContentId, Model = embedder.ModelId, Dims = vectors[i].Length },
                        tx, cancellationToken: ct));
                }

                await tx.CommitAsync(ct);
            }

            foreach (var item in items)
            {
                await queue.CompleteAsync(item.WorkId, null, ct);
            }

            return items.Count;
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }
}
