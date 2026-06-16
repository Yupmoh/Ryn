using System.Globalization;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// Raw-socket regression tests for <see cref="LocalWebServer"/> that an <c>HttpClient</c> cannot express,
/// because the client normalizes/validates the wire before the server ever sees it. A real
/// <see cref="TcpClient"/> writes literal request bytes so the server's own parser and guards are exercised:
/// <list type="bullet">
/// <item>TST-06 — DNS-rebinding defenses: a forged non-loopback <c>Host</c> is rejected; a present
/// disallowed <c>Origin</c> is rejected (even with a valid token).</item>
/// <item>PAP-20 — the buffered head reader: a request whose head is split across packets parses, and two
/// pipelined requests on one keep-alive connection both parse.</item>
/// </list>
/// </summary>
public sealed class LocalWebServerRawSocketTests : IAsyncLifetime
{
    private string _contentDir = "";
    private LocalWebServer _server = null!;
    private FakeHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _contentDir = Path.Combine(Path.GetTempPath(), $"ryn-raw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentDir);
        await File.WriteAllTextAsync(Path.Combine(_contentDir, "index.html"), "<html>INDEX</html>");

        _host = new FakeHost();
        _server = new LocalWebServer(_contentDir, preferredPort: 29400);
        _server.SetWebView(_host);
        await _server.StartAsync();

        // Url is http://localhost:{port}
        _port = int.Parse(_server.Url[(_server.Url.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_contentDir, true); } catch (IOException) { }
    }

    // ---- TST-06: DNS-rebinding / origin enforcement ----

    [Fact]
    public async Task IpcCommand_WithForgedNonLoopbackHost_IsForbidden()
    {
        // Valid token but a forged Host header (the DNS-rebinding vector): the server must reject because the
        // Host is not a loopback name/address.
        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: attacker.example.com\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(403, "a non-loopback Host must be rejected even with a valid token");
    }

    [Fact]
    public async Task IpcCommand_WithValidLoopbackHostAndToken_IsAllowed()
    {
        _host.OnDispatch = (_, _) => Task.FromResult((true, "{\"ok\":true}"));

        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{_port}\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(200);
    }

    [Fact]
    public async Task IpcCommand_WithDisallowedOrigin_IsForbidden()
    {
        // A present, non-loopback, non-allowed Origin must be rejected even when the token and Host are valid.
        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: https://evil.example.com\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(403, "a disallowed cross-site Origin must be rejected");
    }

    [Fact]
    public async Task IpcCommand_WithLoopbackOrigin_IsAllowed()
    {
        _host.OnDispatch = (_, _) => Task.FromResult((true, "{\"ok\":true}"));

        var response = await SendRawAsync(
            $"POST /ipc/cmd/1/x.y HTTP/1.1\r\n" +
            $"Host: localhost:{_port}\r\n" +
            $"Origin: http://localhost:{_port}\r\n" +
            $"{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\n" +
            $"Content-Length: 2\r\n" +
            $"Connection: close\r\n\r\n" +
            $"{{}}");

        StatusOf(response).Should().Be(200, "a same-origin loopback Origin is permitted");
    }

    // ---- PAP-20: buffered head reader (split + pipelined) ----

    [Fact]
    public async Task RequestHeadSplitAcrossPackets_StillParses()
    {
        _host.OnDispatch = (cmd, _) => Task.FromResult((true, $"\"{cmd}\""));

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        var stream = client.GetStream();

        // Send the head in two separate flushes with the boundary mid-header, so the reader must accumulate
        // across reads to find CRLFCRLF.
        var part1 = $"POST /ipc/cmd/1/split.cmd HTTP/1.1\r\nHost: localhost:{_port}\r\n{IpcProtocol.TokenHeader}: ";
        var part2 = $"{_host.IpcToken}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(part1));
        await stream.FlushAsync();
        await Task.Delay(20); // ensure the first chunk lands as its own read on the server
        await stream.WriteAsync(Encoding.ASCII.GetBytes(part2));
        await stream.FlushAsync();

        var response = await ReadAllAsync(stream);
        StatusOf(response).Should().Be(200, "a head split across packets must still parse");
        BodyOf(response).Should().Contain("split.cmd");
    }

    [Fact]
    public async Task TwoPipelinedRequests_OnOneKeepAliveConnection_BothParse()
    {
        _host.OnDispatch = (cmd, _) => Task.FromResult((true, $"\"{cmd}\""));

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        var stream = client.GetStream();

        // Two full requests written back-to-back in a single flush; the second is sent before the first's
        // response is read. The reader must carry the overread tail of request 1 into request 2.
        var first =
            $"POST /ipc/cmd/1/first.cmd HTTP/1.1\r\nHost: localhost:{_port}\r\n{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\nContent-Length: 2\r\n\r\n{{}}";
        var second =
            $"POST /ipc/cmd/2/second.cmd HTTP/1.1\r\nHost: localhost:{_port}\r\n{IpcProtocol.TokenHeader}: {_host.IpcToken}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(first + second));
        await stream.FlushAsync();

        var response = await ReadAllAsync(stream);

        // Both responses arrive on the same connection; both command names must appear in order.
        var firstIdx = response.IndexOf("first.cmd", StringComparison.Ordinal);
        var secondIdx = response.IndexOf("second.cmd", StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThanOrEqualTo(0, "the first pipelined request must parse");
        secondIdx.Should().BeGreaterThan(firstIdx, "the second pipelined request must parse after the first");
    }

    // ---- raw-socket helpers ----

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
        // Read until the server closes the connection (Connection: close) or a short idle timeout elapses.
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
        catch (OperationCanceledException) { /* idle: keep-alive connection left open — return what we have */ }
        catch (IOException) { }
        return sb.ToString();
    }

    private static int StatusOf(string response)
    {
        // "HTTP/1.1 NNN Reason\r\n..."
        var firstSpace = response.IndexOf(' ', StringComparison.Ordinal);
        var secondSpace = response.IndexOf(' ', firstSpace + 1);
        var code = response[(firstSpace + 1)..secondSpace];
        return int.Parse(code, CultureInfo.InvariantCulture);
    }

    private static string BodyOf(string response)
    {
        var idx = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return idx < 0 ? "" : response[(idx + 4)..];
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
