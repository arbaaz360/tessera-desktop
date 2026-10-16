using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Tessera.Smoke;

/// <summary>
/// The published core, driven the way the shell drives it (docs/CONTRACT.md
/// section 1): the environment, the ready line, the token, a library root,
/// thumbnails (and optionally embeddings and a text search), then stdin
/// closed and a clean exit with <c>core.pid</c> gone. The library folder must
/// be byte-for-byte the same at the end.
/// </summary>
internal static class CoreSmoke
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    public static async Task<int> RunAsync(string exe, string library, string data, string? modelsFrom, bool embeddings, string? expectDevice, TimeSpan timeout)
    {
        var report = new Report($"Core on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        exe = Path.GetFullPath(exe);
        library = Path.GetFullPath(library);
        data = Path.GetFullPath(data);
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
        {
            throw new InvalidOperationException($"{data} must be a new or empty folder");
        }

        var pictures = Directory.GetFiles(library, "*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList();
        var heic = pictures.Count(f => Path.GetExtension(f).Equals(".heic", StringComparison.OrdinalIgnoreCase));
        report.Number("pictures in the library", $"{pictures.Count} ({heic} HEIC)");
        var before = Snapshot(library);

        if (modelsFrom is not null)
        {
            // The model the search step downloaded, so the core loads it
            // instead of fetching it again. File.Copy clones on APFS. Not the
            // CoreML cache: the core compiles as on a buyer's first launch.
            var copied = CopyTree(Path.GetFullPath(modelsFrom), Path.Combine(data, "models"));
            report.Number("model files placed in <data>/models", copied.ToString());
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var origin = OperatingSystem.IsWindows() ? "http://tauri.localhost" : "tauri://localhost";
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["TESSERA_TOKEN"] = token;
        psi.Environment["TESSERA_DATA_DIR"] = data;
        psi.Environment["TESSERA_WEB_ORIGIN"] = origin;
        psi.Environment.Remove("TESSERA_PORT");
        psi.Environment.Remove("TESSERA_DEV");

        var stderr = new List<string>();
        var sw = Stopwatch.StartNew();
        using var core = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start");
        core.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.Add(e.Data);
                }
            }
        };
        core.BeginErrorReadLine();

        try
        {
            return await DriveAsync(report, core, sw, token, origin, library, data, pictures.Count, embeddings, expectDevice, timeout, before, stderr);
        }
        finally
        {
            if (!core.HasExited)
            {
                core.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<int> DriveAsync(
        Report report, Process core, Stopwatch sw, string token, string origin, string library, string data,
        int expected, bool embeddings, string? expectDevice, TimeSpan timeout, Dictionary<string, (long, DateTime)> before, List<string> stderr)
    {
        // ---------------------------------------------------------------- ready
        string? line;
        try
        {
            line = await core.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            line = null;
        }

        if (!report.Check(line is not null, "one ready line on stdout within 30 s", line ?? (core.HasExited ? $"exited {core.ExitCode}" : "nothing")))
        {
            DumpStderr(stderr);
            return report.Finish();
        }

        report.Number("start to ready line", sw.Elapsed.TotalMilliseconds, "ms", 0);
        var ready = JsonNode.Parse(line!)!;
        var port = (int)ready["port"]!;
        report.Check((string?)ready["event"] == "ready", "the line is the ready event", line);
        report.Check((int)ready["pid"]! == core.Id, "the ready line's pid is the child's", $"{ready["pid"]} vs {core.Id}");
        var pidFile = Path.Combine(data, "core.pid");
        report.Check(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim() == core.Id.ToString(), "core.pid holds the pid");

        // Nothing else may ever appear on stdout.
        var extraStdout = new List<string>();
        var stdoutDrain = Task.Run(async () =>
        {
            while (await core.StandardOutput.ReadLineAsync() is { } l)
            {
                extraStdout.Add(l);
            }
        });

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };

        // --------------------------------------------------------------- health
        report.Check(await StatusAsync(http, HttpMethod.Get, "/health/live") == HttpStatusCode.OK, "GET /health/live 200 without the token");
        report.Check(await StatusAsync(http, HttpMethod.Get, "/health/ready") == HttpStatusCode.OK, "GET /health/ready 200 without the token");
        report.Check(await StatusAsync(http, HttpMethod.Get, "/health/ready", token) == HttpStatusCode.OK, "GET /health/ready 200 with the token");

        // ----------------------------------------------------------------- auth
        report.Check(await StatusAsync(http, HttpMethod.Get, "/api/library") == HttpStatusCode.Unauthorized, "GET /api/library 401 without the token");
        report.Check(await StatusAsync(http, HttpMethod.Get, "/api/library", (token[0] == 'A' ? "B" : "A") + token[1..]) == HttpStatusCode.Unauthorized, "GET /api/library 401 with a wrong token");
        report.Check(await StatusAsync(http, HttpMethod.Get, $"/api/library?sessionKey={token}") == HttpStatusCode.OK, "GET /api/library?sessionKey= 200");

        // ----------------------------------------------------------------- CORS
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/library"))
        {
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "PATCH");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            using var answer = await http.SendAsync(preflight);
            var allowed = answer.Headers.TryGetValues("Access-Control-Allow-Origin", out var o) ? string.Join(",", o) : "(none)";
            report.Check(answer.StatusCode == HttpStatusCode.NoContent && allowed == origin,
                $"CORS preflight from {origin} answers 204 and allows it", $"{(int)answer.StatusCode}, allow-origin {allowed}");
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // ----------------------------------------------------------------- root
        sw.Restart();
        using (var added = await http.PostAsJsonAsync("/api/library/roots", new { path = library }))
        {
            var body = await added.Content.ReadAsStringAsync();
            if (!report.Check(added.StatusCode == HttpStatusCode.Created, "POST /api/library/roots 201", $"{(int)added.StatusCode} {body}"))
            {
                DumpStderr(stderr);
                return await StopAsync(report, core, pidFile, stderr, extraStdout, stdoutDrain, library, before);
            }
        }

        // ----------------------------------------------------------- thumbnails
        var state = await WaitAsync(http, timeout, "thumbnails", s =>
            s.Root is { Scanning: false } r && r.Contents >= expected && s.ThumbsPending == 0 && s.ThumbsDone + s.ThumbsFailed >= r.Contents);
        var seconds = sw.Elapsed.TotalSeconds;
        report.Number("files / contents seen by the scan", $"{state.Root?.Files} / {state.Root?.Contents}");
        report.Number("thumbnails done / failed / pending", $"{state.ThumbsDone} / {state.ThumbsFailed} / {state.ThumbsPending}");
        report.Number("root added to last thumbnail", seconds, "s");
        report.Number("thumbnails per second (scan included)", state.ThumbsDone / seconds, "img/s");
        report.Check(state.Root?.Contents == expected, $"the scan found all {expected} pictures", $"{state.Root?.Contents}; scan errors {state.Root?.ScanErrors}: {state.Root?.ErrorSample}");
        report.Check(state.ThumbsDone == expected && state.ThumbsFailed == 0, "every picture has a thumbnail", $"done {state.ThumbsDone}, failed {state.ThumbsFailed}");
        var webps = Directory.Exists(Path.Combine(data, "thumbs"))
            ? Directory.GetFiles(Path.Combine(data, "thumbs"), "*.webp", SearchOption.AllDirectories)
            : [];
        report.Number("thumbnail files on disk", $"{webps.Length}, {webps.Sum(f => new FileInfo(f).Length) / 1024} KB");
        report.Check(webps.Length == state.ThumbsDone, "one .webp per thumbnail under <data>/thumbs");
        if (state.ThumbsFailed > 0)
        {
            DumpStderr(stderr);
        }

        // ----------------------------------------------------------- embeddings
        if (embeddings)
        {
            sw.Restart();
            state = await WaitAsync(http, timeout, "embeddings", s =>
                s.EmbedReady && s.EmbedPending == 0 && s.EmbedDone + s.EmbedFailed >= expected);
            var embedSeconds = sw.Elapsed.TotalSeconds;
            var status = await http.GetFromJsonAsync<JsonNode>("/api/search/status");
            report.Number("search device", $"{status?["device"]}");
            if (expectDevice is not null)
            {
                report.Check((string?)status?["device"] == expectDevice, $"/api/search/status reports device {expectDevice}", $"{status?["device"]}");
            }

            report.Number("embeddings done / failed / pending", $"{state.EmbedDone} / {state.EmbedFailed} / {state.EmbedPending}");
            report.Number("thumbnails done to last embedding (model load included)", embedSeconds, "s");
            report.Check(state.EmbedDone == expected && state.EmbedFailed == 0, "every picture has an embedding",
                $"done {state.EmbedDone}, failed {state.EmbedFailed}; waiting {status?["waiting"]}; error {status?["error"]}");

            var hits = await http.GetFromJsonAsync<JsonNode>("/api/search?q=a%20solid%20red%20square&limit=3");
            var top = hits?["items"]?.AsArray().FirstOrDefault();
            if (report.Check(top is not null, "GET /api/search?q=a solid red square answers items"))
            {
                var asset = await http.GetFromJsonAsync<JsonNode>($"/api/assets/{top!["id"]}");
                var name = (string?)asset?["originalFileName"];
                report.Number("top hit for \"a solid red square\"", $"{name} (score {top["score"]})");
                report.Check(name == "red.png", "the top hit is red.png", name);
            }

            if (state.EmbedFailed > 0 || state.EmbedDone < expected)
            {
                DumpStderr(stderr);
            }
        }

        return await StopAsync(report, core, pidFile, stderr, extraStdout, stdoutDrain, library, before);
    }

    private static async Task<int> StopAsync(
        Report report, Process core, string pidFile, List<string> stderr, List<string> extraStdout, Task stdoutDrain,
        string library, Dictionary<string, (long, DateTime)> before)
    {
        // ------------------------------------------------------------------ stop
        var sw = Stopwatch.StartNew();
        core.StandardInput.Close();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await core.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        report.Check(core.HasExited, "the core exits within 20 s of stdin closing");
        if (core.HasExited)
        {
            report.Number("stdin closed to exit", sw.Elapsed.TotalMilliseconds, "ms", 0);
            report.Check(core.ExitCode == 0, "exit code 0", core.ExitCode.ToString());
        }

        await stdoutDrain.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        report.Check(!File.Exists(pidFile), "core.pid is gone");
        report.Check(extraStdout.Count == 0, "nothing on stdout after the ready line", string.Join(" | ", extraStdout.Take(3)));
        var logs = Path.Combine(Path.GetDirectoryName(pidFile)!, "logs");
        report.Check(Directory.Exists(logs) && Directory.GetFiles(logs, "core-*.log").Length > 0, "logs/core-<date>.log written");

        var after = Snapshot(library);
        var changed = after.Keys.Union(before.Keys)
            .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b).ToList();
        report.Check(changed.Count == 0, "nothing inside the library root was written, moved or deleted", string.Join(", ", changed.Take(5)));

        if (report.Failures > 0)
        {
            DumpStderr(stderr);
        }

        return report.Finish();
    }

    private sealed record Root(bool Scanning, long Files, long Contents, long ScanErrors, string? ErrorSample);

    private sealed record LibraryState(
        Root? Root, long ThumbsPending, long ThumbsDone, long ThumbsFailed,
        long EmbedPending, long EmbedDone, long EmbedFailed, bool EmbedReady);

    private static async Task<LibraryState> ReadAsync(HttpClient http)
    {
        var j = await http.GetFromJsonAsync<JsonNode>("/api/library") ?? throw new InvalidDataException("empty /api/library");
        var r = j["roots"]?.AsArray().FirstOrDefault();
        var root = r is null
            ? null
            : new Root(
                (bool?)r["scanning"] ?? false,
                (long?)r["counts"]?["files"] ?? 0,
                (long?)r["counts"]?["contents"] ?? 0,
                (long?)r["lastScan"]?["errors"] ?? 0,
                r["lastScan"]?["errorSample"]?.ToJsonString());
        var t = j["thumbnails"];
        var e = j["embeddings"];
        return new LibraryState(
            root,
            (long?)t?["pending"] ?? 0, (long?)t?["done"] ?? 0, (long?)t?["failed"] ?? 0,
            (long?)e?["pending"] ?? 0, (long?)e?["done"] ?? 0, (long?)e?["failed"] ?? 0, (bool?)e?["ready"] ?? false);
    }

    /// <summary>Polls GET /api/library twice a second until <paramref name="done"/>, printing changes.</summary>
    private static async Task<LibraryState> WaitAsync(HttpClient http, TimeSpan timeout, string what, Func<LibraryState, bool> done)
    {
        var sw = Stopwatch.StartNew();
        LibraryState state;
        string? last = null;
        while (true)
        {
            state = await ReadAsync(http);
            var now = $"scanning {state.Root?.Scanning}, contents {state.Root?.Contents}, "
                + $"thumbs {state.ThumbsDone}/{state.ThumbsFailed}/{state.ThumbsPending}, "
                + $"embeddings {state.EmbedDone}/{state.EmbedFailed}/{state.EmbedPending} ready {state.EmbedReady}";
            if (now != last)
            {
                Report.Line($"{sw.Elapsed.TotalSeconds,6:F1} s  {now}");
                last = now;
            }

            if (done(state))
            {
                return state;
            }

            if (sw.Elapsed > timeout)
            {
                Report.Line($"gave up waiting for {what} after {timeout.TotalSeconds:F0} s");
                return state;
            }

            await Task.Delay(500);
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http, HttpMethod method, string path, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Relative path → size and modification time, for every file and folder.</summary>
    private static Dictionary<string, (long, DateTime)> Snapshot(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToDictionary(
            i => Path.GetRelativePath(root, i.FullName),
            i => (i is FileInfo f ? f.Length : -1, i is FileInfo ? i.LastWriteTimeUtc : DateTime.MinValue));

    private static int CopyTree(string from, string to)
    {
        var n = 0;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(from, file).Split(Path.DirectorySeparatorChar).Contains("coreml"))
            {
                continue;
            }

            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            n++;
        }

        return n;
    }

    private static void DumpStderr(List<string> stderr)
    {
        lock (stderr)
        {
            Console.WriteLine($"     --- the core's stderr, last {Math.Min(60, stderr.Count)} of {stderr.Count} lines ---");
            foreach (var l in stderr.TakeLast(60))
            {
                Console.WriteLine($"     {l}");
            }
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
