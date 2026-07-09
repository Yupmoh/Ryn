namespace Ryn.Plugins.Notification.Backends;

/// <summary>No-op backend for unsupported platforms. Reports unsupported and drops sends.</summary>
internal sealed class StubNotificationBackend : INotificationBackend
{
#pragma warning disable CS0067 // events never raised on an unsupported platform
    public event Action<string>? Activated;
    public event Action<string>? Dismissed;
#pragma warning restore CS0067

    public bool IsSupported => false;
    public bool IsPermissionGranted() => false;
    public bool RequestPermission() => false;
    public void Send(NotificationRequest request) { }
    public void Dispose() { }
}
