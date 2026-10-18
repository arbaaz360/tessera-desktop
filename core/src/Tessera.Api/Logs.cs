namespace Tessera.Api;

/// <summary>
/// Every log message in the service, as source-generated delegates.
/// </summary>
/// <remarks>
/// <c>logger.LogInformation("...{X}", x)</c> allocates an object array and
/// boxes every argument on each call, whether or not the level is enabled. The
/// generated methods do neither, and check the level first - which matters in
/// the scanner, where a debug line sits inside a loop that runs a million
/// times. Keeping them in one file also makes the service's whole vocabulary
/// legible at a glance.
/// </remarks>
internal static partial class Logs
{
    // ---------------------------------------------------------------- boot

    [LoggerMessage(Level = LogLevel.Information,
        Message = "tessera-core {Version} starting: data {DataDir}, origin {Origin}, dev {Dev}")]
    public static partial void BootStarting(this ILogger logger, string version, string dataDir, string origin, bool dev);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ready on 127.0.0.1:{Port} (pid {Pid})")]
    public static partial void BootReady(this ILogger logger, int port, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "stdin closed; shutting down")]
    public static partial void BootStdinClosed(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stdin cannot be read ({Reason}); EOF shutdown is off, the shell's kill-on-exit is the backstop")]
    public static partial void BootStdinUnusable(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied schema migration {Version}")]
    public static partial void SchemaMigrated(this ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} scan runs were left unfinished by the previous process; marked cancelled")]
    public static partial void InterruptedRuns(this ILogger logger, int count);

    // ---------------------------------------------------------------- scans

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId} started for {Label} at {Path}")]
    public static partial void ScanStarted(this ILogger logger, long runId, string label, string path);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId}: {Count} files already recorded")]
    public static partial void ScanKnown(this ILogger logger, long runId, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId} finished: seen {Seen}, added {Added}, moved {Moved}, copies {Copies}, " +
                  "unchanged {Unchanged}, gone {Gone}, errors {Errors}, cancelled {Cancelled}, " +
                  "{ElapsedMs} ms, {PerSecond:0.0} files/s")]
    public static partial void ScanFinished(
        this ILogger logger, long runId, long seen, long added, long moved, long copies,
        long unchanged, long gone, long errors, bool cancelled, long elapsedMs, double perSecond);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan {RunId} cancelled")]
    public static partial void ScanCancelled(this ILogger logger, long runId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Scan {RunId} refused to mark anything gone: found {Seen} files where " +
                  "the database has {Known}. A root that lost most of its files in one " +
                  "scan is a missing drive far more often than a real deletion.")]
    public static partial void ScanRefusedToMarkGone(
        this ILogger logger, long runId, long known, long seen);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scan {RunId}: {File} failed")]
    public static partial void ScanFileFailed(this ILogger logger, Exception ex, long runId, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of root {RootId} failed")]
    public static partial void ScanFailed(this ILogger logger, Exception ex, long rootId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan {RunId}: folder albums +{Albums}, memberships +{Members}, thumbnails queued {Thumbnails}")]
    public static partial void ScanFolderAlbums(this ILogger logger, long runId, int albums, int members, int thumbnails);

    [LoggerMessage(Level = LogLevel.Information, Message = "Root {RootId} removed; {Albums} folder albums it left with no live picture went with it")]
    public static partial void RootRemoved(this ILogger logger, long rootId, int albums);

    // ---------------------------------------------------------------- imports

    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} queued: {Items} items, album {AlbumId}")]
    public static partial void ImportQueued(this ILogger logger, long importId, int items, long? albumId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Import {ImportId} finished: added {Added}, already {Already}, copied {Copied}, skipped {Skipped}, folders {Folders}, cancelled {Cancelled}, {ElapsedMs} ms")]
    public static partial void ImportFinished(this ILogger logger, long importId, int added, int already, int copied,
        int skipped, int folders, bool cancelled, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import of {Source} failed")]
    public static partial void ImportItemFailed(this ILogger logger, Exception ex, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} imports were left unfinished by the previous process; marked cancelled")]
    public static partial void InterruptedImports(this ILogger logger, int count);

    // ---------------------------------------------------------------- watch

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root} has {Folders} folders; {News} of them hold nothing Tessera knows and will be scanned")]
    public static partial void WatchBootstrapped(this ILogger logger, string root, int folders, int news);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root}/{Folder} changed ({Entries} entries); scanning once it settles")]
    public static partial void WatchNoticed(this ILogger logger, string root, string folder, int? entries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root}/{Folder} is gone; its files are marked gone by the next scan of the root")]
    public static partial void WatchFolderGone(this ILogger logger, string root, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: queued scan {RunId} of {Root}/{Folder} ({Entries} entries)")]
    public static partial void WatchScanning(this ILogger logger, string root, string folder, int entries, long runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Watch tick failed; continuing")]
    public static partial void WatchTickFailed(this ILogger logger, Exception ex);

    // ----------------------------------------------------------- headers

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Header backfill: read {Read}, better date for {Retimed}, unreadable {Unreadable}, {ElapsedMs} ms")]
    public static partial void BackfillPass(this ILogger logger, long read, long retimed, long unreadable, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Header backfill pass failed; retrying")]
    public static partial void BackfillFailed(this ILogger logger, Exception ex);

    // ----------------------------------------------------------- thumbnails

    [LoggerMessage(Level = LogLevel.Error, Message = "Thumbnail worker loop failed; retrying")]
    public static partial void ThumbnailLoopFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Thumbnail failed for content {ContentId}")]
    public static partial void ThumbnailFailed(this ILogger logger, Exception ex, long contentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Thumbnail for content {ContentId} not made: {Reason}")]
    public static partial void ThumbnailUnsupported(this ILogger logger, long contentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Thumbnails: {Count} in {ElapsedMs} ms")]
    public static partial void ThumbnailBatch(this ILogger logger, int count, long elapsedMs);

    // ----------------------------------------------------------- embeddings

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding worker loop failed; retrying")]
    public static partial void EmbeddingLoopFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedding failed for content {ContentId}")]
    public static partial void EmbeddingFailed(this ILogger logger, Exception ex, long contentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embeddings: queued {Count} for {Model}")]
    public static partial void EmbeddingQueued(this ILogger logger, int count, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embeddings: {Made} of {Leased} in {ElapsedMs} ms; index holds {IndexCount}")]
    public static partial void EmbeddingBatch(this ILogger logger, int made, int leased, long elapsedMs, int indexCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model} ready on {Device}")]
    public static partial void ModelReady(this ILogger logger, string model, string device);

    [LoggerMessage(Level = LogLevel.Error, Message = "Model {Model} could not be made ready")]
    public static partial void ModelFailed(this ILogger logger, Exception ex, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vector index {Path} could not be read; it will be rebuilt from the thumbnails")]
    public static partial void VectorIndexUnreadable(this ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vector index {Path}: {Count} vectors for {Model}")]
    public static partial void VectorIndexLoaded(this ILogger logger, string path, int count, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vector index reconciled: ledger {Ledger}, missing from file {Missing} (re-queued), strays in file {Strays}")]
    public static partial void VectorIndexReconciled(this ILogger logger, int ledger, int missing, int strays);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vector index saved to {Path}: {Count} vectors")]
    public static partial void VectorIndexSaved(this ILogger logger, string path, int count);
}
