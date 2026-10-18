using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tessera.Tests;

/// <summary>
/// The handshake of CONTRACT §1 and §2, against the real executable: env in,
/// one JSON line out, token on /api, open /health, EOF on stdin means exit.
/// </summary>
/// <remarks>
/// Uses the published exe under <c>src-tauri/resources/core</c> when it is
/// there (the artefact the shell ships), otherwise the one the build put next
/// to this test assembly.
/// </remarks>
public sealed class BootTests
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(40);

    [Fact]
    public async Task The_core_boots_answers_and_stops_on_stdin_eof()
    {
        using var home = new TempHome();
        using var core = Core.Start(home, token: home.Token);

        var ready = await core.ReadyAsync();
        Assert.Equal("ready", ready.GetProperty("event").GetString());
        Assert.Equal(core.Process.Id, ready.GetProperty("pid").GetInt32());
        Assert.Equal("1.0.0", ready.GetProperty("version").GetString());
        var port = ready.GetProperty("port").GetInt32();
        Assert.InRange(port, 1, 65535);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);

        using (var anonymous = await http.GetAsync("/api/library"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var body = await anonymous.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(string.IsNullOrEmpty(body.GetProperty("detail").GetString()));
        }

        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/library"))
        {
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token-at-all-0123456789");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        }

        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/library"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);
            using var response = await http.SendAsync(bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var library = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("free", library.GetProperty("licence").GetProperty("state").GetString());
            Assert.Equal(2000, library.GetProperty("licence").GetProperty("cap").GetInt32());
        }

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/library?sessionKey={home.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/no-such-route?sessionKey={home.Token}")).StatusCode);

        // File-name search needs no model: it answers even when nothing is loaded.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/search?mode=metadata&q=nothing&limit=5&sessionKey={home.Token}")).StatusCode);

        // The webview preflights PATCH and DELETE-with-a-body.
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/albums/1"))
        {
            preflight.Headers.Add("Origin", "http://tauri.localhost");
            preflight.Headers.Add("Access-Control-Request-Method", "PATCH");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            using var response = await http.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("http://tauri.localhost", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("PATCH", response.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
            Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        }

        using (var elsewhere = new HttpRequestMessage(HttpMethod.Options, "/api/albums/1"))
        {
            elsewhere.Headers.Add("Origin", "http://evil.example");
            elsewhere.Headers.Add("Access-Control-Request-Method", "PATCH");
            using var response = await http.SendAsync(elsewhere);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        // PATCH /api/albums/{id} carries the cover choice; an unknown picture is refused, not ignored.
        using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/albums"))
        {
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);
            create.Content = JsonContent.Create(new { albumName = "Covers" });
            using var created = await http.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var album = await created.Content.ReadFromJsonAsync<JsonElement>();
            var albumId = album.GetProperty("id").GetString();

            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/albums/{albumId}");
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);
            patch.Content = JsonContent.Create(new { albumThumbnailAssetId = "999999" });
            using var patched = await http.SendAsync(patch);
            Assert.Equal(HttpStatusCode.BadRequest, patched.StatusCode);
        }

        var pidFile = Path.Combine(home.DataDir, "core.pid");
        Assert.Equal(core.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), (await File.ReadAllTextAsync(pidFile)).Trim());
        Assert.True(File.Exists(Path.Combine(home.DataDir, "library.db")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(home.DataDir, "logs"), "core-*.log"));

        // EOF on stdin: the shell has gone; so does the core, cleanly.
        var exit = await core.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, exit);
        Assert.False(File.Exists(pidFile), "core.pid should be removed on a clean exit");

        // One line on stdout, ever.
        Assert.Equal("", (await core.Process.StandardOutput.ReadToEndAsync()).Trim());
    }

    [Fact]
    public async Task A_second_core_on_the_same_data_dir_exits_3()
    {
        using var home = new TempHome();
        using var first = Core.Start(home, token: home.Token);
        await first.ReadyAsync();

        using var second = Core.Start(home, token: home.Token);
        using var cts = new CancellationTokenSource(ReadyTimeout);
        await second.Process.WaitForExitAsync(cts.Token);
        Assert.Equal(3, second.Process.ExitCode);

        Assert.Equal(0, await first.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task A_missing_variable_exits_2_and_says_which()
    {
        using var home = new TempHome();
        using var core = Core.Start(home, token: null);
        using var cts = new CancellationTokenSource(ReadyTimeout);
        await core.Process.WaitForExitAsync(cts.Token);
        Assert.Equal(2, core.Process.ExitCode);
        Assert.Contains("TESSERA_TOKEN", await core.Process.StandardError.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_answers_with_a_job_copies_from_outside_and_counts_in_the_library()
    {
        using var home = new TempHome();
        var tessera = Path.Combine(home.DataDir, "Pictures", "Tessera");
        var outside = Path.Combine(home.DataDir, "Downloads");
        TempLibrary.WritePng(Path.Combine(outside, "drop.png"), seed: 7);
        File.WriteAllText(Path.Combine(outside, "drop.txt"), "not a picture");
        var folder = Path.Combine(home.DataDir, "Holiday");
        TempLibrary.WritePng(Path.Combine(folder, "a.png"), seed: 8);
        TempLibrary.WritePng(Path.Combine(folder, "b.png"), seed: 9);

        using var core = Core.Start(home, token: home.Token, extra: new() { ["TESSERA_FOLDER"] = tessera });
        var port = (await core.ReadyAsync()).GetProperty("port").GetInt32();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);

        // Before first use the folder is named, not made.
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(Tessera.Core.PathRules.Canonical(tessera), settings.GetProperty("tesseraFolder").GetString());
        Assert.False(Directory.Exists(tessera));

        using var created = await http.PostAsJsonAsync("/api/albums", new { albumName = "Drops" });
        var albumId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/import", new { paths = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/import", new { paths = new[] { outside }, albumId = "999" })).StatusCode);

        using var accepted = await http.PostAsJsonAsync("/api/import", new
        {
            paths = new[] { Path.Combine(outside, "drop.png"), Path.Combine(outside, "drop.txt"), folder },
            albumId,
        });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var id = job.GetProperty("id").GetInt64();
        Assert.Equal(3, job.GetProperty("total").GetInt32());
        Assert.Equal("folder", job.GetProperty("items")[2].GetProperty("kind").GetString());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (job.GetProperty("state").GetString() == "running" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            job = await http.GetFromJsonAsync<JsonElement>($"/api/import/{id}");
        }

        Assert.Equal("done", job.GetProperty("state").GetString());
        var counts = job.GetProperty("counts");
        Assert.Equal((0, 0, 1, 1, 1), (counts.GetProperty("added").GetInt32(), counts.GetProperty("already").GetInt32(),
            counts.GetProperty("copied").GetInt32(), counts.GetProperty("skipped").GetInt32(), counts.GetProperty("folders").GetInt32()));
        var items = job.GetProperty("items");
        Assert.Equal("copied", items[0].GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(items[0].GetProperty("assetId").GetString()));
        Assert.Equal("not a picture or video", items[1].GetProperty("reason").GetString());
        Assert.Equal(2, items[2].GetProperty("pictures").GetInt32());
        Assert.True(File.Exists(Path.Combine(tessera, "Drops", "drop.png")));
        Assert.True(File.Exists(Path.Combine(outside, "drop.png")), "the original stays where it was");

        // The bytes of a drop, when the webview hands over data.
        using (var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(folder, "a.png"))))
        {
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var uploaded = await http.PostAsync($"/api/import/upload?name=again.png&albumId={albumId}", bytes);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            var single = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("already", single.GetProperty("items")[0].GetProperty("outcome").GetString());
        }

        var album = await http.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal(3, album.GetProperty("assetCount").GetInt32());

        var library = await http.GetFromJsonAsync<JsonElement>("/api/library");
        var last = library.GetProperty("imports").GetProperty("last");
        Assert.Equal(1, last.GetProperty("total").GetInt32());   // the upload, the newest
        Assert.Equal(0, library.GetProperty("imports").GetProperty("running").GetInt32());
        var app = library.GetProperty("tesseraFolder");
        Assert.Equal(Tessera.Core.PathRules.Canonical(tessera), app.GetProperty("path").GetString());
        var appRoot = library.GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("appOwned").GetBoolean());
        Assert.Equal(app.GetProperty("rootId").GetInt64(), appRoot.GetProperty("id").GetInt64());

        // Moving the folder: a library folder is refused, a new place is taken.
        using (var refused = await http.PutAsJsonAsync("/api/settings", new { tesseraFolder = folder }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        var moved = Path.Combine(home.DataDir, "Elsewhere", "Tessera");
        using (var ok = await http.PutAsJsonAsync("/api/settings", new { tesseraFolder = moved }))
        {
            Assert.Equal(Tessera.Core.PathRules.Canonical(moved), (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tesseraFolder").GetString());
        }

        Assert.Equal(0, await core.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(20)));
    }

    private sealed class TempHome : IDisposable
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "tessera-tests", "boot-" + Guid.NewGuid().ToString("N"));

        public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public void Dispose()
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class Core : IDisposable
    {
        public required Process Process { get; init; }
        private readonly List<string> _stderr = [];

        public static Core Start(TempHome home, string? token, Dictionary<string, string>? extra = null)
        {
            var exe = FindExe();
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment.Remove("TESSERA_TOKEN");
            if (token is not null)
            {
                psi.Environment["TESSERA_TOKEN"] = token;
            }

            psi.Environment["TESSERA_DATA_DIR"] = home.DataDir;
            psi.Environment["TESSERA_WEB_ORIGIN"] = "http://tauri.localhost";
            psi.Environment["TESSERA_PORT"] = "0";
            psi.Environment.Remove("ASPNETCORE_URLS");
            psi.Environment.Remove("ASPNETCORE_ENVIRONMENT");

            // Never the real Pictures folder: a test that imports names its own.
            psi.Environment["TESSERA_FOLDER"] = Path.Combine(home.DataDir, "Pictures", "Tessera");
            foreach (var (key, value) in extra ?? [])
            {
                psi.Environment[key] = value;
            }

            var process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start");
            var core = new Core { Process = process };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (core._stderr)
                    {
                        core._stderr.Add(e.Data);
                    }
                }
            };
            return core;
        }

        public async Task<JsonElement> ReadyAsync()
        {
            var read = Process.StandardOutput.ReadLineAsync();
            var done = await Task.WhenAny(read, Task.Delay(ReadyTimeout));
            if (done != read)
            {
                throw new TimeoutException("No ready line within the timeout. stderr: " + await Process.StandardError.ReadToEndAsync());
            }

            var line = await read ?? throw new InvalidOperationException("stdout closed before the ready line. stderr: " + await Process.StandardError.ReadToEndAsync());
            return JsonSerializer.Deserialize<JsonElement>(line);
        }

        public async Task<int> CloseStdinAndWaitAsync(TimeSpan timeout)
        {
            Process.StandardInput.Close();
            using var cts = new CancellationTokenSource(timeout);
            await Process.WaitForExitAsync(cts.Token);
            return Process.ExitCode;
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();
        }

        /// <summary>The published exe if the shell's resource dir has one, else the build's.</summary>
        private static string FindExe()
        {
            var name = OperatingSystem.IsWindows() ? "tessera-core.exe" : "tessera-core";
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docs", "CONTRACT.md")))
                {
                    var published = Path.Combine(dir.FullName, "src-tauri", "resources", "core", name);
                    if (File.Exists(published))
                    {
                        return published;
                    }

                    break;
                }
            }

            var built = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(built))
            {
                throw new FileNotFoundException("tessera-core executable not found next to the tests nor under src-tauri/resources/core", built);
            }

            return built;
        }
    }
}
