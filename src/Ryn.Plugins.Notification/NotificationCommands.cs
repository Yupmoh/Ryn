using System.Diagnostics;
using Ryn.Ipc;

namespace Ryn.Plugins.Notification;

public static class NotificationCommands
{
    [RynCommand("notification.send")]
    public static void Send(string title, string body)
    {
        if (OperatingSystem.IsMacOS())
        {
            var escapedTitle = EscapeOsascript(title);
            var escapedBody = EscapeOsascript(body);
            RunProcess("osascript", $"-e display notification \"{escapedBody}\" with title \"{escapedTitle}\"");
        }
        else if (OperatingSystem.IsLinux())
        {
            RunProcess("notify-send", $"-- {EscapeShellArg(title)} {EscapeShellArg(body)}");
        }
        else if (OperatingSystem.IsWindows())
        {
            var escapedTitle = EscapePowerShell(title);
            var script = $"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(0); $xml.GetElementsByTagName('text')[0].AppendChild($xml.CreateTextNode('{escapedTitle}')); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Ryn').Show([Windows.UI.Notifications.ToastNotification]::new($xml))";
            RunProcess("powershell", $"-command \"{script}\"");
        }
    }

    [RynCommand("notification.sendWithSound")]
    public static void SendWithSound(string title, string body, string sound)
    {
        if (OperatingSystem.IsMacOS())
        {
            var escapedTitle = EscapeOsascript(title);
            var escapedBody = EscapeOsascript(body);
            var escapedSound = EscapeOsascript(sound);

            var soundClause = string.IsNullOrEmpty(sound) ? "" : $" sound name \"{escapedSound}\"";
            RunProcess("osascript", $"-e display notification \"{escapedBody}\" with title \"{escapedTitle}\"{soundClause}");
        }
        else if (OperatingSystem.IsLinux())
        {
            // notify-send doesn't have a cross-distro sound flag; send the notification normally
            RunProcess("notify-send", $"-- {EscapeShellArg(title)} {EscapeShellArg(body)}");
        }
        else if (OperatingSystem.IsWindows())
        {
            var escapedTitle = EscapePowerShell(title);
            var script = $"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(0); $xml.GetElementsByTagName('text')[0].AppendChild($xml.CreateTextNode('{escapedTitle}')); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Ryn').Show([Windows.UI.Notifications.ToastNotification]::new($xml))";
            RunProcess("powershell", $"-command \"{script}\"");
        }
    }

    [RynCommand("notification.isSupported")]
    public static bool IsSupported()
    {
        if (OperatingSystem.IsMacOS()) return true;
        if (OperatingSystem.IsLinux()) return true;
        if (OperatingSystem.IsWindows()) return true;
        return false;
    }

    [RynCommand("notification.requestPermission")]
    public static string RequestPermission()
    {
        if (OperatingSystem.IsMacOS())
            return "granted"; // osascript notifications always work

        if (OperatingSystem.IsLinux())
            return IsToolAvailable("notify-send") ? "granted" : "denied";

        if (OperatingSystem.IsWindows())
            return "granted"; // PowerShell toast always works

        return "denied";
    }

    private static string EscapeOsascript(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapeShellArg(string value)
    {
        // Wrap in single quotes; escape any embedded single quotes
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static void RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.WaitForExit();
    }

    private static bool IsToolAvailable(string tool)
    {
        var whichCommand = OperatingSystem.IsWindows() ? "where" : "which";

        var psi = new ProcessStartInfo
        {
            FileName = whichCommand,
            Arguments = tool,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
