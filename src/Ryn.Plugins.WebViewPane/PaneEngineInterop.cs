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

    /// <summary>Reads the first stable native pointer (idx 0) for a saucer webview; 0 if unavailable.</summary>
    internal static nint GetNativeHandle(saucer_webview* webview)
    {
        nuint size = 0;
        Saucer.saucer_webview_native(webview, 0, null, &size);
        if (size < (nuint)sizeof(nint) || size > 64) return 0;
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf)
        {
            Saucer.saucer_webview_native(webview, 0, ptr, &size);
            return MemoryMarshal.Read<nint>(buf);
        }
    }

    // --- Windows: raw COM vtable calls against WebView2 ---
    // Vtable slots verified against WebView2.h (SDK 1.0.2903.40); COM interface layouts are ABI-frozen.

    private static readonly Guid IidCoreWebView2Settings2 = new("ee9a0f68-f46c-4e32-ac23-ef8cac224d2a");

    private const int SlotQueryInterface = 0;
    private const int SlotRelease = 2;
    private const int SlotWebView2GetSettings = 3;   // ICoreWebView2::get_Settings
    private const int SlotSettings2PutUserAgent = 22; // IUnknown(3) + ICoreWebView2Settings(18) + put_UserAgent(1)

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
    // saucer 8 builds against GTK4/libadwaita (WebKitGTK 6.0 API); older 4.1 kept as a fallback so a
    // saucer built against webkit2gtk-4.1 still resolves.

    static PaneEngineInterop()
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(PaneEngineInterop).Assembly, ResolveLinuxLibrary);
    }

    private static nint ResolveLinuxLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "webkitgtk") return 0;
        foreach (var candidate in (string[])
                 ["libwebkitgtk-6.0.so.4", "libwebkitgtk-6.0.so", "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.1.so"])
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
