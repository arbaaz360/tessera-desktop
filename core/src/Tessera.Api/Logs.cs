namespace Tessera.Api;

/// <summary>
/// Every log message in the service, as source-generated delegates.
/// </summary>
/// <remarks>
/// <para>
/// <c>logger.LogInformation("...{X}", x)</c> allocates an object array and
/// boxes every argument on each call, whether or not the level is enabled. The
/// generated methods do neither, and check the level first - which matters in
/// the scanner, where a debug line sits inside a loop that runs a million
/// times.
/// </para>
/// <para>
/// Keeping them in one file also makes the service's whole vocabulary legible
/// at a glance, and gives every message a stable id.
/// </para>
/// </remarks>
internal static partial class Logs
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId} started for {Label} at {Path}")]
    public static partial void ScanStarted(this ILogger logger, long runId, string label, string path);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId}: {Count} files already recorded")]
    public static partial void ScanKnown(this ILogger logger, long runId, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {RunId} finished: seen {Seen}, added {Added}, moved {Moved}, " +
                  "unchanged {Unchanged}, gone {Gone}, errors {Errors}, cancelled {Cancelled}")]
    public static partial void ScanFinished(
        this ILogger logger, long runId, long seen, long added, long moved,
        long unchanged, long gone, long errors, bool cancelled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan {RunId} cancelled")]
    public static partial void ScanCancelled(this ILogger logger, long runId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Scan {RunId} refused to mark anything gone: found {Seen} files where " +
                  "the database has {Known}. A root that lost most of its files in one " +
                  "scan is a broken mount far more often than a real deletion.")]
    public static partial void ScanRefusedToMarkGone(
        this ILogger logger, long runId, long known, long seen);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scan {RunId}: {File} failed")]
    public static partial void ScanFileFailed(this ILogger logger, Exception ex, long runId, string file);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of root {RootId} failed")]
    public static partial void ScanFailed(this ILogger logger, Exception ex, long rootId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied schema migration {Version}")]
    public static partial void SchemaMigrated(this ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Serving the web app from {WebRoot}")]
    public static partial void WebAppServed(this ILogger logger, string webRoot);

    [LoggerMessage(Level = LogLevel.Information, Message = "No web app at {WebRoot}; API only")]
    public static partial void WebAppAbsent(this ILogger logger, string webRoot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Thumbnail worker loop failed; retrying")]
    public static partial void ThumbnailLoopFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Thumbnail failed for content {ContentId}")]
    public static partial void ThumbnailFailed(this ILogger logger, Exception ex, long contentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Foundation answered {Status} for {Query}")]
    public static partial void SearchUpstreamStatus(this ILogger logger, int status, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Foundation unreachable")]
    public static partial void SearchUpstreamDown(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cover picker answered {Status} for album {AlbumId}")]
    public static partial void CoverPickerStatus(this ILogger logger, int status, long albumId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cover picker unreachable")]
    public static partial void CoverPickerDown(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Music worker loop failed; continuing")]
    public static partial void MusicLoopFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Music identification failed for content {ContentId}")]
    public static partial void MusicFailed(this ILogger logger, Exception ex, long contentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Every music identifier lane is resting (rate limited); holding the queue for {Hold}")]
    public static partial void MusicHeld(this ILogger logger, TimeSpan hold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan {RunId}: folder albums +{Albums}, memberships +{Members}, thumbnails queued {Thumbnails}")]
    public static partial void ScanFolderAlbums(this ILogger logger, long runId, int albums, int members, int thumbnails);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root} has {Folders} folders; {News} of them hold nothing Tessera knows and will be scanned")]
    public static partial void WatchBootstrapped(this ILogger logger, string root, int folders, int news);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root}/{Folder} changed ({Entries} entries); scanning once it settles")]
    public static partial void WatchNoticed(this ILogger logger, string root, string folder, int? entries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: {Root}/{Folder} is gone; its files are marked gone by the next scan of the root")]
    public static partial void WatchFolderGone(this ILogger logger, string root, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watch: scanning {Root}/{Folder} ({Entries} entries)")]
    public static partial void WatchScanning(this ILogger logger, string root, string folder, int entries);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watch: {Root}/{Folder} has {Entries} entries, over the {Max} a scan may start unasked; waiting for a person (POST /api/scan/roots/{{rootId}}/scan?folder=)")]
    public static partial void WatchWaitingForHuman(this ILogger logger, string root, string folder, int entries, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "Watch tick failed; continuing")]
    public static partial void WatchTickFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} answered {Status} when handed {Folder}")]
    public static partial void IndexingRefused(this ILogger logger, string service, int status, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} is not reachable; queued indexing waits for it")]
    public static partial void IndexingUnreachable(this ILogger logger, string service, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan {RunId}: queued {Folder} for faces (request {RequestId}) and registered {Registered} images with Foundation")]
    public static partial void ScanQueuedIndexing(this ILogger logger, long runId, string folder, long? requestId, int? registered);

    [LoggerMessage(Level = LogLevel.Information, Message = "Faces: started FaceSearch request {RequestId}; {Left} more queued")]
    public static partial void FaceDrainStarted(this ILogger logger, long requestId, int left);

    [LoggerMessage(Level = LogLevel.Information, Message = "Faces: nothing left queued; the drain switches itself off")]
    public static partial void FaceDrainDone(this ILogger logger);
}
