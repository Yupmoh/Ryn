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

    [RynCommand("webviewPane.setBounds")]
    public void SetBounds(int id, int x, int y, int width, int height) =>
        _service.SetBounds(id, x, y, width, height);

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

    [RynCommand("webviewPane.execute")]
    public void Execute(int id, string code) => _service.Execute(id, code);

    [RynCommand("webviewPane.eval")]
    public Task<string> EvalAsync(int id, string code) => _service.EvalAsync(id, code);

    [RynCommand("webviewPane.url")]
    public string GetUrl(int id) => _service.GetUrl(id);

    [RynCommand("webviewPane.list")]
    public int[] List() => _service.List();
}
