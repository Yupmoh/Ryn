using System.Runtime.InteropServices;
using Ryn.Interop;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Direct engine calls for pane features the saucer C ABI does not cover, built on the stable native
/// handles saucer exposes (<c>saucer_webview_native</c>): WKWebView* on macOS, ICoreWebView2_2* on
/// Windows (raw COM vtable calls — no built-in COM interop under NativeAOT), WebKitWebView* on Linux.
/// All methods must be called on the UI thread with a live webview.
/// </summary>
internal static unsafe partial class PaneEngineInterop
{
    /// <summary>
    /// Sets the pane's user agent for subsequent navigations. Applies immediately on macOS/Linux;
    /// WebView2 applies it on the next navigation.
    /// </summary>
    public static void SetUserAgent(saucer_webview* webview, string userAgent)
    {
        var native = GetNativeHandle(webview);
        if (native == 0)
            throw new InvalidOperationException("The pane's native webview handle is unavailable.");

        if (OperatingSystem.IsMacOS())
        {
            // WKWebView.customUserAgent = ua
            var nsString = CreateNSString(userAgent);
            objc_msgSend_ptr((void*)native, sel_registerName("setCustomUserAgent:"), (void*)nsString);
        }
        else if (OperatingSystem.IsWindows())
        {
            SetUserAgentWindows(native, userAgent);
        }
        else if (OperatingSystem.IsLinux())
        {
            var settings = webkit_web_view_get_settings(native);
            if (settings == 0)
                throw new InvalidOperationException("WebKitGTK settings are unavailable for this pane.");
            webkit_settings_set_user_agent(settings, userAgent);
        }
        else
        {
            throw new PlatformNotSupportedException("webviewPane.setUserAgent is not supported on this OS.");
        }
    }

    /// <summary>Reads a stable native pointer for a saucer webview; 0 if unavailable.</summary>
    internal static nint GetNativeHandle(saucer_webview* webview, nuint index = 0)
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

    // --- Windows: raw COM vtable calls against WebView2 ---
    // Vtable slots verified against WebView2.h (SDK 1.0.2903.40); COM interface layouts are ABI-frozen.

    private static readonly Guid IidCoreWebView2Settings2 = new("ee9a0f68-f46c-4e32-ac23-ef8cac224d2a");

    private const int SlotQueryInterface = 0;
    private const int SlotRelease = 2;
    private const int SlotControllerPutBounds = 6;  // ICoreWebView2Controller::put_Bounds
    private const int SlotWebView2GetSettings = 3;   // ICoreWebView2::get_Settings
    private const int SlotSettings2PutUserAgent = 22; // IUnknown(3) + ICoreWebView2Settings(18) + put_UserAgent(1)

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct WindowsRect(int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// Applies pane bounds immediately on WebView2. Saucer 8.0.x stores <c>set_bounds</c> but defers its
    /// controller update until the parent window resizes (saucer#69); keeping this beside the normal saucer
    /// call preserves its state while avoiding a full-window pane until that resize occurs.
    /// </summary>
    internal static bool ApplyWindowsBounds(saucer_webview* webview, nint hwnd, int x, int y, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || webview == null) return false;

        var controller = GetNativeHandle(webview, 1);
        if (controller == 0) return false;

        var dpi = hwnd != 0 ? GetDpiForWindow(hwnd) : 0;
        var bounds = ScaleWindowsBounds(x, y, width, height, dpi == 0 ? 96u : dpi);
        var putBounds = (delegate* unmanaged[Stdcall]<nint, WindowsRect, int>)(*(nint**)controller)[SlotControllerPutBounds];
        return putBounds(controller, bounds) >= 0;
    }

    internal static WindowsRect ScaleWindowsBounds(int x, int y, int width, int height, uint dpi)
    {
        var factor = dpi / 96f;
        var left = (int)(x * factor);
        var top = (int)(y * factor);
        var scaledWidth = (int)(width * factor);
        var scaledHeight = (int)(height * factor);
        return new WindowsRect(left, top, left + scaledWidth, top + scaledHeight);
    }

    // CA1508: the analyzer cannot model writes through out-pointers passed to native function pointers,
    // so it thinks the post-call null checks are dead. They are not.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    private static void SetUserAgentWindows(nint coreWebView2, string userAgent)
    {
        // ICoreWebView2::get_Settings — the ICoreWebView2_2* saucer hands out derives from ICoreWebView2.
        var getSettings = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)coreWebView2)[SlotWebView2GetSettings];
        nint settings = 0;
        if (getSettings(coreWebView2, &settings) < 0 || settings == 0)
            throw new InvalidOperationException("Failed to read WebView2 settings.");

        try
        {
            var iid = IidCoreWebView2Settings2;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)settings)[SlotQueryInterface];
            nint settings2 = 0;
            if (queryInterface(settings, &iid, &settings2) < 0 || settings2 == 0)
                throw new PlatformNotSupportedException(
                    "This WebView2 runtime does not support user-agent overrides (ICoreWebView2Settings2).");

            try
            {
                var putUserAgent = (delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)settings2)[SlotSettings2PutUserAgent];
                int hr;
                fixed (char* ua = userAgent)
                {
                    hr = putUserAgent(settings2, ua);
                }
                if (hr < 0)
                    throw new InvalidOperationException($"WebView2 rejected the user agent (HRESULT 0x{hr:X8}).");
            }
            finally
            {
                ComRelease(settings2);
            }
        }
        finally
        {
            ComRelease(settings);
        }
    }

    internal static void ComRelease(nint unknown)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[SlotRelease];
        _ = release(unknown);
    }

    // --- macOS: ObjC runtime ---

    internal static nint CreateNSString(string value)
    {
        // Autoreleased; safe on the UI thread where AppKit's run loop drains the pool.
        return objc_msgSend_nint_str(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U4)]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void objc_msgSend_ptr(void* receiver, nint selector, void* arg);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint objc_msgSend_nint_str(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    // --- Linux: WebKitGTK ---
    // The webkitgtk candidate sonames must stay in sync with WebViewPageZoom.ResolveLinuxLibrary in Ryn.Core.
    // saucer 8 builds against GTK4/libadwaita (WebKitGTK 6.0 API); older 4.1 kept as a fallback so a
    // saucer built against webkit2gtk-4.1 still resolves.

    static PaneEngineInterop()
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(PaneEngineInterop).Assembly, ResolveLinuxLibrary);
    }

    private static nint ResolveLinuxLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = libraryName switch
        {
            "webkitgtk" => ["libwebkitgtk-6.0.so.4", "libwebkitgtk-6.0.so", "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.1.so"],
            "cairo" => ["libcairo.so.2", "libcairo.so"],
            "glib" => ["libglib-2.0.so.0", "libglib-2.0.so"],
            "gobject" => ["libgobject-2.0.so.0", "libgobject-2.0.so"],
            "gtk" => ["libgtk-4.so.1", "libgtk-4.so", "libgtk-3.so.0", "libgtk-3.so"],
            _ => [],
        };
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }
        return 0;
    }

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint webkit_web_view_get_settings(nint webView);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void webkit_settings_set_user_agent(nint settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string userAgent);
}
