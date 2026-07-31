using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

// Saucer 8.0.4 observes GtkWindow's remembered default size, which stays stale during compositor-driven
// frameless resizing on Wayland. GdkSurface::layout reports the actual configured surface dimensions.
internal static unsafe partial class LinuxWindowResizeObserver
{
    internal static nuint Install(nint gtkWindow, void* userdata)
    {
        if (!OperatingSystem.IsLinux() || gtkWindow == 0)
            return 0;

        var widget = (void*)gtkWindow;
        var surface = gtk_native_get_surface(gtk_widget_get_native(widget));
        if (surface != null)
        {
            var window = NativeCallbackHelper.Resolve<RynWindow>(userdata);
            window.AttachLinuxResizeSurface((nint)surface, ConnectSurface(surface, userdata));
            return 0;
        }

        return g_signal_connect_data(
            widget,
            "realize",
            (nint)(delegate* unmanaged[Cdecl]<void*, void*, void>)&OnRealize,
            (nint)userdata,
            0,
            0);
    }

    internal static void Uninstall(nint gtkWindow, nuint realizeHandler, nint surface, nuint layoutHandler)
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (layoutHandler != 0 && surface != 0)
            g_signal_handler_disconnect(surface, layoutHandler);
        if (realizeHandler != 0 && gtkWindow != 0)
            g_signal_handler_disconnect(gtkWindow, realizeHandler);
    }

    internal static nuint ConnectSurface(void* surface, void* userdata) =>
        g_signal_connect_data(
            surface,
            "layout",
            (nint)(delegate* unmanaged[Cdecl]<void*, int, int, void*, void>)&OnLayout,
            (nint)userdata,
            0,
            0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRealize(void* widget, void* userdata)
    {
        NativeGuard.Invoke("LinuxWindowResizeObserver.OnRealize", () =>
        {
            var surface = gtk_native_get_surface(gtk_widget_get_native(widget));
            if (surface == null)
                return;

            var window = NativeCallbackHelper.Resolve<RynWindow>(userdata);
            window.AttachLinuxResizeSurface((nint)surface, ConnectSurface(surface, userdata));
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLayout(void* surface, int width, int height, void* userdata)
    {
        var window = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("LinuxWindowResizeObserver.OnLayout", () => window.HandleNativeResize(width, height));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_native")]
    private static partial void* gtk_widget_get_native(void* widget);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_native_get_surface")]
    private static partial void* gtk_native_get_surface(void* native);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint g_signal_connect_data(
        void* instance,
        string detailedSignal,
        nint callback,
        nint data,
        nint destroyData,
        int connectFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")]
    private static partial void g_signal_handler_disconnect(nint instance, nuint handlerId);
}
