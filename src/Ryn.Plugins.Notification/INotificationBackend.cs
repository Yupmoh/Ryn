namespace Ryn.Plugins.Notification;

/// <summary>A notification to deliver. Sound and icon are best-effort per platform.</summary>
internal sealed record NotificationRequest(string Id, string Title, string Body, string? Sound, string? IconPath);

/// <summary>
/// Platform notification delivery with activation reporting. Implementations raise <see cref="Activated"/>
/// when the user clicks a notification and <see cref="Dismissed"/> where the OS reports dismissal, both
/// carrying the original id. Delivery-only backends (no activation channel) simply never raise them.
/// </summary>
internal interface INotificationBackend : IDisposable
{
    /// <summary>Whether notifications can be delivered at all on this system.</summary>
    public bool IsSupported { get; }

    /// <summary>True if the app already holds notification permission, without prompting.</summary>
    public bool IsPermissionGranted();

    /// <summary>Requests permission (may prompt); returns whether it is now granted.</summary>
    public bool RequestPermission();

    /// <summary>Delivers a notification.</summary>
    public void Send(NotificationRequest request);

    /// <summary>Raised with the notification id when the user activates (clicks) it.</summary>
    public event Action<string>? Activated;

    /// <summary>Raised with the notification id when the OS reports it dismissed (where supported).</summary>
    public event Action<string>? Dismissed;
}
