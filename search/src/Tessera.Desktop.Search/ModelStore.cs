using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tessera.Desktop.Search;

/// <summary>
/// Makes <c>&lt;ModelsDir&gt;/&lt;modelId&gt;/</c> hold every file the manifest
/// lists for this platform, with the right sha256. Missing or wrong files are downloaded to a
/// <c>.part</c> file, resumed with a Range request when one is already
/// there, verified, then renamed into place. Nothing is trusted until its
/// hash matches.
/// </summary>
/// <remarks>
/// The only network traffic this library makes is to the URLs in the
/// manifest. A developer testing against a private GitHub release can set
/// <c>TESSERA_MODELS_TOKEN</c>; it is sent as a bearer token to github.com
/// hosts only and never anywhere else.
/// </remarks>
public sealed class ModelStore : IDisposable
{
    public const string TokenEnvVar = "TESSERA_MODELS_TOKEN";
    private const int BufferBytes = 1 << 20;

    private readonly SearchOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _ownsHttp;

    public ModelStore(SearchOptions options, HttpClient? http = null, ILogger<ModelStore>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = http is null;
        _http = http ?? DefaultClient();
        _log = log ?? NullLogger<ModelStore>.Instance;
    }

    private static HttpClient DefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TesseraDesktop", "1.0"));
        return client;
    }

    /// <summary>Directory for <paramref name="modelId"/> under <see cref="SearchOptions.ModelsDir"/>.</summary>
    public string DirectoryFor(string modelId) => Path.Combine(_options.ModelsDir, modelId);

    /// <summary>
    /// Verifies every manifest file, downloading what is missing or wrong.
    /// Concurrent callers queue on one lock, so only one download runs.
    /// Returns the model directory.
    /// </summary>
    public async Task<string> EnsureAsync(ModelManifest manifest, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest = manifest.ForPlatform();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string dir = DirectoryFor(manifest.ModelId);
            Directory.CreateDirectory(dir);
            foreach (ModelFile file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.Combine(dir, file.Name);
                if (await IsValidAsync(path, file, ct).ConfigureAwait(false))
                {
                    _log.LogDebug("{File} present and verified", file.Name);
                    continue;
                }
                await DownloadAsync(file, path, progress, ct).ConfigureAwait(false);
            }
            string manifestPath = Path.Combine(dir, "manifest.json");
            string json = manifest.ToJson();
            if (!File.Exists(manifestPath) || await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false) != json)
                await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);
            return dir;
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when every file is present with the right size and hash. Does not download.</summary>
    public async Task<bool> IsCompleteAsync(ModelManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest = manifest.ForPlatform();
        string dir = DirectoryFor(manifest.ModelId);
        foreach (ModelFile file in manifest.Files)
        {
            if (!await IsValidAsync(Path.Combine(dir, file.Name), file, ct).ConfigureAwait(false)) return false;
        }
        return true;
    }

    private static async Task<bool> IsValidAsync(string path, ModelFile file, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Bytes) return false;
        string hash = await Sha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAsync(ModelFile file, string path, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        string part = path + ".part";
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(file, part, progress, ct).ConfigureAwait(false);
                string hash = await Sha256Async(part, ct).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{file.Name}: sha256 {hash} does not match the manifest ({file.Sha256}).");
                File.Move(part, path, overwrite: true);
                _log.LogInformation("{File}: downloaded and verified ({Bytes} bytes)", file.Name, file.Bytes);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException && attempt < attempts)
            {
                _log.LogWarning(e, "{File}: attempt {Attempt} failed, retrying", file.Name, attempt);
                if (e is InvalidDataException) TryDelete(part);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                TryDelete(part);
                throw;
            }
        }
    }

    private async Task FetchAsync(ModelFile file, string part, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Bytes)
        {
            TryDelete(part);
            have = 0;
        }
        if (have == file.Bytes)
        {
            progress?.Report(new ModelDownloadProgress(file.Name, have, file.Bytes));
            return;
        }

        Uri url = await ResolveAsync(file, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(request);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        FileMode mode;
        if (response.StatusCode == HttpStatusCode.PartialContent && have > 0)
        {
            mode = FileMode.Append;
            _log.LogInformation("{File}: resuming at {Have} bytes", file.Name, have);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            mode = FileMode.Create;
            have = 0;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Nothing sensible to resume; start over.
            TryDelete(part);
            throw new HttpRequestException($"{file.Name}: server refused the range request.");
        }
        else
        {
            throw new HttpRequestException($"{file.Name}: {(int)response.StatusCode} {response.ReasonPhrase} from {file.Url}");
        }

        long? total = response.Content.Headers.ContentLength is long len ? have + len : file.Bytes;
        progress?.Report(new ModelDownloadProgress(file.Name, have, total));

        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(part, mode, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true);
        byte[] buffer = new byte[BufferBytes];
        long received = have;
        long lastReport = received;
        while (true)
        {
            int n = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            received += n;
            if (received - lastReport >= 4 * BufferBytes)
            {
                lastReport = received;
                progress?.Report(new ModelDownloadProgress(file.Name, received, total));
            }
        }
        await target.FlushAsync(ct).ConfigureAwait(false);
        progress?.Report(new ModelDownloadProgress(file.Name, received, total));
        if (received != file.Bytes)
            throw new InvalidDataException($"{file.Name}: received {received} bytes, manifest says {file.Bytes}.");
    }

    /// <summary>
    /// The URL to fetch. With no token this is the manifest URL as it is.
    /// With <see cref="TokenEnvVar"/> set and a GitHub release download
    /// URL, the asset is looked up through the API, because a private
    /// repository answers 404 on the plain download URL even to its owner.
    /// </summary>
    private async Task<Uri> ResolveAsync(ModelFile file, CancellationToken ct)
    {
        var url = new Uri(file.Url);
        string? token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token) || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return url;
        // /{owner}/{repo}/releases/download/{tag}/{name}
        string[] parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 6 || parts[2] != "releases" || parts[3] != "download") return url;
        string owner = parts[0], repo = parts[1], tag = parts[4], name = Uri.UnescapeDataString(parts[5]);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("{File}: release lookup answered {Status}; using the plain URL", file.Name, (int)response.StatusCode);
            return url;
        }
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var n) && n.GetString() == name
                    && asset.TryGetProperty("url", out var u) && u.GetString() is string apiUrl)
                    return new Uri(apiUrl);
            }
        }
        _log.LogWarning("{File}: not among the release assets; using the plain URL", file.Name);
        return url;
    }

    private static void AddAuth(HttpRequestMessage request)
    {
        string? token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token)) return;
        string host = request.RequestUri?.Host ?? "";
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
