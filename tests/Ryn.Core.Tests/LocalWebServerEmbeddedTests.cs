using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// Verifies the local HTTP server composes with embedded content: a bundled build serves the in-memory byte[]
/// map over http://localhost (so UseLocalServer works for scripts that reject the ryn:// origin, e.g. Cloudflare
/// Turnstile), with no loose wwwroot on disk. The on-disk content directory remains a dev-mode fallback.
/// </summary>
public sealed class LocalWebServerEmbeddedTests : IAsyncLifetime
{
    private LocalWebServer _server = null!;
    private HttpClient _client = null!;

    private static EmbeddedContentStore MakeStore(params (string Path, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = archive.CreateEntry(path).Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entry.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        return EmbeddedContentStore.LoadFrom(ms)!;
    }

    public async Task InitializeAsync()
    {
        // No content directory on disk — the server must serve purely from the in-memory embedded store.
        _server = new LocalWebServer(contentDirectory: null, preferredPort: 28760);
        _server.SetWebView(new FakeHost());
        _server.SetEmbeddedContent(MakeStore(
            ("index.html", "<html>EMBEDDED</html>"),
            ("assets/app.js", "console.log('embedded')")));
        await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_server.Url) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task ServesEmbeddedIndexAtRoot()
    {
        var resp = await _client.GetAsync("/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("EMBEDDED");
    }

    [Fact]
    public async Task ServesEmbeddedAssetWithMimeType()
    {
        var resp = await _client.GetAsync("/assets/app.js");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/javascript");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("embedded");
    }

    [Fact]
    public async Task UnknownRoute_FallsBackToEmbeddedIndex_ForSpa()
    {
        var resp = await _client.GetAsync("/some/client/route");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("EMBEDDED");
    }

    private sealed class FakeHost : ILocalServerHost
    {
        public string IpcToken { get; } = Guid.NewGuid().ToString("N");
        public Task<(bool Ok, string Data)> DispatchCommandFromServerAsync(string command, string body) => Task.FromResult((true, "null"));
        public void HandleEvalFromServer(long evalId, int ok, string body) { }
    }
}
