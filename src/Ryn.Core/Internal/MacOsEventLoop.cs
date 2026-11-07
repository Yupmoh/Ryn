using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>
/// Wakes the AppKit event loop after saucer stops it. saucer quits with <c>[NSApp stop:nil]</c>, which only sets
/// a flag that NSApplication checks after it handles the next event. A quit issued from a posted callback (last
/// window closed, shutdown request, cancellation) therefore leaves an idle loop blocked until unrelated input
/// arrives. Posting an application-defined event lets <c>saucer_application_run</c> return at once. macOS only;
/// safe from any thread, since <c>postEvent:atStart:</c> is documented as callable from subthreads.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static partial class MacOsEventLoop
{
    private const nuint NSEventTypeApplicationDefined = 15;

    internal static void Wake()
    {
        // Pool the autoreleased NSEvent: callers include thread-pool threads that have no AppKit pool.
        var pool = objc_autoreleasePoolPush();
        try
        {
            var sharedApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (sharedApp == 0)
                return;
            var wakeEvent = objc_msgSend_otherEvent(
                objc_getClass("NSEvent"),
                sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                NSEventTypeApplicationDefined, default, 0, 0, 0, 0, 0, 0, 0);
            if (wakeEvent != 0)
                objc_msgSend_postEvent(sharedApp, sel_registerName("postEvent:atStart:"), wakeEvent, 1);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X;
        public double Y;
    }

    private const string Libobjc = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Libobjc)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [LibraryImport(Libobjc)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    // +[NSEvent otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:]
    [LibraryImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_otherEvent(
        nint receiver, nint selector, nuint type, NSPoint location, nuint modifierFlags, double timestamp,
        nint windowNumber, nint context, short subtype, nint data1, nint data2);

    // -[NSApplication postEvent:atStart:]
    [LibraryImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_postEvent(nint receiver, nint selector, nint nsEvent, byte atStart);

    [LibraryImport(Libobjc)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Libobjc)]
    private static partial void objc_autoreleasePoolPop(nint pool);
}
