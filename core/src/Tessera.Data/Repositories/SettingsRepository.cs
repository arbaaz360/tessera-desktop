using System.Globalization;
using Dapper;

namespace Tessera.Data.Repositories;

/// <summary>The licence states the shell may set (CONTRACT §4).</summary>
public static class LicenceStates
{
    public const string Free = "free";
    public const string Licensed = "licensed";

    public static bool IsKnown(string? s) => s is Free or Licensed;
}

/// <summary>What <c>GET /api/settings</c> and <c>GET /api/licence</c> read.</summary>
public sealed record AppSettings(int ThumbnailConcurrency, bool EmbedOnBattery, bool SearchEnabled, string LicenceState);

/// <summary>
/// The few things the app persists outside the library itself: the settings
/// page, the search switch, and the licence state the shell reports.
/// </summary>
/// <remarks>
/// One key/value table plus the job_control row for thumbnail concurrency,
/// which the worker already read before settings existed. One fact, one
/// place: concurrency is not copied into the settings table.
/// </remarks>
public sealed class SettingsRepository(Db db)
{
    private const string LicenceKey = "licence.state";
    private const string SearchKey = "search.enabled";
    private const string BatteryKey = "embed.onBattery";
    private const string ImportFolderKey = "import.folder";

    public async Task<AppSettings> ReadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var values = (await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT key, value FROM setting", cancellationToken: ct)))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        var concurrency = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT coalesce((SELECT concurrency FROM job_control WHERE name = @Name), 4)",
            new { Name = JobNames.Thumbnails }, cancellationToken: ct));

        return new AppSettings(
            concurrency,
            Bool(values, BatteryKey, false),
            Bool(values, SearchKey, true),
            values.TryGetValue(LicenceKey, out var state) && LicenceStates.IsKnown(state) ? state : LicenceStates.Free);
    }

    public async Task<string> LicenceStateAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var state = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM setting WHERE key = @Key", new { Key = LicenceKey }, cancellationToken: ct));
        return LicenceStates.IsKnown(state) ? state! : LicenceStates.Free;
    }

    public Task SetLicenceStateAsync(string state, CancellationToken ct) => SetAsync(LicenceKey, state, ct);

    public Task SetSearchEnabledAsync(bool enabled, CancellationToken ct) => SetAsync(SearchKey, enabled ? "true" : "false", ct);

    public Task SetEmbedOnBatteryAsync(bool on, CancellationToken ct) => SetAsync(BatteryKey, on ? "true" : "false", ct);

    /// <summary>Where the Tessera folder is, once decided; null until then.</summary>
    public async Task<string?> ImportFolderAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM setting WHERE key = @Key", new { Key = ImportFolderKey }, cancellationToken: ct));
    }

    public Task SetImportFolderAsync(string canonicalPath, CancellationToken ct) => SetAsync(ImportFolderKey, canonicalPath, ct);

    public async Task SetThumbnailConcurrencyAsync(int concurrency, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE job_control SET concurrency = @Concurrency, updated_at = {Db.NowMs} WHERE name = @Name",
            new { Name = JobNames.Thumbnails, Concurrency = Math.Clamp(concurrency, 1, 64) }, cancellationToken: ct));
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var conn = await db.OpenWriteAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO setting (key, value) VALUES (@Key, @Value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = {Db.NowMs}
            """,
            new { Key = key, Value = value }, cancellationToken: ct));
    }

    private static bool Bool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}
