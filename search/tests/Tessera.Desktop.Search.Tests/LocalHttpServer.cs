using System.Net;

namespace Tessera.Desktop.Search.Tests;

/// <summary>
/// Serves the files in one directory over loopback with Range support, and
/// counts requests, so ModelStore can be tested without the network.
/// Set <see cref="FailAfterBytes"/> to cut a response short (simulates a
/// dropped connection) or <see cref="Corrupt"/> to serve wrong bytes.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string BaseUrl { get; }
    public List<(string Path, string? Range)> Requests { get; } = new();
    public long? FailAfterBytes { get; set; }
    public bool Corrupt { get; set; }
    public bool RefuseRange { get; set; }

    public LocalHttpServer(string root)
    {
        _root = root;
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        string rel = ctx.Request.Url!.AbsolutePath.TrimStart('/');
        string? range = ctx.Request.Headers["Range"];
        lock (Requests) Requests.Add((rel, range));
        string path = Path.Combine(_root, rel);
        var res = ctx.Response;
        try
        {
            if (!File.Exists(path))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }
            byte[] data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (Corrupt) for (int i = 0; i < data.Length; i += 97) data[i] ^= 0x5A;
            long start = 0;
            if (range is not null && !RefuseRange && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                string spec = range["bytes=".Length..];
                int dash = spec.IndexOf('-');
                start = long.Parse(dash >= 0 ? spec[..dash] : spec, System.Globalization.CultureInfo.InvariantCulture);
                if (start >= data.Length)
                {
                    res.StatusCode = 416;
                    res.Close();
                    return;
                }
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {start}-{data.Length - 1}/{data.Length}";
            }
            else
            {
                res.StatusCode = 200;
            }
            long length = data.Length - start;
            res.ContentLength64 = length;
            res.Headers["Accept-Ranges"] = "bytes";
            long limit = FailAfterBytes is long f ? Math.Min(length, f) : length;
            await res.OutputStream.WriteAsync(data.AsMemory((int)start, (int)limit)).ConfigureAwait(false);
            if (limit < length) res.Abort(); else res.Close();
        }
        catch (HttpListenerException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
