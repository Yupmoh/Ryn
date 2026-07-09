using Ryn.Ipc;

namespace Ryn.Plugins.Notification;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class NotificationCommands
#pragma warning restore CA1812
{
    private readonly NotificationService _service;
    private int _autoId;

    public NotificationCommands(NotificationService service) => _service = service;

    [RynCommand("notification.send")]
    public void Send(string title, string body) => _service.Send(NextId(), title, body);

    [RynCommand("notification.sendWithSound")]
    public void SendWithSound(string title, string body, string sound) =>
        _service.Send(NextId(), title, body, sound: sound);

    [RynCommand("notification.sendWithIcon")]
    public void SendWithIcon(string title, string body, string iconPath) =>
        _service.Send(NextId(), title, body, iconPath: iconPath);

    /// <summary>
    /// Sends a notification with a caller-chosen id echoed back by the <c>notification.activated</c> and
    /// <c>notification.dismissed</c> events, so a click can be routed to the right in-app target.
    /// </summary>
    [RynCommand("notification.sendWithId")]
    public void SendWithId(string id, string title, string body, string? sound, string? iconPath) =>
        _service.Send(id, title, body, sound, iconPath);

    [RynCommand("notification.isSupported")]
    public bool IsSupported() => _service.IsSupported;

    /// <summary>Queries whether permission is already granted, without prompting.</summary>
    [RynCommand("notification.isPermissionGranted")]
    public bool IsPermissionGranted() => _service.IsPermissionGranted();

    [RynCommand("notification.requestPermission")]
    public string RequestPermission() => _service.RequestPermission();

    private string NextId() => "auto-" + Interlocked.Increment(ref _autoId).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
