using System.Text.Json;
using Dapper;
using Tessera.Api.Boot;
using Tessera.Data;
using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>
/// The vector index on disk: <c>vectors/&lt;modelId&gt;.bin</c> (CONTRACT §6).
/// </summary>
/// <remarks>
/// Loaded once at start and then reconciled against the <c>embedding</c>
/// ledger: a content the ledger says is embedded but the file does not hold
/// is forgotten, so the worker makes it again; the difference is logged as
/// a number. Saved to a temporary name and moved into place, so a crash
/// mid-write cannot leave a file that is half an index.
/// </remarks>
public sealed class VectorStore(IVectorIndex index, IImageEmbedder embedder, CoreEnvironment env, Db db, ILogger<VectorStore> log) : IDisposable
{
    private readonly SemaphoreSlim _io = new(1, 1);
    private int _dirty;

    public string Path => System.IO.Path.Combine(env.VectorsDir, $"{embedder.ModelId}.bin");

    public bool Dirty => Volatile.Read(ref _dirty) == 1;

    public DateTimeOffset? LastSaved { get; private set; }

    public void MarkDirty() => Volatile.Write(ref _dirty, 1);

    public async Task LoadAsync(CancellationToken ct)
    {
        if (File.Exists(Path))
        {
            try
            {
                await using var stream = File.OpenRead(Path);
                await index.LoadAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // A bad file is re-made from the thumbnails; it is never trusted.
                log.VectorIndexUnreadable(ex, Path);
            }
        }

        await ReconcileAsync(ct);
        log.VectorIndexLoaded(Path, index.Count, embedder.ModelId);
    }

    /// <summary>Make the ledger agree with the file. Returns how many ledger rows were dropped.</summary>
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        var ledger = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT content_id FROM embedding WHERE model = @Model",
            new { Model = embedder.ModelId }, cancellationToken: ct))).ToList();

        var missing = ledger.Where(id => !index.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM embedding WHERE model = @Model AND content_id IN (SELECT value FROM json_each(@Ids))",
                new { Model = embedder.ModelId, Ids = JsonSerializer.Serialize(missing) }, cancellationToken: ct));
        }

        // The other direction - vectors the ledger does not know - cannot be
        // enumerated through IVectorIndex; the search filters by live content,
        // so a stray vector is never shown. The count still says it exists.
        var strays = Math.Max(0, index.Count - (ledger.Count - missing.Count));
        log.VectorIndexReconciled(ledger.Count, missing.Count, strays);
        return missing.Count;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        if (!Dirty)
        {
            return;
        }

        await _io.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(env.VectorsDir);
            var tmp = Path + ".tmp";
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await index.SaveAsync(stream, ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tmp, Path, overwrite: true);
            Volatile.Write(ref _dirty, 0);
            LastSaved = DateTimeOffset.UtcNow;
            log.VectorIndexSaved(Path, index.Count);
        }
        finally
        {
            _io.Release();
        }
    }

    public void Dispose() => _io.Dispose();
}
