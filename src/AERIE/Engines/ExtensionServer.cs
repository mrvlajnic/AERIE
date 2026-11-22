using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AERIE.Core.Extensions;

namespace AERIE;

/// Each extension's files, served on a loopback port of its own:
/// http://127.0.0.1:<port>/ is the extension's origin, as
/// chrome-extension://<id>/ is in Chrome. WebKit's C API has no way to
/// answer a custom scheme, and a port per extension keeps their storage
/// apart. The port is kept between launches so the origin — and whatever
/// the extension stored under it — stays the same.
sealed class ExtensionServer : IDisposable
{
    readonly Manifest manifest;
    readonly Func<string, string> runtime;   // kind → runtime.js
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}/";
    /// The only way to open the background page: it runs the extension's APIs
    /// as the browser, so it is not a document any page may navigate to.
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string BackgroundUrl => Base + "__search/background.html?key=" + Token;

    public ExtensionServer(Manifest manifest, int preferredPort, Func<string, string> runtime)
    {
        this.manifest = manifest;
        this.runtime = runtime;
        TcpListener? l = null;
        if (preferredPort > 0)
        {
            try { l = new TcpListener(IPAddress.Loopback, preferredPort); l.Start(); }
            catch { l = null; }
        }
        if (l == null) { l = new TcpListener(IPAddress.Loopback, 0); l.Start(); }
        listener = l;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Loop();
    }

    async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch when (stop.IsCancellationRequested) { return; }
            catch { await Task.Delay(250); continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHead(stream);
                if (head == null) return;
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) { await Respond(stream, 405, "text/plain", Encoding.UTF8.GetBytes("no"), false); return; }
                // Last wins rather than throwing on a duplicate header.
                var headers = new Dictionary<string, string>();
                foreach (var line in lines.Skip(1))
                {
                    var p = line.Split(':', 2);
                    if (p.Length == 2) headers[p[0].Trim().ToLowerInvariant()] = p[1].Trim();
                }
                var target = parts[1];
                var query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { query = target[(q + 1)..]; target = target[..q]; }
                var path = Uri.UnescapeDataString(target).TrimStart('/');
                bool head_ = parts[0] == "HEAD";

                if (path == "__search/runtime.js")
                {
                    // Only an extension page (or the browser) may load the runtime.
                    if (!FromSelf(headers)) { await Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("not the extension"), head_); return; }
                    var kind = System.Web.HttpUtility.ParseQueryString(query)["kind"] ?? "page";
                    await Respond(stream, 200, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(runtime(kind)), head_);
                    return;
                }
                if (path == "__search/background.html")
                {
                    // It runs the extension's chrome.* APIs; only the browser's
                    // own URL, carrying this run's token, opens it.
                    if (!string.Equals(QueryValue(query, "key"), Token, StringComparison.Ordinal))
                    { await Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("not the extension"), head_); return; }
                    string html = manifest.BackgroundPage != null && File.Exists(Path.Combine(manifest.Folder, manifest.BackgroundPage))
                        ? Bootstrap.InjectRuntime(File.ReadAllText(Path.Combine(manifest.Folder, manifest.BackgroundPage)), "background")
                        : Bootstrap.BackgroundHtml(manifest);
                    await Respond(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), head_);
                    return;
                }

                var file = Path.GetFullPath(Path.Combine(manifest.Folder, path.Length == 0 ? "index.html" : path));
                var root = Path.GetFullPath(manifest.Folder) + Path.DirectorySeparatorChar;
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { await Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), head_); return; }

                // A web page may load only what the extension lists as web accessible.
                if (!FromSelf(headers) && !Accessible(path, headers)) { await Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("not web accessible"), head_); return; }

                var type = Type(file);
                byte[] body = await File.ReadAllBytesAsync(file);
                if (type.StartsWith("text/html"))
                {
                    var kind = path == manifest.Popup ? "popup" : "page";
                    body = Encoding.UTF8.GetBytes(Bootstrap.InjectRuntime(Encoding.UTF8.GetString(body), kind));
                }
                await Respond(stream, 200, type, body, head_);
            }
            catch { }
        }
    }

    /// True only for this extension's own origin: 127.0.0.1 (or localhost) on
    /// this exact port. No prefix tricks, no port 80801 matching 8080.
    bool FromSelf(Dictionary<string, string> headers)
    {
        var from = headers.GetValueOrDefault("origin") ?? headers.GetValueOrDefault("referer");
        if (!string.IsNullOrEmpty(from))
        {
            if (!Uri.TryCreate(from, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp
                && (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                && uri.Port == Port;
        }

        // No Origin/Referer: a typed address, or the browser opening the page.
        // Chromium says so with Fetch Metadata; WebKit does not send it, so an
        // HTML navigation is the fallback. A bare subresource is not the extension.
        var mode = headers.GetValueOrDefault("sec-fetch-mode");
        var site = headers.GetValueOrDefault("sec-fetch-site");
        if (mode != null || site != null)
            return mode == "navigate" && (site is "none" or "same-origin");
        return headers.TryGetValue("accept", out var accept)
            && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// A listed web-accessible resource, for an origin its `matches` allow.
    bool Accessible(string path, Dictionary<string, string> headers)
    {
        var from = headers.GetValueOrDefault("origin") ?? headers.GetValueOrDefault("referer");
        Uri? origin = Uri.TryCreate(from, UriKind.Absolute, out var uri) ? uri : null;
        foreach (var (resources, matches) in manifest.WebAccessible)
        {
            if (!resources.Any(r => System.Text.RegularExpressions.Regex.IsMatch(
                    path, MatchPattern.GlobToRegex(r.TrimStart('/')), System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                continue;
            // A legacy list with no matches means every origin.
            if (matches.Length == 0) return true;
            if (origin is null) continue;
            if (matches.Any(m => MatchPattern.Matches(m, origin.AbsoluteUri))) return true;
        }
        return false;
    }

    static string? QueryValue(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!name.Equals(key, StringComparison.Ordinal)) continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    static async Task<string?> ReadHead(NetworkStream stream)
    {
        var buffer = new byte[16384];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total));
            if (n == 0) return null;
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    static async Task Respond(NetworkStream stream, int status, string type, byte[] body, bool headOnly)
    {
        var reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Method Not Allowed" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (!headOnly) await stream.WriteAsync(body);
    }

    static string Type(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); } catch { }
    }
}
