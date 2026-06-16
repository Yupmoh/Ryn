using Ryn.Core;
using Ryn.Plugins.Tray.Backends;

namespace Ryn.Plugins.Tray;

public sealed class TrayService : IDisposable
{
    private readonly TrayOptions _options;
    private readonly ITrayBackend _backend;
    private bool _disposed;

    internal Action<string, string>? EmitEvent { get; set; }

    internal TrayService(TrayOptions options, IMainThreadDispatcher mainThread)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _options = options;

        if (OperatingSystem.IsWindows())
            _backend = new WindowsTrayBackend();
        else if (OperatingSystem.IsMacOS())
            _backend = new MacOsTrayBackend(mainThread);
        else if (OperatingSystem.IsLinux())
            _backend = new LinuxTrayBackend();
        else
            _backend = new StubTrayBackend();

        _backend.IconClicked += OnIconClicked;
        _backend.MenuItemClicked += OnMenuItemClicked;
    }

    public void Show() => _backend.Show(_options.IconPath, _options.Tooltip);
    public void Hide() => _backend.Hide();
    public void SetTooltip(string tooltip) => _backend.SetTooltip(tooltip);
    public void SetMenu(IReadOnlyList<TrayMenuItem> items) => _backend.SetMenu(items);
    public void ShowNotification(string title, string message) => _backend.ShowNotification(title, message);

    private void OnIconClicked() => EmitEvent?.Invoke("tray.clicked", "null");

    // Encode the item id as a proper JSON string (it can contain quotes/backslashes/control chars) rather
    // than naive concatenation, which would break the payload or allow script injection downstream.
    private void OnMenuItemClicked(string itemId) =>
        EmitEvent?.Invoke("tray.menuItemClicked", $"\"{System.Text.Json.JsonEncodedText.Encode(itemId)}\"");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.IconClicked -= OnIconClicked;
        _backend.MenuItemClicked -= OnMenuItemClicked;
        _backend.Dispose();
    }
}
