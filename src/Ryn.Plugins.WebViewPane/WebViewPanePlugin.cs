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
            WirePaneTeardown(window, service.CloseAll);

        _services.GetService<Ryn.Core.Internal.RynWindowAccessor>()?.OnReady(rynWindow =>
            rynWindow.PageZoomChanged += (_, _) => service.ReapplyBoundsForPageZoom());

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Panes must be released inside the native close event, not after it: every saucer webview keeps a
    /// non-clearable window-closed listener that captures its native instance, and saucer fires the closed
    /// event over a snapshot of listeners — a pane freed during <c>Closed</c> has already been copied into
    /// that snapshot, so its listener runs on freed memory (SIGSEGV on macOS/Windows). Tearing down on
    /// <see cref="RynWindow.CloseApproved"/> (or <see cref="IRynWindow.Closing"/> for other window
    /// implementations) lets each pane unhook its listener before the snapshot is taken. The
    /// <see cref="IRynWindow.Closed"/> subscription stays as a safety net for close paths that skip the
    /// close event; closeAll is idempotent, so double invocation is harmless.
    /// </summary>
    internal static void WirePaneTeardown(IRynWindow window, Action closeAll)
    {
        if (window is RynWindow rynWindow)
            rynWindow.CloseApproved += (_, _) => closeAll();
        else
            window.Closing += (_, args) => { if (!args.Cancel) closeAll(); };

        window.Closed += (_, _) => closeAll();
    }
}
