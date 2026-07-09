using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.WebViewPane;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed class WebViewPanePlugin : IRynPlugin
#pragma warning restore CA1812
{
    private readonly IServiceProvider _services;

    public WebViewPanePlugin(IServiceProvider services) => _services = services;

    public string Name => "webviewPane";

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var service = _services.GetRequiredService<WebViewPaneService>();
        service.EmitEvent = (eventName, jsonData) =>
        {
            var webView = _services.GetService<IRynWebView>();
            webView?.EmitEvent(eventName, jsonData);
        };

        // Panes are native children of the main window: tear them down when it closes rather than letting
        // freed-window teardown race live pane webviews.
        var window = _services.GetService<IRynWindow>();
        if (window is not null)
            window.Closed += (_, _) => service.CloseAll();

        return ValueTask.CompletedTask;
    }
}
