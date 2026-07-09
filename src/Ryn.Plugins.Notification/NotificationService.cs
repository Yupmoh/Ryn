using Ryn.Plugins.Notification.Backends;

namespace Ryn.Plugins.Notification;

/// <summary>
/// Delivers notifications and reports activation. Backed by a per-OS <see cref="INotificationBackend"/>;
/// raises <see cref="Activated"/>/<see cref="Dismissed"/> (also forwarded to JS as
/// <c>notification.activated</c>/<c>notification.dismissed</c> by the plugin).
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly INotificationBackend _backend;
    private bool _disposed;

    /// <summary>Raised with the notification id when the user clicks a notification.</summary>
    public event Action<string>? Activated;

    /// <summary>Raised with the notification id when the OS reports a notification dismissed.</summary>
    public event Action<string>? Dismissed;

    internal NotificationService() : this(CreateBackend()) { }

    // Test seam.
    internal NotificationService(INotificationBackend backend)
    {
        _backend = backend;
        _backend.Activated += id => Activated?.Invoke(id);
        _backend.Dismissed += id => Dismissed?.Invoke(id);
    }

    private static INotificationBackend CreateBackend()
    {
        if (OperatingSystem.IsMacOS()) return new MacOsNotificationBackend();
        if (OperatingSystem.IsLinux()) return new LinuxNotificationBackend();
        if (OperatingSystem.IsWindows()) return new WindowsNotificationBackend();
        return new StubNotificationBackend();
    }

    /// <summary>Whether notifications can be delivered on this system.</summary>
    public bool IsSupported => _backend.IsSupported;

    /// <summary>True if notification permission is already granted, without prompting.</summary>
    public bool IsPermissionGranted() => _backend.IsPermissionGranted();

    /// <summary>Requests notification permission (may prompt). Returns whether it is now granted.</summary>
    public string RequestPermission() => _backend.RequestPermission() ? "granted" : "denied";

    /// <summary>
    /// Sends a notification with an explicit id echoed back by <see cref="Activated"/>/<see cref="Dismissed"/>.
    /// </summary>
    public void Send(string id, string title, string body, string? sound = null, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _backend.Send(new NotificationRequest(id, title ?? "", body ?? "", sound, iconPath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.Dispose();
    }
}
