using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>
/// Brings the application to the foreground on macOS via <c>[NSApp activateIgnoringOtherApps:YES]</c>. saucer
/// presents a window but does not make the process the active application, and a terminal-launched (non-bundled)
/// binary does not activate on its own, so without this the window is visible while its app stays inactive (no
/// focus, behind other apps). macOS only; must run on the UI thread (AppKit is not thread-safe).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static partial class MacOsAppActivation
{
    internal static void Activate()
    {
        var appClass = objc_getClass("NSApplication");
        if (appClass == 0)
            return;
        var sharedApp = objc_msgSend(appClass, sel_registerName("sharedApplication"));
        if (sharedApp == 0)
            return;
        objc_msgSend_bool(sharedApp, sel_registerName("activateIgnoringOtherApps:"), 1);
    }

    private const string Libobjc = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Libobjc)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_bool(nint receiver, nint selector, byte arg);
}
