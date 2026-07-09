using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.MenuBar;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed class MenuBarPlugin : IRynPlugin
#pragma warning restore CA1812
{
    private readonly IServiceProvider _services;

    public MenuBarPlugin(IServiceProvider services) => _services = services;

    public string Name => "menubar";

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var menuBarService = _services.GetRequiredService<MenuBarService>();
        menuBarService.EmitEvent = (eventName, jsonData) =>
        {
            var webView = _services.GetService<IRynWebView>();
            webView?.EmitEvent(eventName, jsonData);
        };
        // Queued through the main-thread dispatcher, which buffers pre-loop work — the standard menu is in
        // place from the first frame the event loop renders.
        menuBarService.ApplyStartupMenu();
        return ValueTask.CompletedTask;
    }
}
