using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

/// <summary>
/// Starts an interactive edge or corner resize of a GTK toplevel through GDK. saucer 8.0.4's GTK
/// <c>window::impl::start_resize</c> never reaches GDK: the lambda it posts receives the <c>impl</c>, calls
/// <c>impl::start_resize</c> again and re-posts itself on every main-loop iteration, so no resize starts and the
/// loop spins for the rest of the process. Ryn therefore calls <c>gdk_toplevel_begin_resize</c> itself, as saucer's
/// native layer would. Must run on the GTK UI thread.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class LinuxWindowResize
{
    private const int GdkButtonPrimary = 1;
    private const uint GdkButton1Mask = 1u << 8;
    private const uint GdkCurrentTime = 0;

    private enum GdkSurfaceEdge
    {
        NorthWest,
        North,
        NorthEast,
        West,
        East,
        SouthWest,
        South,
        SouthEast,
    }

    internal static void Start(nint gtkWindow, WindowEdge edge)
    {
        if (gtkWindow == 0 || ToGdkEdge(edge) is not { } gdkEdge)
            return;

        var surface = gtk_native_get_surface(gtk_widget_get_native((void*)gtkWindow));
        if (surface == null)
            return;
        var seat = gdk_display_get_default_seat(gdk_surface_get_display(surface));
        if (seat == null)
            return;
        var pointer = gdk_seat_get_pointer(seat);
        if (pointer == null)
            return;

        // The page requests the resize from its mousedown, so the primary button is normally still held. If it was
        // released before the request arrived, this was a click: starting a resize now would follow the pointer with
        // no button release left to end it. Only the button state decides: a fast drag can carry the pointer outside
        // the surface before the request arrives, and the resize must still start from wherever the pointer is.
        double x = 0, y = 0;
        uint mask = 0;
        _ = gdk_surface_get_device_position(surface, pointer, &x, &y, &mask);
        if ((mask & GdkButton1Mask) == 0)
            return;

        gdk_toplevel_begin_resize(surface, gdkEdge, pointer, GdkButtonPrimary, x, y, GdkCurrentTime);
    }

    private static GdkSurfaceEdge? ToGdkEdge(WindowEdge edge) => edge switch
    {
        WindowEdge.Top => GdkSurfaceEdge.North,
        WindowEdge.Bottom => GdkSurfaceEdge.South,
        WindowEdge.Left => GdkSurfaceEdge.West,
        WindowEdge.Right => GdkSurfaceEdge.East,
        WindowEdge.TopLeft => GdkSurfaceEdge.NorthWest,
        WindowEdge.TopRight => GdkSurfaceEdge.NorthEast,
        WindowEdge.BottomLeft => GdkSurfaceEdge.SouthWest,
        WindowEdge.BottomRight => GdkSurfaceEdge.SouthEast,
        _ => null,
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_native")]
    private static partial void* gtk_widget_get_native(void* widget);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_native_get_surface")]
    private static partial void* gtk_native_get_surface(void* native);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_surface_get_display")]
    private static partial void* gdk_surface_get_display(void* surface);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_default_seat")]
    private static partial void* gdk_display_get_default_seat(void* display);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_seat_get_pointer")]
    private static partial void* gdk_seat_get_pointer(void* seat);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_surface_get_device_position")]
    private static partial int gdk_surface_get_device_position(void* surface, void* device, double* x, double* y, uint* mask);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_toplevel_begin_resize")]
    private static partial void gdk_toplevel_begin_resize(
        void* toplevel, GdkSurfaceEdge edge, void* device, int button, double x, double y, uint timestamp);
}
