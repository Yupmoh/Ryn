using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

[SupportedOSPlatform("macos")]
internal static partial class MacOsTitleBar
{
    // Last left-mousedown NSEvent per swizzled NSWindow*, retained between mousedown and mouseup. When the
    // page's injected title-bar script rules the point draggable (live-DOM verdict at click time), the
    // window.beginNativeDrag command lands here and the drag starts from this REAL originating event via
    // performWindowDragWithEvent: — so the IPC delay costs nothing: AppKit anchors the drag to the original
    // mousedown location and the window can never desync from the cursor (unlike dragging from
    // NSApp.currentEvent, which by IPC time is a later, unrelated event).
    private readonly record struct PendingMouseDown(nint Event, long TimestampMs);
    private static readonly ConcurrentDictionary<nint, PendingMouseDown> Pending = new();

    // Declared window class → original sendEvent: IMP, saved before we IMP-swap our observer in. The
    // observer calls the ORIGINAL IMP directly (never objc_msgSendSuper), so it is recursion-proof and
    // KVO-transparent: the window's runtime (isa) class is often a dynamically-registered KVO/notifying
    // class we must not re-parent — WKWebView and saucer both observe the window — so we swap the method
    // on the DECLARED class ([window class]) instead of touching the isa.
    private static readonly ConcurrentDictionary<nint, nint> OriginalSendEvent = new();
    private static readonly object SwizzleLock = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    internal static unsafe void Apply(nint nsWindowPtr, bool overlay)
    {
        if (nsWindowPtr == 0) return;

        var nsWindow = (void*)nsWindowPtr;

        // Hide title text, make title bar transparent
        objc_msgSend_nint(nsWindow, sel_registerName("setTitleVisibility:"), 1);
        objc_msgSend_bool(nsWindow, sel_registerName("setTitlebarAppearsTransparent:"), 1);

        // Overlay: content extends under title bar (fullSizeContentView).
        if (overlay)
        {
            var mask = objc_msgSend_ret_nint(nsWindow, sel_registerName("styleMask"));
            objc_msgSend_nint(nsWindow, sel_registerName("setStyleMask:"), mask | (1 << 15));
        }
        // Hidden: no fullSizeContentView — title bar stays as a separate native strip
        // with drag and traffic lights. Content renders below it.
    }

    /// <summary>
    /// IMP-swaps <c>sendEvent:</c> on the window's declared class so the latest left-mousedown event is
    /// retained (released again on mouseup) and everything is forwarded to the original implementation.
    /// Passive on its own: a drag only starts when the page verdict arrives via <see cref="BeginDrag"/>.
    /// Idempotent; one swap per declared class covers every window of that class.
    /// </summary>
    internal static unsafe void InstallDragMonitor(nint nsWindowPtr)
    {
        if (nsWindowPtr == 0) return;
        lock (SwizzleLock)
        {
            // The DECLARED class, not object_getClass: KVO observers (WKWebView, saucer) isa-swizzle the
            // window into a runtime subclass that must stay in place; [window class] sees through it.
            var cls = objc_msgSend_ret_nint((void*)nsWindowPtr, sel_registerName("class"));
            if (cls == 0 || OriginalSendEvent.ContainsKey(cls)) return;
            var sel = sel_registerName("sendEvent:");
            var method = class_getInstanceMethod(cls, sel); // hierarchy-searching: finds NSWindow's if not overridden
            if (method == 0) return;
            var original = method_getImplementation(method);
            if (original == 0) return;
            OriginalSendEvent[cls] = original;
            class_replaceMethod(cls, sel, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnSendEvent, "v@:@");
        }
    }

    // NSEventType: LeftMouseDown = 1, LeftMouseUp = 2. Observe-and-forward only — never swallows an event,
    // so the DOM always sees the full click stream (the page preventDefaults draggable mousedowns itself).
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnSendEvent(nint self, nint sel, nint nsEvent)
    {
        NativeGuard.Invoke("titlebar.sendEvent", () =>
        {
            var type = objc_msgSend_ret_nint((void*)nsEvent, sel_registerName("type"));
            if (type == 1)
            {
                objc_msgSend_ret_nint((void*)nsEvent, sel_registerName("retain"));
                if (Pending.TryRemove(self, out var stale))
                    objc_msgSend_ret_nint((void*)stale.Event, sel_registerName("release"));
                Pending[self] = new PendingMouseDown(nsEvent, Environment.TickCount64);
            }
            else if (type == 2 && Pending.TryRemove(self, out var p))
            {
                objc_msgSend_ret_nint((void*)p.Event, sel_registerName("release"));
            }
        });
        // Always forward to the original implementation, even if the guard tripped. Resolve it from the
        // declared class (KVO-transparent); walk up in case a subclass window inherited our swap.
        var cls = objc_msgSend_ret_nint((void*)self, sel_registerName("class"));
        while (cls != 0 && !OriginalSendEvent.TryGetValue(cls, out _)) cls = class_getSuperclass(cls);
        if (cls != 0 && OriginalSendEvent.TryGetValue(cls, out var original))
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)original)(self, sel, nsEvent);
    }

    /// <summary>
    /// Starts a native window drag from the retained mousedown event, if the page's verdict plausibly refers
    /// to it: the event is fresh (&lt; 1 s), the button is still down, and the verdict's page CSS-pixel point
    /// (scaled to points by <paramref name="scale"/>) matches the event location. A stale or mismatched
    /// verdict — e.g. one racing a newer click on an interactive control — is dropped.
    /// </summary>
    internal static unsafe void BeginDrag(nint nsWindowPtr, double cssX, double cssY, double scale)
    {
        if (nsWindowPtr == 0 || !Pending.TryRemove(nsWindowPtr, out var p)) return;
        try
        {
            if (Environment.TickCount64 - p.TimestampMs > 1000) return;
            var pressed = objc_msgSend_ret_nint((void*)objc_getClass("NSEvent"), sel_registerName("pressedMouseButtons"));
            if ((pressed & 1) == 0) return; // released before the verdict arrived — it's a click, not a drag

            var nsWindow = (void*)nsWindowPtr;
            var contentView = (void*)objc_msgSend_ret_nint(nsWindow, sel_registerName("contentView"));
            if (contentView == null) return;

            // Event location (window base coords) → content view coords → top-left origin, then compare
            // against the verdict's point. convertPoint:fromView: with nil converts from window coordinates
            // and is title-bar-inset-correct for every TitleBarStyle.
            var loc = objc_msgSend_ret_pt((void*)p.Event, sel_registerName("locationInWindow"));
            var inView = objc_msgSend_pt_ptr_ret_pt(contentView, sel_registerName("convertPoint:fromView:"), loc, null);
            var bounds = GetRect(contentView, sel_registerName("bounds"));
            var px = inView.X;
            var py = bounds.Height - inView.Y; // flip: AppKit bottom-left → CSS top-left
            const double Tolerance = 12.0;
            if (Math.Abs(px - cssX * scale) > Tolerance || Math.Abs(py - cssY * scale) > Tolerance) return;

            objc_msgSend_ptr(nsWindow, sel_registerName("performWindowDragWithEvent:"), (void*)p.Event);
        }
        finally
        {
            objc_msgSend_ret_nint((void*)p.Event, sel_registerName("release"));
        }
    }


    // Requested traffic-light top-left (window-top-left CSS points) per window, so the resize/key observer
    // can re-apply after AppKit re-lays the buttons out.
    private static readonly ConcurrentDictionary<nint, NSPoint> TrafficLight = new();
    private static nint _observerObject;
    private static bool _observerRegistered;

    /// <summary>
    /// Moves the traffic-light buttons so the close button's top-left sits at (x, y) measured from the
    /// window's top-left (points), preserving the buttons' spacing. Re-applied automatically on resize and
    /// when the window becomes key. Use to vertically center the lights in a taller custom title bar.
    /// </summary>
    internal static unsafe void SetTrafficLightPosition(nint nsWindowPtr, double x, double y)
    {
        if (nsWindowPtr == 0) return;
        TrafficLight[nsWindowPtr] = new NSPoint { X = x, Y = y };
        EnsureObserver(nsWindowPtr);
        ApplyTrafficLight(nsWindowPtr, x, y);
    }

    internal static void ClearTrafficLightPosition(nint nsWindowPtr)
    {
        if (nsWindowPtr != 0) TrafficLight.TryRemove(nsWindowPtr, out _);
    }

    private static unsafe void ApplyTrafficLight(nint nsWindowPtr, double x, double y)
    {
        var nsWindow = (void*)nsWindowPtr;
        var close = (void*)objc_msgSend_nint_ret_nint(nsWindow, sel_registerName("standardWindowButton:"), 0);
        var min = (void*)objc_msgSend_nint_ret_nint(nsWindow, sel_registerName("standardWindowButton:"), 1);
        var zoom = (void*)objc_msgSend_nint_ret_nint(nsWindow, sel_registerName("standardWindowButton:"), 2);
        if ((nint)close == 0) return;

        var superview = (void*)objc_msgSend_ret_nint(close, sel_registerName("superview"));
        if ((nint)superview == 0) return;
        var superH = GetRect(superview, sel_registerName("bounds")).Height;

        var closeFrame = GetRect(close, sel_registerName("frame"));
        // Convert the requested top-left y (from the window top) to the superview's bottom-left origin.
        var newX = x;
        var newY = superH - y - closeFrame.Height;
        var dx = newX - closeFrame.X;
        var dy = newY - closeFrame.Y;

        // Shift all three by the same delta so their native spacing is preserved.
        foreach (var button in new[] { close, min, zoom })
        {
            if ((nint)button == 0) continue;
            var f = GetRect(button, sel_registerName("frame"));
            objc_msgSend_setpt(button, sel_registerName("setFrameOrigin:"), new NSPoint { X = f.X + dx, Y = f.Y + dy });
        }
    }

    private static unsafe void EnsureObserver(nint nsWindowPtr)
    {
        if (!_observerRegistered)
        {
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RynTrafficLightObserver", 0);
            class_addMethod(cls, sel_registerName("onNotify:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTrafficLightNotify, "v@:@");
            objc_registerClassPair(cls);
            _observerObject = objc_msgSend_ret_nint((void*)objc_msgSend_ret_nint((void*)cls, sel_registerName("alloc")), sel_registerName("init"));
            _observerRegistered = true;
        }

        // Observe this window's resize and become-key: AppKit re-lays the buttons out on both.
        var center = (void*)objc_msgSend_ret_nint((void*)objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        foreach (var name in new[] { "NSWindowDidResizeNotification", "NSWindowDidBecomeKeyNotification" })
        {
            objc_msgSend_observe((void*)center, sel_registerName("addObserver:selector:name:object:"),
                (void*)_observerObject, sel_registerName("onNotify:"),
                (void*)CreateNSString(name), (void*)nsWindowPtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnTrafficLightNotify(nint self, nint sel, nint notification)
        => NativeGuard.Invoke("titlebar.trafficLight", () =>
        {
            var window = objc_msgSend_ret_nint((void*)notification, sel_registerName("object"));
            if (window != 0 && TrafficLight.TryGetValue(window, out var pos))
                ApplyTrafficLight(window, pos.X, pos.Y);
        });

    private static unsafe nint CreateNSString(string s) =>
        objc_msgSend_strarg_ret_nint((void*)objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), s);

    internal static unsafe (double Left, double Top) GetTrafficLightInsets(nint nsWindowPtr)
    {
        if (nsWindowPtr == 0) return (0, 0);

        var nsWindow = (void*)nsWindowPtr;

        // NSWindowButton: Close=0, Miniaturize=1, Zoom=2
        // Get the zoom (rightmost) button to find the right edge
        var zoomButton = (void*)objc_msgSend_nint_ret_nint(nsWindow, sel_registerName("standardWindowButton:"), 2);
        if ((nint)zoomButton == 0) return (70, 28);

        var buttonFrame = GetRect(zoomButton, sel_registerName("frame"));

        // The superview (title bar view) has the buttons positioned relative to it
        var superview = (void*)objc_msgSend_ret_nint(zoomButton, sel_registerName("superview"));
        if ((nint)superview == 0) return (70, 28);

        var superFrame = GetRect(superview, sel_registerName("frame"));

        // Right edge of zoom button + padding
        var left = buttonFrame.X + buttonFrame.Width + 12;
        var top = superFrame.Height;

        return (left, top);
    }

    // --- ObjC Runtime P/Invoke ---

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool class_addMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint class_getSuperclass(nint cls);

    // class_getInstanceMethod searches the whole hierarchy, so it resolves the effective sendEvent: even
    // when the window's own class doesn't override it (NSWindow's implementation is returned).
    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint class_getInstanceMethod(nint cls, nint sel);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint method_getImplementation(nint method);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint class_replaceMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    // NSPoint (16 bytes, 2 doubles) returns in registers on BOTH ABIs — arm64 in v0/v1, x86_64 System V in
    // xmm0/xmm1 (two SSE-class eightbytes) — so the ordinary objc_msgSend trampoline is correct everywhere;
    // only the 32-byte NSRect needs the _stret split below.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial NSPoint objc_msgSend_ret_pt(void* receiver, nint selector);

    // convertPoint:fromView: — (NSPoint, NSView*) in, NSPoint out; all register-class on both ABIs.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial NSPoint objc_msgSend_pt_ptr_ret_pt(void* receiver, nint selector, NSPoint point, void* view);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_bool(void* receiver, nint selector, byte value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_nint(void* receiver, nint selector, nint value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_ptr(void* receiver, nint selector, void* value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ret_nint(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_nint_ret_nint(void* receiver, nint selector, nint arg);

    // setFrameOrigin: — NSPoint (2 doubles) by value; arm64 passes it in FP registers via the ordinary trampoline.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_setpt(void* receiver, nint selector, NSPoint point);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_strarg_ret_nint(void* receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    // addObserver:selector:name:object: — (id observer, SEL selector, NSString* name, id object).
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_observe(void* receiver, nint selector,
        void* observer, nint selectorArg, void* name, void* obj);

    // NSRect is 32 bytes (4 doubles), so its return ABI differs by architecture:
    //  * arm64  — the Apple AAPCS64 returns the struct in floating-point registers (d0..d3) via the
    //             ordinary objc_msgSend trampoline. There is NO objc_msgSend_stret on arm64; calling it
    //             would crash. This is the only RID Ryn currently ships, and it is already correct.
    //  * x86_64 — the System V ABI classifies a >16-byte aggregate as MEMORY, so it is returned through a
    //             hidden pointer (sret) supplied by the caller. objc_msgSend assumes a register return and
    //             reads garbage; the selector dispatch must instead use objc_msgSend_stret, which takes the
    //             out-struct pointer as its first argument. Routed via GetRect below by OSArchitecture.
    // Selecting the wrong trampoline reads uninitialized/garbage rectangles (drag-view sizing and traffic-
    // light insets), which is the defect behind PAP-10 / INT-09 on Intel Macs.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial NSRect objc_msgSend_rect_arm64(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend_stret")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_stret_x64(NSRect* outRect, void* receiver, nint selector);

    /// <summary>
    /// Sends a zero-argument, <see cref="NSRect"/>-returning Objective-C message, selecting the calling
    /// convention for the running architecture. arm64 uses the ordinary <c>objc_msgSend</c> register-return
    /// trampoline (no <c>_stret</c> variant exists); x86_64 uses <c>objc_msgSend_stret</c> with the hidden
    /// struct-return pointer demanded by the System V "MEMORY" classification of a 32-byte aggregate.
    /// </summary>
    private static unsafe NSRect GetRect(void* receiver, nint selector)
    {
        // RuntimeInformation.OSArchitecture is the OS/runtime architecture; under Rosetta 2 a process runs
        // as X64 and must use the x86_64 ABI, which this branch does correctly. Anything other than X64
        // (Arm64, and any future arch) takes the register-return path, matching the shipped arm64 target.
        if (RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            NSRect rect;
            objc_msgSend_stret_x64(&rect, receiver, selector);
            return rect;
        }

        return objc_msgSend_rect_arm64(receiver, selector);
    }

    // initWithFrame: takes the NSRect by value and returns a pointer (id). The by-value 32-byte argument is
    // marshalled by P/Invoke per the platform convention (registers on arm64; the System V "MEMORY" class,
    // i.e. passed on the stack, on x86_64), and the pointer return is INTEGER-class in both ABIs — so the
    // ordinary objc_msgSend trampoline is correct on every architecture here. Only struct *returns* need the
    // _stret split above; a struct *argument* with a scalar return does not.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_rect_ret_nint(void* receiver, nint selector, NSRect frame);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }
}
