using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Core.Internal;
using Ryn.Plugins.MenuBar.Backends;

namespace Ryn.Plugins.MenuBar;

public sealed class MenuBarService : IDisposable
{
    private readonly MenuBarOptions _options;
    private readonly IServiceProvider _services;
    private readonly IMenuBarBackend _backend;
    private bool _disposed;

    // Best-effort fullscreen toggle state for the toggleFullScreen role on platforms where Ryn emulates it
    // (IRynWindow.SetFullscreen has no getter).
    private bool _fullscreen;

    internal Action<string, string>? EmitEvent { get; set; }

    internal MenuBarService(MenuBarOptions options, IMainThreadDispatcher mainThread, IServiceProvider services)
        : this(options, services, CreateBackend(options, mainThread, services))
    {
    }

    // Test seam: lets tests drive the service against a fake backend without touching AppKit/Win32.
    internal MenuBarService(MenuBarOptions options, IServiceProvider services, IMenuBarBackend backend)
    {
        _options = options;
        _services = services;
        _backend = backend;

        _backend.MenuItemClicked += OnMenuItemClicked;
        _backend.RoleActivated += OnRoleActivated;
    }

    private static IMenuBarBackend CreateBackend(
        MenuBarOptions options, IMainThreadDispatcher mainThread, IServiceProvider services)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOsMenuBarBackend(mainThread, ResolveAppName(options));
        if (OperatingSystem.IsWindows())
            return new WindowsMenuBarBackend(mainThread, () =>
                services.GetService<RynWindowAccessor>()?.Window?.GetNativeWindowHandle() ?? 0);
        return new StubMenuBarBackend();
    }

    internal string AppName => ResolveAppName(_options);

    private static string ResolveAppName(MenuBarOptions options) =>
        options.AppName
        ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath)
        ?? "Application";

    /// <summary>Replaces the application menu. Top-level <c>appMenu</c>/<c>editMenu</c>/<c>windowMenu</c> roles expand to the standard menus.</summary>
    public void SetMenu(IReadOnlyList<MenuBarItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _backend.SetMenu(MenuBarDefaults.ExpandTopLevelRoles(items, AppName));
    }

    /// <summary>Restores the startup state: the platform-standard menu on macOS, no menu elsewhere.</summary>
    public void Reset()
    {
        if (OperatingSystem.IsMacOS())
            _backend.SetMenu(MenuBarDefaults.CreateDefault(AppName));
        else
            _backend.SetMenu([]);
    }

    /// <summary>
    /// Applies the default menu at startup when configured. macOS only — without App/Edit/Window menus the
    /// app sits below the platform baseline (no Cmd-Q/Cmd-C/Cmd-V); on Windows and Linux a menu bar appears
    /// only once the app explicitly sets one.
    /// </summary>
    internal void ApplyStartupMenu()
    {
        if (_options.ApplyDefaultMenu && OperatingSystem.IsMacOS())
            _backend.SetMenu(MenuBarDefaults.CreateDefault(AppName));
    }

    // Encode the item id as a proper JSON string (it can contain quotes/backslashes/control chars) rather
    // than naive concatenation, which would break the payload or allow script injection downstream.
    private void OnMenuItemClicked(string itemId) =>
        EmitEvent?.Invoke("menubar.itemClicked", $"\"{System.Text.Json.JsonEncodedText.Encode(itemId)}\"");

    // Only raised by backends without native role dispatch (Windows); macOS routes roles through the
    // responder chain and never gets here.
    private void OnRoleActivated(string roleName)
    {
        if (!MenuBarRoles.TryGet(roleName, out var role)) return;

        switch (role.Kind)
        {
            case RoleKind.Quit:
                _services.GetService<IRynApplicationLifetime>()?.RequestShutdown();
                break;

            case RoleKind.Window:
                var window = _services.GetService<IRynWindow>();
                if (window is null) break;
                switch (role.Name)
                {
                    case "close": window.Close(); break;
                    case "minimize": window.Minimize(); break;
                    case "zoom": window.ToggleMaximize(); break;
                    case "toggleFullScreen": window.SetFullscreen(_fullscreen = !_fullscreen); break;
                    default: break;
                }
                break;

            case RoleKind.Edit:
                // execCommand names match the role names exactly (undo/redo/cut/copy/paste/selectAll/delete).
                // Deprecated but universally supported in WebView2, and the only route that acts on the
                // focused editable element without focus-stealing.
#pragma warning disable CA2012 // Fire-and-forget: menu clicks have no completion to await or surface
                _services.GetService<IRynWebView>()
                    ?.EvaluateJavaScriptAsync($"document.execCommand('{role.Name}')");
#pragma warning restore CA2012
                break;

            case RoleKind.MacOnly:
            default:
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.MenuItemClicked -= OnMenuItemClicked;
        _backend.RoleActivated -= OnRoleActivated;
        _backend.Dispose();
    }
}
