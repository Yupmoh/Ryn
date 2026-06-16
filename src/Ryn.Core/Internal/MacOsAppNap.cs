using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>
/// Disables macOS App Nap for the application's lifetime. App Nap throttles a process that is not the active,
/// foreground app — slowing or suspending its run loop, timers, and the main GCD queue. For a GUI app that
/// must keep rendering and processing posted UI work (e.g. opening a window from a background thread) while it
/// is in the background, that throttling makes that work stall until the app is brought to the foreground.
/// Starting a long-lived <c>NSProcessInfo</c> "user initiated" activity opts the process out of App Nap while
/// still allowing the system to sleep normally. macOS only; a no-op (and never called) elsewhere.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static partial class MacOsAppNap
{
    // NSActivityUserInitiatedAllowingIdleSystemSleep: prevents App Nap / automatic termination but does NOT
    // keep the display or system awake (0x00FFFFFF == NSActivityUserInitiated without the idle-sleep bit).
    private const ulong NSActivityUserInitiatedAllowingIdleSystemSleep = 0x00FFFFFF;

    // Retained activity token; held for the process lifetime so the activity stays in effect.
    private static nint _activity;

    internal static void Disable()
    {
        if (_activity != 0)
            return;

        var processInfoClass = objc_getClass("NSProcessInfo");
        var stringClass = objc_getClass("NSString");
        if (processInfoClass == 0 || stringClass == 0)
            return;

        var processInfo = objc_msgSend(processInfoClass, sel_registerName("processInfo"));
        if (processInfo == 0)
            return;

        var reasonPtr = Marshal.StringToHGlobalAnsi("Ryn UI event loop");
        nint reason;
        try
        {
            reason = objc_msgSend_ptr(stringClass, sel_registerName("stringWithUTF8String:"), reasonPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(reasonPtr);
        }
        if (reason == 0)
            return;

        var activity = objc_msgSend_activity(
            processInfo,
            sel_registerName("beginActivityWithOptions:reason:"),
            NSActivityUserInitiatedAllowingIdleSystemSleep,
            reason);
        if (activity != 0)
            _activity = objc_msgSend(activity, sel_registerName("retain"));
    }

    private const string Libobjc = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Libobjc)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_ptr(nint receiver, nint selector, nint arg);

    [LibraryImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_activity(nint receiver, nint selector, ulong options, nint reason);
}
