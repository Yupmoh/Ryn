using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// End-to-end tests for the hand-rolled loopback HTTP server: drives it with a real HttpClient and a fake
/// host, exercising static serving, SPA fallback, the IPC contract, and token/auth enforcement.
/// </summary>
public sealed class LocalWebServerTests : IAsyncLifetime
{
    private string _contentDir = "";
    private LocalWebServer _server = null!;
    private FakeHost _host = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _contentDir = Path.Combine(Path.GetTempPath(), $"ryn-srv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentDir);
        await File.WriteAllTextAsync(Path.Combine(_contentDir, "index.html"), "<html>INDEX</html>");
        await File.WriteAllTextAsync(Path.Combine(_contentDir, "app.js"), "console.log(1)");

        _host = new FakeHost();
        // Port 0 is coerced to the default; pass a high uncommon port and rely on the fallback range.
        _server = new LocalWebServer(_contentDir, preferredPort: 28730);
        _server.SetWebView(_host);
        await _server.StartAsync();

        _port = int.Parse(_server.Url[(_server.Url.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        _client = new HttpClient { BaseAddress = new Uri(_server.Url) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_contentDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ServesIndexAtRoot()
    {
        var resp = await _client.GetAsync("/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("INDEX");
    }

    [Fact]
    public async Task ServesStaticAssetWithMimeType()
    {
        var resp = await _client.GetAsync("/app.js");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/javascript");
    }

    [Fact]
    public async Task UnknownRoute_FallsBackToIndex_ForSpa()
    {
        var resp = await _client.GetAsync("/some/client/route");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("INDEX");
    }

    [Theory]
    // HttpClient would normalize these before they ever reach the server, so this MUST go over a raw socket
    // (TST-03): a literal "../", a percent-encoded "%2e%2e%2f", and backslash variants must all be contained
    // by ResolveWithinContent and never serve an out-of-root file.
    [InlineData("/../../../../etc/passwd")]
    [InlineData("/%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd")]
    [InlineData("/..%2f..%2f..%2fetc%2fpasswd")]
    [InlineData("/..\\..\\..\\..\\etc\\passwd")]
    [InlineData("/static/../../../../etc/passwd")]
    public async Task PathTraversal_OverRawSocket_NeverServesOutOfRoot(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Write the literal request line so the server's own parser/ResolveWithinContent is exercised.
        var response = await SendRawAsync(
            $"GET {target} HTTP/1.1\r\nHost: localhost:{_port}\r\nConnection: close\r\n\r\n");

        // Whatever the server returns (index fallback or 404), it must never be the contents of /etc/passwd.
        response.Should().NotContain("root:", "a traversal must never escape the content root");
        // It must still be a well-formed HTTP response, not a crash/hang.
        response.Should().StartWith("HTTP/1.1 ");
        var status = StatusOf(response);
        status.Should().BeOneOf([200, 404], "a traversal resolves to the SPA index fallback or a not-found");
    }

    private async Task<string> SendRawAsync(string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(rawRequest));
        await stream.FlushAsync();

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
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return sb.ToString();
    }

    private static int StatusOf(string response)
    {
        var firstSpace = response.IndexOf(' ', StringComparison.Ordinal);
        var secondSpace = response.IndexOf(' ', firstSpace + 1);
        return int.Parse(response[(firstSpace + 1)..secondSpace], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task IpcCommand_WithToken_Dispatches_AndReturnsResultInline()
    {
        _host.OnDispatch = (cmd, body) => Task.FromResult((true, $"{{\"cmd\":\"{cmd}\"}}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ipc/cmd/7/fs.readTextFile")
        {
            Content = new StringContent("{\"path\":\"x\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Ryn-Token", _host.IpcToken);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("{\"cmd\":\"fs.readTextFile\"}");
    }

    [Fact]
    public async Task IpcCommand_Error_Returns500_WithMessageInline()
    {
        _host.OnDispatch = (_, _) => Task.FromResult((false, "boom"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ipc/cmd/1/x.y")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Ryn-Token", _host.IpcToken);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await resp.Content.ReadAsStringAsync()).Should().Be("boom");
    }

    [Fact]
    public async Task IpcCommand_WithoutToken_IsForbidden()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ipc/cmd/1/x.y")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IpcCommand_WithWrongToken_IsForbidden()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ipc/cmd/1/x.y")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Ryn-Token", "not-the-token");
        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
