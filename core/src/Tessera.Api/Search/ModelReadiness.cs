using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>
/// One download at a time, started by the worker or by
/// <c>POST /api/search/model/download</c>, with its progress on show.
/// </summary>
/// <remarks>
/// <see cref="IImageEmbedder.EnsureReadyAsync"/> is idempotent by contract,
/// but a second caller would still wait on a second task; this keeps one and
/// hands it to everyone, so the endpoint is idempotent too (CONTRACT §4).
/// </remarks>
public sealed class ModelReadiness(IImageEmbedder embedder, IHostApplicationLifetime lifetime, ILogger<ModelReadiness> log)
{
    private readonly object _gate = new();
    private Task? _task;

    /// <summary>The file being fetched right now, or null.</summary>
    public ModelDownloadProgress? Progress { get; private set; }

    /// <summary>Why the last attempt failed, or null.</summary>
    public string? LastError { get; private set; }

    public bool Downloading
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    /// <summary>Start, or join, the one attempt to make the model ready.</summary>
    public Task EnsureAsync()
    {
        lock (_gate)
        {
            if (embedder.IsReady)
            {
                return Task.CompletedTask;
            }

            if (_task is null || _task.IsCompleted)
            {
                _task = RunAsync();
            }

            return _task;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            var progress = new Progress<ModelDownloadProgress>(p => Progress = p);
            await embedder.EnsureReadyAsync(progress, lifetime.ApplicationStopping);
            LastError = null;
            var device = embedder.Device?.ToString() ?? "none";
            log.ModelReady(embedder.ModelId, device);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.ModelFailed(ex, embedder.ModelId);
        }
        finally
        {
            Progress = null;
        }
    }
}
