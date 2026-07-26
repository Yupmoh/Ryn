using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;

namespace Ryn.Plugins.Notification.Backends;

/// <summary>
/// Linux notifications over libnotify. Ryn's GTK application loop dispatches the libnotify signals:
/// notifications carry a default "activate" action whose <c>ActionInvoked</c> signal raises
/// <see cref="Activated"/> and whose <c>closed</c> signal raises <see cref="Dismissed"/> — real
/// click-to-focus. If libnotify can't be loaded it falls back to the <c>notify-send</c> CLI (delivery only,
/// no activation).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class LinuxNotificationBackend : INotificationBackend
{
    private readonly bool _useLibnotify;
    private static nint s_backendHandle;

    public event Action<string>? Activated;
    public event Action<string>? Dismissed;

    public LinuxNotificationBackend()
    {
        _useLibnotify = TryInitLibnotify();
    }

    public bool IsSupported => _useLibnotify || IsToolAvailable("notify-send");
    public bool IsPermissionGranted() => IsSupported; // Linux has no per-app notification permission gate
    public bool RequestPermission() => IsSupported;

    public void Send(NotificationRequest request)
    {
        if (_useLibnotify) SendViaLibnotify(request);
        else SendViaNotifySend(request);
    }

    // ---------- libnotify (delivery + activation) ----------

    private bool TryInitLibnotify()
    {
        try
        {
            if (notify_init("Ryn") == 0) return false;
            s_backendHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static void SendViaLibnotify(NotificationRequest request)
    {
        var n = notify_notification_new(request.Title, request.Body,
            string.IsNullOrEmpty(request.IconPath) ? null : request.IconPath);
        if (n == 0) { SendViaNotifySend(request); return; }

        // A "default" action makes the whole bubble clickable on most shells.
        notify_notification_add_action(n, "default", "Open",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnActionInvoked, NsIdHandle(request.Id), 0);
        _ = g_signal_connect_data(n, "closed",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnClosed, NsIdHandle(request.Id), 0, 0);
        _ = notify_notification_show(n, 0);
    }

    // The action/closed callbacks carry the id as a strdup'd user_data pointer.
    private static nint NsIdHandle(string id) => Marshal.StringToHGlobalAnsi(id);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnActionInvoked(nint notification, nint action, nint userData)
        => NativeGuard.Invoke("LinuxNotificationBackend.OnActionInvoked", () => Resolve()?.Activated?.Invoke(ReadId(userData)));

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnClosed(nint notification, nint userData)
        => NativeGuard.Invoke("LinuxNotificationBackend.OnClosed", () => Resolve()?.Dismissed?.Invoke(ReadId(userData)));

    private static LinuxNotificationBackend? Resolve() =>
        s_backendHandle != 0 && GCHandle.FromIntPtr(s_backendHandle).Target is LinuxNotificationBackend b ? b : null;

    private static string ReadId(nint userData) => userData == 0 ? "" : Marshal.PtrToStringAnsi(userData) ?? "";

    // ---------- notify-send fallback (delivery only) ----------

    private static void SendViaNotifySend(NotificationRequest request)
    {
        if (!IsToolAvailable("notify-send"))
            throw new InvalidOperationException(
                "notify-send is not installed. Install libnotify (e.g. 'sudo apt install libnotify-bin').");

        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--urgency=normal");
        if (!string.IsNullOrEmpty(request.IconPath)) psi.ArgumentList.Add($"--icon={request.IconPath}");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(request.Title);
        psi.ArgumentList.Add(request.Body);
        using var process = Process.Start(psi);
        process?.WaitForExit();
    }

    private static bool IsToolAvailable(string tool)
    {
        var psi = new ProcessStartInfo { FileName = "which", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(tool);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public void Dispose()
    {
        if (s_backendHandle != 0)
        {
            var handle = GCHandle.FromIntPtr(s_backendHandle);
            if (handle.IsAllocated) handle.Free();
            s_backendHandle = 0;
        }
    }

    static LinuxNotificationBackend()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxNotificationBackend).Assembly, (name, asm, path) =>
        {
            string[] candidates = name switch
            {
                "notify" => ["libnotify.so.4", "libnotify.so"],
                "glib" => ["libglib-2.0.so.0", "libglib-2.0.so"],
                "gobject" => ["libgobject-2.0.so.0", "libgobject-2.0.so"],
                _ => [],
            };
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out var h)) return h;
            return 0;
        });
    }

    [LibraryImport("notify", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int notify_init(string appName);
    [LibraryImport("notify", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint notify_notification_new(string summary, string body, string? icon);
    [LibraryImport("notify", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void notify_notification_add_action(nint n, string action, string label, nint callback, nint userData, nint freeFunc);
    [LibraryImport("notify")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int notify_notification_show(nint n, nint error);
    [LibraryImport("gobject", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint g_signal_connect_data(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
}
