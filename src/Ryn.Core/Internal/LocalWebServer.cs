using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ryn.Core.Internal;

/// <summary>
/// Minimal HTTP/1.1 server over a raw loopback socket. It serves the app's static content and the IPC
/// endpoints at <c>http://localhost:{port}</c> — a real, whitelistable, secure-context origin — without
/// pulling in ASP.NET Core/Kestrel (~7MB in an AOT binary) and without <c>http.sys</c> URL-ACL
/// reservations (which <see cref="System.Net.HttpListener"/> would require for non-admin users).
///
/// IPC contract (unchanged from the previous Kestrel implementation):
///   POST /ipc/cmd/{id}/{command}  — JSON body, X-Ryn-Token header; inline result, 200 ok / 500 error
///   POST /ipc/eval/{id}/{ok}      — eval response channel
/// </summary>
internal sealed class LocalWebServer : IAsyncDisposable
{
    private const int DefaultPort = 7421;
    private const int MaxHeadBytes = 32 * 1024;            // request line + headers
    private const long MaxBodyBytes = 32L * 1024 * 1024;   // matches the previous body cap

    private readonly string? _contentDirectory;
    private readonly string? _allowedCorsOrigin;
    private readonly int _preferredPort;
    private ILocalServerHost? _webView;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    // Per-connection handler tasks, tracked so DisposeAsync can await them instead of fire-and-forgetting —
    // otherwise a connection could still be touching the stream/CTS after the server reports itself disposed.
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    public string Url { get; private set; } = "";

    /// <param name="contentDirectory">Static content root, or null for an IPC-only server (e.g. backing a Vite dev server).</param>
    /// <param name="preferredPort">Fixed loopback port to try first.</param>
    /// <param name="allowedCorsOrigin">When set (e.g. a dev-server origin), cross-origin IPC from that origin is permitted via CORS.</param>
    internal LocalWebServer(string? contentDirectory, int preferredPort, string? allowedCorsOrigin = null)
    {
        _contentDirectory = contentDirectory is null ? null : Path.GetFullPath(contentDirectory);
        _preferredPort = preferredPort > 0 ? preferredPort : DefaultPort;
        _allowedCorsOrigin = allowedCorsOrigin?.TrimEnd('/');
    }

    internal void SetWebView(ILocalServerHost webView) => _webView = webView;

    internal Task StartAsync()
    {
        var port = BindLoopback(_preferredPort);
        Url = $"http://localhost:{port}";
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private int BindLoopback(int preferred)
    {
        foreach (var port in CandidatePorts(preferred))
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (SocketException) { /* port taken — try the next */ }
        }

        // Last resort: let the OS pick a free loopback port.
        var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        _listener = fallback;
        return ((IPEndPoint)fallback.LocalEndpoint).Port;
    }

    private static IEnumerable<int> CandidatePorts(int preferred)
    {
        yield return preferred;
        for (var i = 1; i <= 16; i++)
            yield return preferred + i;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var connection = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
            _connections[connection] = 0;
            // Self-remove on completion so the set doesn't grow unbounded over a long-lived server.
            _ = connection.ContinueWith(
                static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
                _connections, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                var keepAlive = true;
                while (keepAlive && !ct.IsCancellationRequested)
                {
                    var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                    if (request is null)
                        break;

                    keepAlive = request.KeepAlive;
                    await RouteAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
                }
            }
            catch (IOException) { /* client went away */ }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }
    }

    // ---- request parsing ----

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(512);
        var one = new byte[1];

        while (true)
        {
            int n;
            try { n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false); }
            catch (IOException) { return null; }

            if (n == 0)
                return null; // EOF

            head.Add(one[0]);
            if (head.Count > MaxHeadBytes)
                return null;

            var c = head.Count;
            if (c >= 4 && head[c - 4] == (byte)'\r' && head[c - 3] == (byte)'\n'
                       && head[c - 2] == (byte)'\r' && head[c - 1] == (byte)'\n')
                break;
        }

        var headText = Encoding.ASCII.GetString(head.ToArray());
        var lines = headText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
            return null;

        var method = requestLine[0];
        var target = requestLine[1];
        var version = requestLine[2];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out var clStr)
            && long.TryParse(clStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength)
            && contentLength > 0)
        {
            if (contentLength > MaxBodyBytes)
                return null;

            body = new byte[contentLength];
            var read = 0;
            while (read < body.Length)
            {
                int n;
                try { n = await stream.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false); }
                catch (IOException) { return null; }
                if (n == 0) return null;
                read += n;
            }
        }

        var keepAlive = DetermineKeepAlive(version, headers);
        return new HttpRequest(method, target, headers, body, keepAlive);
    }

    private static bool DetermineKeepAlive(string version, Dictionary<string, string> headers)
    {
        var connection = headers.TryGetValue("Connection", out var c) ? c : "";
        if (connection.Contains("close", StringComparison.OrdinalIgnoreCase))
            return false;
        if (version.Equals("HTTP/1.0", StringComparison.Ordinal))
            return connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        return true; // HTTP/1.1 default
    }

    // ---- routing ----

    private async Task RouteAsync(NetworkStream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        var path = request.Path; // without query
        var corsHeaders = BuildCorsHeaders(request);

        // CORS preflight for IPC.
        if (request.Method == "OPTIONS" && path.StartsWith("/ipc/", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 204, "No Content", null, [], corsHeaders, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/ipc/cmd/", StringComparison.Ordinal))
        {
            await HandleIpcCommandAsync(stream, request, corsHeaders, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/ipc/eval/", StringComparison.Ordinal))
        {
            await HandleIpcEvalAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (request.Method is "GET" or "HEAD")
        {
            await ServeStaticAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(stream, 404, "Not Found", "not found", keepAlive, ct).ConfigureAwait(false);
    }

    private async Task HandleIpcCommandAsync(NetworkStream stream, HttpRequest request,
        List<(string, string)> corsHeaders, bool keepAlive, CancellationToken ct)
    {
        if (_webView is null)
        {
            await WriteTextAsync(stream, 503, "Service Unavailable", "webview not ready", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request))
        {
            await WriteTextAsync(stream, 403, "Forbidden", "forbidden", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        // /ipc/cmd/{id}/{command}
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4)
        {
            await WriteTextAsync(stream, 400, "Bad Request", "bad command path", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        var command = Uri.UnescapeDataString(segments[3]);
        var bodyText = Encoding.UTF8.GetString(request.Body);

        var (ok, data) = await _webView.DispatchCommandFromServerAsync(command, bodyText).ConfigureAwait(false);

        var headers = new List<(string, string)>(corsHeaders) { ("X-Content-Type-Options", "nosniff") };
        await WriteAsync(stream, ok ? 200 : 500, ok ? "OK" : "Internal Server Error",
            ok ? "application/json" : "text/plain", Encoding.UTF8.GetBytes(data), headers, keepAlive, ct).ConfigureAwait(false);
    }

    private async Task HandleIpcEvalAsync(NetworkStream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (_webView is null)
        {
            await WriteTextAsync(stream, 503, "Service Unavailable", "webview not ready", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request))
        {
            await WriteTextAsync(stream, 403, "Forbidden", "forbidden", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        // /ipc/eval/{id}/{ok}
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4
            || !long.TryParse(segments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var evalId)
            || !int.TryParse(segments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var okFlag))
        {
            await WriteTextAsync(stream, 400, "Bad Request", "bad eval path", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        _webView.HandleEvalFromServer(evalId, okFlag, Encoding.UTF8.GetString(request.Body));
        await WriteTextAsync(stream, 200, "OK", "", keepAlive, ct).ConfigureAwait(false);
    }

    // ---- authorization (loopback + per-launch token + same-origin) ----

    private bool IsAuthorized(HttpRequest request)
    {
        var token = _webView?.IpcToken;
        if (string.IsNullOrEmpty(token)) return false;

        var presented = request.Headers.TryGetValue("X-Ryn-Token", out var t) ? t : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token)))
            return false;

        var host = HostOnly(request.Headers.TryGetValue("Host", out var h) ? h : "");
        if (!IsLoopbackHost(host)) return false;

        if (request.Headers.TryGetValue("Origin", out var origin) && !string.IsNullOrEmpty(origin))
        {
            var allowed = _allowedCorsOrigin is not null
                && string.Equals(origin.TrimEnd('/'), _allowedCorsOrigin, StringComparison.OrdinalIgnoreCase);
            if (!allowed && !(Uri.TryCreate(origin, UriKind.Absolute, out var ou) && IsLoopbackHost(ou.Host)))
                return false;
        }

        return true;
    }

    private List<(string, string)> BuildCorsHeaders(HttpRequest request)
    {
        var headers = new List<(string, string)>();
        if (_allowedCorsOrigin is null) return headers;

        var origin = request.Headers.TryGetValue("Origin", out var o) ? o : "";
        if (string.Equals(origin.TrimEnd('/'), _allowedCorsOrigin, StringComparison.OrdinalIgnoreCase))
        {
            headers.Add(("Access-Control-Allow-Origin", origin));
            headers.Add(("Vary", "Origin"));
            headers.Add(("Access-Control-Allow-Methods", "POST, OPTIONS"));
            headers.Add(("Access-Control-Allow-Headers", "Content-Type, X-Ryn-Token"));
        }
        return headers;
    }

    private static string HostOnly(string host)
    {
        var colon = host.LastIndexOf(':');
        // keep IPv6 literals like [::1]:port intact-ish; simple split is fine for loopback names/IPv4
        return colon > 0 && !host.Contains(']', StringComparison.Ordinal) ? host[..colon] : host.Trim('[', ']');
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.Ordinal)
        || host.Equals("::1", StringComparison.Ordinal)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    // ---- static files (SPA) ----

    private async Task ServeStaticAsync(NetworkStream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (_contentDirectory is null)
        {
            await WriteTextAsync(stream, 404, "Not Found", "not found", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        var rawPath = request.Path;
        var relative = Uri.UnescapeDataString(rawPath.TrimStart('/'));
        if (string.IsNullOrEmpty(relative))
            relative = "index.html";

        var filePath = ResolveWithinContent(relative);

        // SPA fallback: an unmatched route serves index.html (mirrors the previous MapFallback behavior).
        if (filePath is null || !File.Exists(filePath))
            filePath = ResolveWithinContent("index.html");

        if (filePath is null || !File.Exists(filePath))
        {
            await WriteTextAsync(stream, 404, "Not Found", "not found", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        byte[] content;
        try { content = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false); }
        catch (IOException)
        {
            await WriteTextAsync(stream, 500, "Internal Server Error", "read error", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        var headers = new List<(string, string)>
        {
            ("Cache-Control", "no-cache, no-store, must-revalidate"),
            ("X-Content-Type-Options", "nosniff"),
        };
        var body = request.Method == "HEAD" ? [] : content;
        await WriteAsync(stream, 200, "OK", GetMimeType(Path.GetExtension(filePath)), body, headers, keepAlive, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a request-relative path under the content root, rejecting directory traversal.</summary>
    private string? ResolveWithinContent(string relative)
    {
        if (_contentDirectory is null) return null;

        var combined = Path.GetFullPath(Path.Combine(_contentDirectory, relative));
        var root = _contentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (combined.Equals(root, comparison)
            || combined.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            return combined;

        return null; // traversal attempt
    }

    private static string GetMimeType(string extension) => extension.ToUpperInvariant() switch
    {
        ".HTML" or ".HTM" => "text/html; charset=utf-8",
        ".CSS" => "text/css; charset=utf-8",
        ".JS" or ".MJS" => "application/javascript; charset=utf-8",
        ".JSON" or ".MAP" => "application/json; charset=utf-8",
        ".SVG" => "image/svg+xml",
        ".PNG" => "image/png",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".GIF" => "image/gif",
        ".WEBP" => "image/webp",
        ".ICO" => "image/x-icon",
        ".WOFF" => "font/woff",
        ".WOFF2" => "font/woff2",
        ".TTF" => "font/ttf",
        ".OTF" => "font/otf",
        ".WASM" => "application/wasm",
        ".TXT" => "text/plain; charset=utf-8",
        ".XML" => "application/xml; charset=utf-8",
        ".MP3" => "audio/mpeg",
        ".MP4" => "video/mp4",
        ".WEBM" => "video/webm",
        ".PDF" => "application/pdf",
        _ => "application/octet-stream",
    };

    // ---- response writing ----

    private static Task WriteTextAsync(NetworkStream stream, int status, string reason, string text, bool keepAlive, CancellationToken ct) =>
        WriteAsync(stream, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), [], keepAlive, ct);

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, string? contentType,
        byte[] body, IReadOnlyList<(string Name, string Value)> headers, bool keepAlive, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        if (contentType is not null)
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        foreach (var (name, value) in headers)
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();
        _listener?.Dispose();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Drain outstanding per-connection handlers so none is still using the stream/CTS after disposal.
        // Cancellation above unblocks their read loops; their own try/catch swallows IO/socket faults.
        var pending = _connections.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    private sealed record HttpRequest(string Method, string Target, Dictionary<string, string> Headers, byte[] Body, bool KeepAlive)
    {
        /// <summary>Path component of the request target, without the query string.</summary>
        public string Path
        {
            get
            {
                var q = Target.IndexOf('?', StringComparison.Ordinal);
                return q >= 0 ? Target[..q] : Target;
            }
        }
    }
}
