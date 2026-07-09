using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.GlobalShortcut;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed class GlobalShortcutPlugin : IRynPlugin
#pragma warning restore CA1812
{
    private readonly IServiceProvider _services;

    public GlobalShortcutPlugin(IServiceProvider services) => _services = services;

    public string Name => "globalShortcut";

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var service = _services.GetRequiredService<GlobalShortcutService>();
        service.EmitEvent = (eventName, jsonData) =>
        {
            var webView = _services.GetService<IRynWebView>();
            webView?.EmitEvent(eventName, jsonData);
        };
        return ValueTask.CompletedTask;
    }
}
