using System.Text.Json;
using Ryn.Ipc;

namespace Ryn.Plugins.WebViewPane;

[RynJsonContext(typeof(WebViewPaneJsonContext))]
#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class WebViewPaneCommands
#pragma warning restore CA1812
{
    private readonly WebViewPaneService _service;

    public WebViewPaneCommands(WebViewPaneService service) => _service = service;

    [RynCommand("webviewPane.open")]
    public async Task<int> OpenAsync(JsonElement options)
    {
        var request = JsonSerializer.Deserialize(
            options.GetRawText(), WebViewPaneJsonContext.Default.PaneOpenRequest) ?? new PaneOpenRequest();
        return await _service.OpenAsync(request).ConfigureAwait(false);
    }

    [RynCommand("webviewPane.close")]
    public Task<bool> CloseAsync(int id) => _service.CloseAsync(id);

    /// <summary>Top-left CSS pixels relative to the window content area on every platform.</summary>
    [RynCommand("webviewPane.setBounds")]
    public void SetBounds(int id, int x, int y, int width, int height) =>
        _service.SetBounds(id, x, y, width, height);

    /// <summary>Background color: #rgb/#rrggbb/#rrggbbaa/rgb()/rgba(). See PaneOpenRequest.Background.</summary>
    [RynCommand("webviewPane.setBackground")]
    public void SetBackground(int id, string color) => _service.SetBackground(id, color);

    [RynCommand("webviewPane.navigate")]
    public void Navigate(int id, string url) => _service.Navigate(id, url);

    [RynCommand("webviewPane.back")]
    public void Back(int id) => _service.Back(id);

    [RynCommand("webviewPane.forward")]
    public void Forward(int id) => _service.Forward(id);

    [RynCommand("webviewPane.reload")]
    public void Reload(int id) => _service.Reload(id);

    [RynCommand("webviewPane.setZoom")]
    public void SetZoom(int id, double factor) => _service.SetZoom(id, factor);

    [RynCommand("webviewPane.setDevTools")]
    public void SetDevTools(int id, bool enabled) => _service.SetDevTools(id, enabled);

    [RynCommand("webviewPane.cdpCall")]
    public Task<string> CdpCallAsync(int id, string method, string? paramsJson) =>
        _service.CdpCallAsync(id, method, paramsJson ?? "{}");

    [RynCommand("webviewPane.cdpSubscribe")]
    public Task CdpSubscribeAsync(int id, string eventName) => _service.CdpSubscribeAsync(id, eventName);

    [RynCommand("webviewPane.resolveDownload")]
    public Task ResolveDownloadAsync(long downloadId, string action, string? path) =>
        _service.ResolveDownloadAsync(downloadId,
            string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase), path);

    [RynCommand("webviewPane.setSuspended")]
    public Task SetSuspendedAsync(int id, bool suspended) => _service.SetSuspendedAsync(id, suspended);

    [RynCommand("webviewPane.reloadFromCrash")]
    public void ReloadFromCrash(int id) => _service.ReloadFromCrash(id);

    [RynCommand("webviewPane.screenshot")]
    public Task<string> ScreenshotAsync(int id) => _service.ScreenshotAsync(id);

    [RynCommand("webviewPane.find")]
    public Task<PaneFindResult> FindAsync(int id, string text, bool? forward, bool? matchCase) =>
        _service.FindAsync(id, text, forward ?? true, matchCase ?? false);

    [RynCommand("webviewPane.findNext")]
    public Task<PaneFindResult> FindNextAsync(int id, bool? forward) =>
        _service.FindNextAsync(id, forward ?? true);

    [RynCommand("webviewPane.findStop")]
    public Task<PaneFindResult> FindStopAsync(int id, bool? clearHighlights) =>
        _service.FindStopAsync(id, clearHighlights ?? true);

    [RynCommand("webviewPane.resolvePermission")]
    public Task<bool> ResolvePermissionAsync(long requestId, bool grant) =>
        _service.ResolvePermissionAsync(requestId, grant);

    [RynCommand("webviewPane.setUserAgent")]
    public Task SetUserAgentAsync(int id, string userAgent) => _service.SetUserAgentAsync(id, userAgent);

    [RynCommand("webviewPane.execute")]
    public void Execute(int id, string code) => _service.Execute(id, code);

    [RynCommand("webviewPane.eval")]
    public Task<string> EvalAsync(int id, string code) => _service.EvalAsync(id, code);

    [RynCommand("webviewPane.url")]
    public string GetUrl(int id) => _service.GetUrl(id);

    [RynCommand("webviewPane.list")]
    public int[] List() => _service.List();
}
