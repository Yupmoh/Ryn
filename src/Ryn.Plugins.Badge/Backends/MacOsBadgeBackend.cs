using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ryn.Core;

namespace Ryn.Plugins.Badge.Backends;

/// <summary>
/// macOS Dock badge via <c>NSApp.dockTile.badgeLabel</c>. Purely a property set on AppKit objects, so every
/// call is marshalled onto the main thread; there is no native state to tear down beyond clearing the label.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsBadgeBackend : IBadgeBackend
{
    private readonly IMainThreadDispatcher _mainThread;
    private bool _disposed;

    public MacOsBadgeBackend(IMainThreadDispatcher mainThread)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _mainThread = mainThread;
    }

    public void SetLabel(string? label)
    {
        if (_disposed) return;
        _mainThread.Post(() => SetLabelOnUi(label));
    }

    private unsafe void SetLabelOnUi(string? label)
    {
        if (_disposed) return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var nsApp = (void*)objc_msgSend_ret_nint(
                (void*)objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            var dockTile = (void*)objc_msgSend_ret_nint(nsApp, sel_registerName("dockTile"));
            // nil clears the badge; stringWithUTF8String: only for a real label.
            objc_msgSend_ptr(dockTile, sel_registerName("setBadgeLabel:"),
                label is null ? null : (void*)CreateNSString(label));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Clear the badge before flipping _disposed so the posted work isn't dropped by the guard; the Dock
        // badge outlives the process otherwise. InvokeAsync (not Post) so disposal waits for the clear while
        // the loop is still draining.
        _mainThread.InvokeAsync(() => SetLabelOnUi(null)).GetAwaiter().GetResult();
        _disposed = true;
    }

    private static unsafe nint CreateNSString(string str)
    {
        var utf8 = Encoding.UTF8.GetBytes(str + "\0");
        fixed (byte* ptr = utf8)
        {
            return objc_msgSend_ptr_ret_nint(
                (void*)objc_getClass("NSString"),
                sel_registerName("stringWithUTF8String:"),
                ptr);
        }
    }

    // --- ObjC Runtime P/Invoke ---

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_ptr(
        void* receiver, nint selector, void* value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ret_nint(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ptr_ret_nint(
        void* receiver, nint selector, void* value);
}
