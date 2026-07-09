using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;
using Ryn.Interop;
using Ryn.Plugins.WebViewPane.Native;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Web-process crash detection and best-effort suspension, built per engine:
/// macOS hooks <c>webViewWebContentProcessDidTerminate:</c> onto saucer's navigation delegate class
/// (saucer does not implement that selector, so <c>class_addMethod</c> is a clean add — verified against
/// saucer 8's <c>NavigationDelegate</c>); Windows subscribes <c>ProcessFailed</c> and suspends with
/// <c>Controller.IsVisible=false</c> + <c>TrySuspend</c> (a real freeze); Linux connects
/// <c>web-process-terminated</c> and hides the GTK widget (WebKitGTK throttles unmapped views).
/// Suspending hides the pane on every platform — treat it as "pane is in the background".
/// </summary>
internal static unsafe partial class PaneLifecycleInterop
{
    /// <summary>Native webview pointer → crash callback. Keyed on the engine handle (idx 0).</summary>
    private static readonly ConcurrentDictionary<nint, Action<string>> CrashHandlers = new();
    private static int s_macHookInstalled;

    /// <summary>Registers crash reporting for a pane. Call on the UI thread after creation.</summary>
    public static void RegisterCrashHandler(saucer_webview* webview, Action<string> onTerminated)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0) return;
        CrashHandlers[native] = onTerminated;

        if (OperatingSystem.IsMacOS()) InstallMacHook(native);
        else if (OperatingSystem.IsWindows()) InstallWindowsHook(native);
        else if (OperatingSystem.IsLinux()) InstallLinuxHook(native);
    }

    /// <summary>Drops the pane's crash registration. Call before the webview is freed.</summary>
    public static void UnregisterCrashHandler(saucer_webview* webview)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native != 0) CrashHandlers.TryRemove(native, out _);
    }

    /// <summary>
    /// Hides the pane and throttles it: a real process freeze on WebView2 (after the visibility flip),
    /// a hidden NSView/GtkWidget elsewhere, which the engines background-throttle. Resume reverses it.
    /// </summary>
    public static void SetSuspended(saucer_webview* webview, bool suspended)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0)
            throw new InvalidOperationException("The pane's native webview handle is unavailable.");

        if (OperatingSystem.IsMacOS())
        {
            objc_msgSend_bool(native, PaneEngineInterop.sel_registerName("setHidden:"), suspended ? (byte)1 : (byte)0);
        }
        else if (OperatingSystem.IsWindows())
        {
            SetSuspendedWindows(webview, native, suspended);
        }
        else if (OperatingSystem.IsLinux())
        {
            gtk_widget_set_visible(native, suspended ? (byte)0 : (byte)1);
        }
        else
        {
            throw new PlatformNotSupportedException("webviewPane.setSuspended is not supported on this OS.");
        }
    }

    // --- macOS ---

    [SupportedOSPlatform("macos")]
    private static void InstallMacHook(nint wkWebView)
    {
        var delegateObj = objc_msgSend_ret(wkWebView, PaneEngineInterop.sel_registerName("navigationDelegate"));
        if (delegateObj == 0) return;

        if (Interlocked.Exchange(ref s_macHookInstalled, 1) == 0)
        {
            var cls = object_getClass(delegateObj);
            var selector = PaneEngineInterop.sel_registerName("webViewWebContentProcessDidTerminate:");
            // saucer's NavigationDelegate does not implement this optional selector; if a future saucer
            // does, adding fails and we simply keep saucer's behavior rather than fight over the IMP.
            _ = class_addMethod(cls, selector,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMacProcessTerminated, "v@:@");
        }

        // WebKit caches the delegate's respondsToSelector flags at assignment time, so the method we
        // just added is invisible until the delegate is re-assigned. Re-set it for every new pane.
        objc_msgSend_set(wkWebView, PaneEngineInterop.sel_registerName("setNavigationDelegate:"), delegateObj);
    }

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMacProcessTerminated(nint self, nint selector, nint wkWebView)
    {
        NativeGuard.Invoke("PaneLifecycleInterop.OnMacProcessTerminated", () =>
        {
            if (CrashHandlers.TryGetValue(wkWebView, out var handler))
                handler("webContentProcessTerminated");
        });
    }

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint object_getClass(nint obj);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint cls, nint selector, nint imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_ret(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_bool(nint receiver, nint selector, byte value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_set(nint receiver, nint selector, nint value);

    // --- Windows ---

    private static readonly Guid IidProcessFailedEventHandler = new("79e0aea4-990b-42d9-aa1d-0fcc2e5bc7f1");
    private static readonly Guid IidCoreWebView2_3 = new("A0D6DF20-3B92-416D-AA0C-437A9C727857");
    private static readonly Guid IidTrySuspendCompletedHandler = new("00f206a7-9d17-4605-91f6-4e8e4de192e3");

    private const int SlotAddProcessFailed = 25;      // ICoreWebView2::add_ProcessFailed
    private const int SlotProcessFailedKind = 3;      // ICoreWebView2ProcessFailedEventArgs::get_ProcessFailedKind
    private const int SlotControllerPutIsVisible = 4; // ICoreWebView2Controller::put_IsVisible
    private const int SlotTrySuspend = 68;            // ICoreWebView2_3::TrySuspend (3 + 58 + 7)
    private const int SlotResume = 69;                // ICoreWebView2_3::Resume

    private sealed class WindowsCrashSubscription
    {
        public required nint CoreWebView2 { get; init; }
    }

    [SupportedOSPlatform("windows")]
    private static void InstallWindowsHook(nint coreWebView2)
    {
        var handler = ComCallback.Create(IidProcessFailedEventHandler,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&OnWindowsProcessFailed,
            new WindowsCrashSubscription { CoreWebView2 = coreWebView2 });

        var addProcessFailed = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)
            (*(nint**)coreWebView2)[SlotAddProcessFailed];
        long token = 0;
        _ = addProcessFailed(coreWebView2, handler, &token);
        ComCallback.Release(handler); // the event source holds its reference until the webview dies
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindowsProcessFailed(nint comThis, nint sender, nint args)
    {
        NativeGuard.Invoke("PaneLifecycleInterop.OnWindowsProcessFailed", () =>
        {
            var subscription = ComCallback.GetCallback<WindowsCrashSubscription>(comThis);
            var kind = 9; // UnknownProcessExited
            if (args != 0)
            {
                var getKind = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)args)[SlotProcessFailedKind];
                int value = 0;
                if (getKind(args, &value) >= 0) kind = value;
            }
            if (CrashHandlers.TryGetValue(subscription.CoreWebView2, out var handler))
                handler(DescribeWindowsProcessFailedKind(kind));
        });
        return 0;
    }

    internal static string DescribeWindowsProcessFailedKind(int kind) => kind switch
    {
        0 => "browserProcessExited",
        1 => "renderProcessExited",
        2 => "renderProcessUnresponsive",
        3 => "frameRenderProcessExited",
        4 => "utilityProcessExited",
        5 => "sandboxHelperProcessExited",
        6 => "gpuProcessExited",
        7 => "ppapiPluginProcessExited",
        8 => "ppapiBrokerProcessExited",
        _ => "unknownProcessExited",
    };

    // CA1508: `core3` is written through a pointer by the native QueryInterface; the analyzer cannot see it.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    [SupportedOSPlatform("windows")]
    private static void SetSuspendedWindows(saucer_webview* webview, nint coreWebView2, bool suspended)
    {
        // Visibility lives on the controller (stable native idx 1); suspension on ICoreWebView2_3.
        var controller = GetNativeHandleAt(webview, 1);
        if (controller != 0)
        {
            var putIsVisible = (delegate* unmanaged[Stdcall]<nint, int, int>)
                (*(nint**)controller)[SlotControllerPutIsVisible];
            _ = putIsVisible(controller, suspended ? 0 : 1);
        }

        var iid = IidCoreWebView2_3;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)coreWebView2)[0];
        nint core3 = 0;
        if (queryInterface(coreWebView2, &iid, &core3) < 0 || core3 == 0)
            return; // visibility flip alone still throttles rendering

        try
        {
            if (suspended)
            {
                // Fire-and-forget: TrySuspend legitimately declines (open DevTools, pending downloads).
                var completed = ComCallback.Create(IidTrySuspendCompletedHandler,
                    (nint)(delegate* unmanaged[Stdcall]<nint, int, int, int>)&OnTrySuspendCompleted, string.Empty);
                var trySuspend = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)core3)[SlotTrySuspend];
                _ = trySuspend(core3, completed);
                ComCallback.Release(completed);
            }
            else
            {
                var resume = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)core3)[SlotResume];
                _ = resume(core3);
            }
        }
        finally
        {
            PaneEngineInterop.ComRelease(core3);
        }
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnTrySuspendCompleted(nint comThis, int errorCode, int isSuccessful) => 0;

    private static nint GetNativeHandleAt(saucer_webview* webview, nuint index)
    {
        nuint size = 0;
        Saucer.saucer_webview_native(webview, index, null, &size);
        if (size < (nuint)sizeof(nint) || size > 64) return 0;
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf)
        {
            Saucer.saucer_webview_native(webview, index, ptr, &size);
            return MemoryMarshal.Read<nint>(buf);
        }
    }

    // --- Linux ---

    [SupportedOSPlatform("linux")]
    private static void InstallLinuxHook(nint webKitWebView)
    {
        _ = g_signal_connect_data(webKitWebView, "web-process-terminated",
            (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnLinuxProcessTerminated,
            0, 0, 0);
    }

    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLinuxProcessTerminated(nint webKitWebView, int reason, nint userData)
    {
        NativeGuard.Invoke("PaneLifecycleInterop.OnLinuxProcessTerminated", () =>
        {
            if (CrashHandlers.TryGetValue(webKitWebView, out var handler))
                handler(DescribeLinuxTerminationReason(reason));
        });
    }

    internal static string DescribeLinuxTerminationReason(int reason) => reason switch
    {
        0 => "crashed",
        1 => "exceededMemoryLimit",
        2 => "terminatedByApi",
        _ => "unknown",
    };

    [LibraryImport("gobject", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint g_signal_connect_data(nint instance, string detailedSignal, nint callback,
        nint data, nint destroyData, int connectFlags);

    [LibraryImport("gtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_widget_set_visible(nint widget, byte visible);
}
