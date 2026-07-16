using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryn.Interop;

namespace Ryn.Core.Internal;

/// <summary>Applies page zoom to a live native webview. Must be called on the UI thread.</summary>
internal static unsafe partial class WebViewPageZoom
{
    internal const double Min = 0.25;
    internal const double Max = 5.0;

    // ICoreWebView2Controller vtable slot, verified against WebView2.h (SDK 1.0.2903.40); COM interface
    // layouts are ABI-frozen: IUnknown(0-2), get/put_IsVisible(3-4), get/put_Bounds(5-6), get/put_ZoomFactor(7-8).
    private const int SlotControllerPutZoomFactor = 8;

    internal static double Clamp(double factor) =>
        double.IsFinite(factor) ? Math.Clamp(factor, Min, Max) : 1.0;

    internal static bool TryApply(saucer_webview* webview, double factor)
    {
        if (webview == null) return false;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var wkWebView = GetNativeHandle(webview, 0);
                if (wkWebView == 0) return false;
                objc_msgSend_double((void*)wkWebView, sel_registerName("setPageZoom:"), factor);
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                var controller = GetNativeHandle(webview, 1);
                if (controller == 0) return false;
                var putZoomFactor = (delegate* unmanaged[Stdcall]<nint, double, int>)(*(nint**)controller)[SlotControllerPutZoomFactor];
                return putZoomFactor(controller, factor) >= 0;
            }

            if (OperatingSystem.IsLinux())
            {
                var webKitWebView = GetNativeHandle(webview, 0);
                if (webKitWebView == 0) return false;
                webkit_web_view_set_zoom_level(webKitWebView, factor);
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }

        return false;
    }

    private static nint GetNativeHandle(saucer_webview* webview, nuint index)
    {
        nuint size;
        Unsafe.SkipInit(out size);
        Saucer.saucer_webview_native(webview, index, null, &size);
        if (size < (nuint)sizeof(nint) || size > 64) return 0;
        Span<byte> buffer = stackalloc byte[(int)size];
        fixed (byte* pointer = buffer)
        {
            Saucer.saucer_webview_native(webview, index, pointer, &size);
            return MemoryMarshal.Read<nint>(buffer);
        }
    }

    // Ryn.Core's single DllImportResolver slot: NativeLibrary.SetDllImportResolver throws on a second
    // registration per assembly, so any future Linux P/Invoke in Core must route its library name through
    // this switch rather than registering its own resolver. Candidate sonames must stay in sync with
    // PaneEngineInterop.ResolveLinuxLibrary in Ryn.Plugins.WebViewPane (saucer 8 builds against
    // GTK4/libadwaita, WebKitGTK 6.0; the 4.1 fallback keeps webkit2gtk-4.1 builds resolving).
    static WebViewPageZoom()
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(WebViewPageZoom).Assembly, ResolveLinuxLibrary);
    }

    private static nint ResolveLinuxLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = libraryName switch
        {
            "webkitgtk" => ["libwebkitgtk-6.0.so.4", "libwebkitgtk-6.0.so", "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.1.so"],
            _ => [],
        };
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return 0;
    }

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_double(void* receiver, nint selector, double value);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void webkit_web_view_set_zoom_level(nint webView, double zoomLevel);
}
