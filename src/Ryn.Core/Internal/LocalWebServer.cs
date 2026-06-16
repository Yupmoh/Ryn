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
            // One reader per connection so any bytes overread past a request's body (HTTP pipelining) carry
            // forward to the next request on the same keep-alive connection.
            var reader = new RequestReader(stream);
            try
            {
                var keepAlive = true;
                while (keepAlive && !ct.IsCancellationRequested)
                {
                    var request = await reader.ReadRequestAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Buffered, per-connection HTTP/1.1 request reader. Fills a reusable buffer from the socket in chunks
    /// (instead of one byte per syscall), scans for the <c>CRLFCRLF</c> head terminator, then reads the body.
    /// Any bytes read past the current request's body are retained in <see cref="_leftover"/> so a pipelined
    /// next request on the same keep-alive connection parses without loss. Parsing/validation semantics
    /// (head/body caps, EOF→null, IOException→null) match the previous per-byte implementation exactly.
    /// </summary>
    private sealed class RequestReader(NetworkStream stream)
    {
        private const int ReadChunk = 8 * 1024;
        private readonly NetworkStream _stream = stream;
        private readonly byte[] _readBuffer = new byte[ReadChunk];

        // Bytes already pulled off the socket but not yet consumed (head overread / pipelined request tails).
        private byte[] _leftover = [];

        public async Task<HttpRequest?> ReadRequestAsync(CancellationToken ct)
        {
            // Accumulate the head (request line + headers) until CRLFCRLF, starting from any carried-over bytes.
            var head = new ByteAccumulator(_leftover);
            _leftover = [];

            // Scanning index for the CRLFCRLF terminator; resumes near the prior tail to stay O(n) overall.
            var scanFrom = 0;
            int headEnd;
            while ((headEnd = IndexOfCrlfCrlf(head.Buffer, head.Count, ref scanFrom)) < 0)
            {
                if (head.Count > MaxHeadBytes)
                    return null;

                int n;
                try { n = await _stream.ReadAsync(_readBuffer.AsMemory(0, ReadChunk), ct).ConfigureAwait(false); }
                catch (IOException) { return null; }

                if (n == 0)
                    return null; // EOF before a complete head

                head.Append(_readBuffer, n);
            }

            var bodyStart = headEnd + 4; // past the CRLFCRLF

            // Enforce the head cap on the terminated head (request line + headers + CRLFCRLF must be within cap),
            // matching the per-byte loop which rejected once the accumulated head exceeded MaxHeadBytes.
            if (bodyStart > MaxHeadBytes)
                return null;

            var headText = Encoding.ASCII.GetString(head.Buffer, 0, headEnd + 2); // include the final header CRLF
            var overread = head.Slice(bodyStart);

            var request = ParseHead(headText);
            if (request is null)
                return null;

            var body = await ReadBodyAsync(request.Headers, overread, ct).ConfigureAwait(false);
            if (body is null)
                return null;

            return request with { Body = body };
        }

        /// <summary>Reads the request body of the declared Content-Length, consuming overread bytes first.</summary>
        private async Task<byte[]?> ReadBodyAsync(Dictionary<string, string> headers, byte[] overread, CancellationToken ct)
        {
            if (!(headers.TryGetValue("Content-Length", out var clStr)
                  && long.TryParse(clStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength)
                  && contentLength > 0))
            {
                // No body: any overread belongs to the next pipelined request.
                _leftover = overread;
                return [];
            }

            if (contentLength > MaxBodyBytes)
                return null;

            var body = new byte[contentLength];
            var read = 0;

            // Satisfy the body from overread head bytes first.
            if (overread.Length > 0)
            {
                var fromOverread = (int)Math.Min(overread.Length, body.Length);
                Array.Copy(overread, 0, body, 0, fromOverread);
                read = fromOverread;
                if (overread.Length > fromOverread)
                    _leftover = overread[fromOverread..]; // tail past the body = next pipelined request
            }

            while (read < body.Length)
            {
                int n;
                try { n = await _stream.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false); }
                catch (IOException) { return null; }
                if (n == 0) return null; // EOF mid-body
                read += n;
            }

            return body;
        }

        /// <summary>Scans for <c>\r\n\r\n</c> starting near <paramref name="scanFrom"/>; returns the index of the first CR or -1.</summary>
        private static int IndexOfCrlfCrlf(byte[] buffer, int count, ref int scanFrom)
        {
            // Resume 3 bytes back so a terminator straddling the prior read boundary is still found.
            var start = Math.Max(0, scanFrom - 3);
            for (var i = start; i + 3 < count; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                    && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    return i;
            }
            scanFrom = count;
            return -1;
        }

        private static HttpRequest? ParseHead(string headText)
        {
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

            var keepAlive = DetermineKeepAlive(version, headers);
            return new HttpRequest(method, target, headers, [], keepAlive);
        }
    }

    /// <summary>Growable byte buffer for accumulating the request head without per-byte <c>List</c> overhead.</summary>
    private struct ByteAccumulator(byte[] seed)
    {
        private byte[] _buffer = seed.Length > 0 ? (byte[])seed.Clone() : new byte[512];
        private int _count = seed.Length;

        public readonly byte[] Buffer => _buffer;
        public readonly int Count => _count;

        public void Append(byte[] source, int length)
        {
            EnsureCapacity(_count + length);
            Array.Copy(source, 0, _buffer, _count, length);
            _count += length;
        }

        /// <summary>Copies bytes from <paramref name="from"/> to the end into a fresh array (the overread tail).</summary>
        public readonly byte[] Slice(int from)
        {
            if (from >= _count) return [];
            var slice = new byte[_count - from];
            Array.Copy(_buffer, from, slice, 0, slice.Length);
            return slice;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer.Length) return;
            var size = _buffer.Length * 2;
            while (size < needed) size *= 2;
            Array.Resize(ref _buffer, size);
        }
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
        if (request.Method == "OPTIONS" && path.StartsWith(IpcProtocol.IpcPrefix, StringComparison.Ordinal))
        {
            await WriteAsync(stream, 204, "No Content", null, [], corsHeaders, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(IpcProtocol.IpcCommandPrefix, StringComparison.Ordinal))
        {
            await HandleIpcCommandAsync(stream, request, corsHeaders, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(IpcProtocol.IpcEvalPrefix, StringComparison.Ordinal))
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

        var presented = request.Headers.TryGetValue(IpcProtocol.TokenHeader, out var t) ? t : "";
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
            headers.Add(("Access-Control-Allow-Headers", $"Content-Type, {IpcProtocol.TokenHeader}"));
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

        // One canonical containment rule for the whole framework (PAP-23): exact-root OR child-with-sep,
        // under the host case policy (ordinal on Linux, ordinal-ignore-case on macOS/Windows).
        return RynPath.IsContainedIn(combined, _contentDirectory, RynPath.HostComparison)
            ? combined
            : null; // traversal attempt
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
