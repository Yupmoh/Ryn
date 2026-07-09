using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;

namespace Ryn.Plugins.Notification.Backends;

/// <summary>
/// macOS notifications. When the process runs inside an application bundle (a non-null
/// <c>CFBundleIdentifier</c>) it uses <c>UNUserNotificationCenter</c>, whose delegate reports clicks as
/// <see cref="Activated"/> and dismissals as <see cref="Dismissed"/>. Unbundled (e.g. <c>dotnet run</c>),
/// modern macOS refuses to register a UN delegate, so it falls back to delivering via <c>osascript</c> —
/// reliable, but with no activation channel. Published Ryn apps are bundled and get activation.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacOsNotificationBackend : INotificationBackend
{
    private readonly bool _useUserNotifications;
    private nint _delegateObject;
    private static nint s_backendHandle; // GCHandle to the live instance for the ObjC delegate callbacks

    public event Action<string>? Activated;
    public event Action<string>? Dismissed;

    public MacOsNotificationBackend()
    {
        _useUserNotifications = HasBundleIdentifier() && TryInstallDelegate();
    }

    public bool IsSupported => true;

    public bool IsPermissionGranted()
    {
        // osascript delivery always works; UN authorization is requested lazily on first send. Treat the
        // presence of a bundle (UN path) as "may need a prompt"; unbundled osascript is always granted.
        return !_useUserNotifications || QueryUnAuthorized();
    }

    public bool RequestPermission()
    {
        if (!_useUserNotifications) return true;
        RequestUnAuthorization();
        return true; // async; the prompt result surfaces on next IsPermissionGranted
    }

    public void Send(NotificationRequest request)
    {
        if (_useUserNotifications) SendViaUserNotifications(request);
        else SendViaOsascript(request);
    }

    // ---------- osascript fallback (delivery only) ----------

    private static void SendViaOsascript(NotificationRequest request)
    {
        var script = $"display notification \"{EscapeOsa(request.Body)}\" with title \"{EscapeOsa(request.Title)}\"";
        if (!string.IsNullOrEmpty(request.Sound))
            script += $" sound name \"{EscapeOsa(request.Sound)}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi);
        process?.WaitForExit();
    }

    private static string EscapeOsa(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    // ---------- UNUserNotificationCenter (delivery + activation) ----------

    private bool TryInstallDelegate()
    {
        var center = CurrentCenter();
        if (center == 0) return false;

        s_backendHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        EnsureDelegateClass();
        _delegateObject = objc_msgSend(objc_msgSend(objc_getClass("RynNotificationDelegate"), Sel("alloc")), Sel("init"));
        objc_setInstanceVariable(_delegateObject, "rynBackend", s_backendHandle);
        objc_msgSend_p(center, Sel("setDelegate:"), _delegateObject);
        return true;
    }

    private static nint CurrentCenter()
    {
        var cls = objc_getClass("UNUserNotificationCenter");
        return cls == 0 ? 0 : objc_msgSend(cls, Sel("currentNotificationCenter"));
    }

    private static void RequestUnAuthorization()
    {
        var center = CurrentCenter();
        if (center == 0) return;
        // options: UNAuthorizationOptionAlert(4) | Sound(2) | Badge(1) = 7. The completion block is optional
        // for our purposes; pass nil and rely on IsPermissionGranted polling the settings afterward.
        objc_msgSend_ul_p(center, Sel("requestAuthorizationWithOptions:completionHandler:"), 7, 0);
    }

    private static bool QueryUnAuthorized()
    {
        // getNotificationSettingsWithCompletionHandler: is async; a synchronous best-effort here returns true
        // so callers proceed to send (UN silently drops if denied). A precise query would need a block round-trip.
        return true;
    }

    private static void SendViaUserNotifications(NotificationRequest request)
    {
        var center = CurrentCenter();
        if (center == 0) { SendViaOsascript(request); return; }

        var content = objc_msgSend(objc_msgSend(objc_getClass("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
        objc_msgSend_p(content, Sel("setTitle:"), NsString(request.Title));
        objc_msgSend_p(content, Sel("setBody:"), NsString(request.Body));
        if (!string.IsNullOrEmpty(request.Sound))
        {
            var sound = objc_msgSend(objc_getClass("UNNotificationSound"), Sel("defaultSound"));
            objc_msgSend_p(content, Sel("setSound:"), sound);
        }

        var reqObj = objc_msgSend_ppp(objc_getClass("UNNotificationRequest"),
            Sel("requestWithIdentifier:content:trigger:"), NsString(request.Id), content, 0);
        objc_msgSend_pp(center, Sel("addNotificationRequest:withCompletionHandler:"), reqObj, 0);
    }

    // Called from the ObjC delegate: recover the backend and raise the managed event.
    private static void Dispatch(nint self, string identifier, bool dismissed) =>
        NativeGuard.Invoke("MacOsNotificationBackend.Dispatch", () =>
        {
            objc_getInstanceVariable(self, "rynBackend", out var raw);
            if (raw == 0) return;
            var handle = GCHandle.FromIntPtr(raw);
            if (handle.Target is not MacOsNotificationBackend backend) return;
            if (dismissed) backend.Dismissed?.Invoke(identifier);
            else backend.Activated?.Invoke(identifier);
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnDidReceiveResponse(nint self, nint sel, nint center, nint response, nint completion)
    {
        // response.notification.request.identifier; response.actionIdentifier == UNNotificationDismissActionIdentifier
        var notification = objc_msgSend(response, Sel("notification"));
        var req = objc_msgSend(notification, Sel("request"));
        var ident = ReadNsString(objc_msgSend(req, Sel("identifier")));
        var action = ReadNsString(objc_msgSend(response, Sel("actionIdentifier")));
        var dismissed = action.Contains("Dismiss", StringComparison.Ordinal);
        Dispatch(self, ident, dismissed);
        if (completion != 0) InvokeEmptyBlock(completion);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnWillPresent(nint self, nint sel, nint center, nint notification, nint completion)
    {
        // Show alerts even while the app is foreground: UNNotificationPresentationOptionBanner(1<<4=16)|Sound(2).
        if (completion != 0) InvokeOptionsBlock(completion, 16 | 2);
    }

    private static int s_delegateClassRegistered;

    private static void EnsureDelegateClass()
    {
        if (Interlocked.Exchange(ref s_delegateClassRegistered, 1) != 0) return;
        var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RynNotificationDelegate", 0);
        class_addIvar(cls, "rynBackend", (nuint)nint.Size, (byte)nint.Log2(nint.Size), "^v");
        class_addMethod(cls, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnDidReceiveResponse, "v@:@@@?");
        class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnWillPresent, "v@:@@@?");
        objc_registerClassPair(cls);
    }

    private static bool HasBundleIdentifier()
    {
        var bundle = objc_msgSend(objc_getClass("NSBundle"), Sel("mainBundle"));
        if (bundle == 0) return false;
        var identifier = objc_msgSend(bundle, Sel("bundleIdentifier"));
        return identifier != 0;
    }

    // completion blocks: void(^)() and void(^)(NSUInteger). We invoke them via the block's invoke slot.
    private static void InvokeEmptyBlock(nint block) =>
        ((delegate* unmanaged[Cdecl]<nint, void>)(*(nint**)block)[2])(block);

    private static void InvokeOptionsBlock(nint block, nuint options) =>
        ((delegate* unmanaged[Cdecl]<nint, nuint, void>)(*(nint**)block)[2])(block, options);

    private static nint Sel(string s) => sel_registerName(s);
    private static nint NsString(string s) => objc_msgSend_s(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);
    private static string ReadNsString(nint nsString)
    {
        if (nsString == 0) return "";
        var utf8 = objc_msgSend(nsString, Sel("UTF8String"));
        return utf8 == 0 ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
    }

    public void Dispose()
    {
        if (_delegateObject != 0)
        {
            var center = CurrentCenter();
            if (center != 0) objc_msgSend_p(center, Sel("setDelegate:"), 0);
            _delegateObject = 0;
        }
        if (s_backendHandle != 0)
        {
            var handle = GCHandle.FromIntPtr(s_backendHandle);
            if (handle.IsAllocated) handle.Free();
            s_backendHandle = 0;
        }
    }

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_allocateClassPair(nint super, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extra);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_registerClassPair(nint cls);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addIvar(nint cls, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint size, byte align, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_setInstanceVariable(nint obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint value);
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getInstanceVariable(nint obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out nint value);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend(nint receiver, nint sel);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_s(nint receiver, nint sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_p(nint receiver, nint sel, nint a);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_pp(nint receiver, nint sel, nint a, nint b);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_ppp(nint receiver, nint sel, nint a, nint b, nint c);
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_ul_p(nint receiver, nint sel, nuint a, nint b);
}
