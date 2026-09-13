using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// CORS regression tests for the <see cref="LocalWebServer"/> IPC endpoints. The allow-origin decision must
/// agree with the request guard: a request the host authorizes must also pass the browser's CORS check, or the
/// page reports a network error for a command the server already accepted. Trust is explicit per origin — the
/// configured cross-origin value, the server's own origin, and runtime authorizations via
/// <see cref="LocalWebServer.AuthorizeIpcOrigin"/>. A loopback origin is NOT trusted by default: the webview may
/// navigate to an unrelated localhost service, and that page holds the token-bearing bridge, so an unapproved
/// origin must be rejected even with a valid token.
/// </summary>
public sealed class LocalWebServerCorsTests : IAsyncLifetime
{
    private const string ConfiguredOrigin = "http://127.0.0.1:31000";

    /// <summary>Loopback origin that is *not* the configured one — the mid-session port change.</summary>
    private const string DriftedOrigin = "http://127.0.0.1:31001";

    private LocalWebServer _server = null!;
    private FakeHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _host = new FakeHost();
        // IPC-only server with an external dev-server origin, as RynWindow configures it for a loopback Url.
        _server = new LocalWebServer(contentDirectory: null, preferredPort: 29450, allowedCorsOrigin: ConfiguredOrigin);
        _server.SetWebView(_host);
        await _server.StartAsync();

        _port = int.Parse(_server.Url[(_server.Url.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Preflight_FromConfiguredOrigin_EchoesAllowOrigin()
    {
        var response = await SendPreflightAsync(ConfiguredOrigin);

        StatusOf(response).Should().Be(204);
        HeaderOf(response, "Access-Control-Allow-Origin").Should().Be(ConfiguredOrigin,
            "the configured cross-origin value is trusted by construction");
        HeaderOf(response, "Vary").Should().Be("Origin");
    }

    [Fact]
    public async Task Preflight_FromUnapprovedLoopbackOrigin_HasNoAllowOrigin()
    {
        var response = await SendPreflightAsync(DriftedOrigin);

        StatusOf(response).Should().Be(204);
        HeaderOf(response, "Access-Control-Allow-Origin").Should().BeNull(
            "a loopback origin the host never approved must not be allowed — trust is explicit, not address-based");
        HeaderOf(response, "Vary").Should().Be("Origin", "the response varies by Origin either way");
    }

    [Fact]
    public async Task IpcCommand_WithValidToken_FromUnapprovedLoopbackOrigin_IsForbidden()
    {
        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: {DriftedOrigin}\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(403,
            "a valid token must not substitute for origin trust: any navigated page holds the bridge, so an " +
            "unrelated localhost service must stay locked out until the host authorizes it");
    }

    [Fact]
    public async Task IpcCommand_AfterRuntimeAuthorization_FromDriftedOrigin_IsAccepted()
    {
        _host.OnDispatch = (_, _) => Task.FromResult((true, "{\"ok\":true}"));

        _server.AuthorizeIpcOrigin(DriftedOrigin);

        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: {DriftedOrigin}\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(200, "the host explicitly trusted the drifted page origin");
        HeaderOf(response, "Access-Control-Allow-Origin").Should().Be(DriftedOrigin,
            "an authorized cross-origin command response must be readable by the page");
    }

    [Fact]
    public async Task IpcEval_AfterRuntimeAuthorization_CarriesAllowOrigin()
    {
        _server.AuthorizeIpcOrigin(DriftedOrigin);

        var response = await SendRawAsync(
            $"POST /ipc/eval/1/1 HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: {DriftedOrigin}\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(200);
        HeaderOf(response, "Access-Control-Allow-Origin").Should().Be(DriftedOrigin,
            "the eval response channel must carry the same CORS headers as the command channel");
    }

    [Fact]
    public async Task Preflight_FromServerOwnOrigin_EchoesAllowOrigin()
    {
        // Pages served by this server are same-origin clients: their POSTs carry the server's own origin,
        // which is trusted by construction (no host action required).
        var response = await SendPreflightAsync($"http://localhost:{_port}");

        StatusOf(response).Should().Be(204);
        HeaderOf(response, "Access-Control-Allow-Origin").Should().Be($"http://localhost:{_port}");
    }

    [Fact]
    public void AuthorizeIpcOrigin_RejectsNonOrigin()
    {
        var act = () => _server.AuthorizeIpcOrigin("not an origin");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task IpcCommand_FromNonLoopbackOrigin_IsForbidden()
    {
        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: https://evil.example.com\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(403, "the request guard must keep rejecting untrusted origins");
    }

    // ---- raw-socket helpers ----

    private Task<string> SendPreflightAsync(string origin) => SendRawAsync(
        $"OPTIONS /ipc/cmd/1/x.y HTTP/1.1\r\n" +
        $"Host: localhost:{_port}\r\n" +
        $"Origin: {origin}\r\n" +
        $"Access-Control-Request-Method: POST\r\n" +
        $"Access-Control-Request-Headers: content-type,{IpcProtocol.TokenHeader}\r\n" +
        $"Connection: close\r\n\r\n");

    private async Task<string> SendRawAsync(string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(rawRequest));
        await stream.FlushAsync();
        return await ReadAllAsync(stream);
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
        }
        catch (OperationCanceledException) { /* idle keep-alive connection: return what we have */ }
        catch (IOException) { }
        return sb.ToString();
    }

    private static int StatusOf(string response)
    {
        var firstSpace = response.IndexOf(' ', StringComparison.Ordinal);
        var secondSpace = response.IndexOf(' ', firstSpace + 1);
        return int.Parse(response[(firstSpace + 1)..secondSpace], CultureInfo.InvariantCulture);
    }

    private static string? HeaderOf(string response, string name)
    {
        var headEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headEnd < 0 ? response : response[..headEnd];
        foreach (var line in head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private sealed class FakeHost : ILocalServerHost
    {
        public string IpcToken { get; } = Guid.NewGuid().ToString("N");

        public Func<string, string, Task<(bool, string)>> OnDispatch { get; set; } =
            (_, _) => Task.FromResult((true, "null"));

        public Task<(bool Ok, string Data)> DispatchCommandFromServerAsync(string command, string body)
            => OnDispatch(command, body);

        public void HandleEvalFromServer(long evalId, int ok, string body) { }
    }
}
