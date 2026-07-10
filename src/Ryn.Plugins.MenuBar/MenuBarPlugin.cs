using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core;

namespace Ryn.Plugins.MenuBar;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed partial class MenuBarPlugin : IRynPlugin
#pragma warning restore CA1812
{
    private readonly IServiceProvider _services;

    public MenuBarPlugin(IServiceProvider services) => _services = services;

    public string Name => "menubar";

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var menuBarService = _services.GetRequiredService<MenuBarService>();
        var logger = _services.GetService<ILoggerFactory>()?.CreateLogger("Ryn.Plugins.MenuBar");
        menuBarService.EmitEvent = (eventName, jsonData) =>
        {
            var webView = _services.GetService<IRynWebView>();
            if (webView is null)
            {
                // Without a webview the event can't reach the page — a silent drop here once cost a
                // downstream integrator an hour diagnosing dead menu clicks. Make it visible.
                if (logger is not null) LogEventDropped(logger, eventName);
                return;
            }
            webView.EmitEvent(eventName, jsonData);
        };
        // Queued through the main-thread dispatcher, which buffers pre-loop work — the standard menu is in
        // place from the first frame the event loop renders.
        menuBarService.ApplyStartupMenu();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "MenuBar event '{eventName}' dropped: no IRynWebView is available to deliver it to the page.")]
    private static partial void LogEventDropped(ILogger logger, string eventName);
}
