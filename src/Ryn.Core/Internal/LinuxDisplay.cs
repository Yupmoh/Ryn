using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>Identifies the active GTK display backend after GTK has initialized.</summary>
internal static unsafe partial class LinuxDisplay
{
    private const string WaylandDisplayType = "GdkWaylandDisplay";

    internal static bool IsNativeWayland()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var display = gdk_display_get_default();
        if (display == null)
            return false;

        var typeName = g_type_name_from_instance(display);
        return IsNativeWaylandType(Marshal.PtrToStringUTF8((nint)typeName));
    }

    internal static bool IsNativeWaylandType(string? typeName) =>
        string.Equals(typeName, WaylandDisplayType, StringComparison.Ordinal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_default")]
    private static partial void* gdk_display_get_default();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_name_from_instance")]
    private static partial byte* g_type_name_from_instance(void* instance);
}
