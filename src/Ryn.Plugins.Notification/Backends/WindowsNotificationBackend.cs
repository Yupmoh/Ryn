using System.Diagnostics;
using System.Runtime.Versioning;

namespace Ryn.Plugins.Notification.Backends;

/// <summary>
/// Windows notifications via a WinRT toast, delivered by a short PowerShell host (no WinRT projection is
/// available under NativeAOT). Delivery is reliable for any app; <b>activation</b> (click-to-focus) is a
/// documented gap here: WinRT toast activation for an <i>unpackaged</i> app requires a Start-menu shortcut
/// carrying the app's AUMID and a registered COM activator, which is app-packaging state Ryn cannot set up
/// from the plugin. Published/packaged Ryn apps that register an AUMID get OS-level activation; until then
/// this backend never raises <see cref="Activated"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNotificationBackend : INotificationBackend
{
    private const string AppUserModelId = "Ryn";

#pragma warning disable CS0067 // raised only when a packaged AUMID + COM activator is registered (see class remarks)
    public event Action<string>? Activated;
    public event Action<string>? Dismissed;
#pragma warning restore CS0067

    public bool IsSupported => true;
    public bool IsPermissionGranted() => true; // Windows shows toasts without an explicit per-app grant
    public bool RequestPermission() => true;

    public void Send(NotificationRequest request)
    {
        var title = EscapePs(request.Title);
        var body = EscapePs(request.Body);
        var tag = EscapePs(request.Id);
        var script = string.Concat(
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; ",
            "$t = '<toast><visual><binding template=\"ToastText02\">",
            $"<text id=\"1\">{title}</text><text id=\"2\">{body}</text>",
            "</binding></visual></toast>'; ",
            "$xml = [Windows.Data.Xml.Dom.XmlDocument]::new(); $xml.LoadXml($t); ",
            "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); ",
            $"$toast.Tag = '{tag}'; ",
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppUserModelId}').Show($toast)");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi);
        process?.WaitForExit();
    }

    private static string EscapePs(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal);

    public void Dispose() { }
}
