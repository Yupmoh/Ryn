using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

[SupportedOSPlatform("macos")]
internal static partial class MacOsTitleBar
{
    private static nint _dragViewClass;
    private static bool _classRegistered;

    // Drag/ignore regions published by the page (the data-webview-* script), keyed by NSWindow* and stored as
    // viewport-top-left CSS-pixel rects [x,y,w,h,...]. The drag view's hitTest consults them: a point inside an
    // ignore rect (an interactive control) or outside every drag rect falls through to the webview; a point
    // inside a drag rect (and not an ignore rect) is a native window drag. Absent → nothing draggable there.
    private sealed record Regions(double[] Drag, double[] Ignore);
    private static readonly ConcurrentDictionary<nint, Regions> DragRegions = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    internal static unsafe void Apply(nint nsWindowPtr, bool overlay) => Apply(nsWindowPtr, overlay, dragView: true);

    internal static unsafe void Apply(nint nsWindowPtr, bool overlay, bool dragView)
    {
        if (nsWindowPtr == 0) return;

        var nsWindow = (void*)nsWindowPtr;

        // Hide title text, make title bar transparent
        objc_msgSend_nint(nsWindow, sel_registerName("setTitleVisibility:"), 1);
        objc_msgSend_bool(nsWindow, sel_registerName("setTitlebarAppearsTransparent:"), 1);

        if (overlay)
        {
            // Overlay: content extends under title bar
            var mask = objc_msgSend_ret_nint(nsWindow, sel_registerName("styleMask"));
            objc_msgSend_nint(nsWindow, sel_registerName("setStyleMask:"), mask | (1 << 15));

            // Native drag view over the webview. It only grabs points inside published drag regions (hitTest);
            // every other point falls through to the DOM. Skipped when the app opts out to self-manage drag.
            if (dragView) AddDragView(nsWindow);
        }
        // Hidden: no fullSizeContentView — title bar stays as a separate native strip
        // with drag and traffic lights. Content renders below it.
    }

    /// <summary>
    /// Publishes the page's draggable and ignored rectangles (viewport-top-left CSS pixels, flat [x,y,w,h,...])
    /// for a window. The overlay drag view's hitTest uses them to decide drag-vs-forward-to-webview.
    /// </summary>
    internal static void SetDragRegions(nint nsWindowPtr, double[] drag, double[] ignore)
    {
        if (nsWindowPtr == 0) return;
        DragRegions[nsWindowPtr] = new Regions(drag ?? [], ignore ?? []);
    }

    internal static void ClearDragRegions(nint nsWindowPtr)
    {
        if (nsWindowPtr != 0) DragRegions.TryRemove(nsWindowPtr, out _);
    }

    private static unsafe void AddDragView(void* nsWindow)
    {
        EnsureDragViewClass();

        var contentView = (void*)objc_msgSend_ret_nint(nsWindow, sel_registerName("contentView"));
        var frame = GetRect(contentView, sel_registerName("frame"));

        // Full-content-size overlay: hitTest returns self only inside a published drag region, nil elsewhere,
        // so the app's drag regions can be any height/position and all other clicks reach the DOM.
        var dragFrame = new NSRect { X = 0, Y = 0, Width = frame.Width, Height = frame.Height };

        var alloc = objc_msgSend_ret_nint((void*)_dragViewClass, sel_registerName("alloc"));
        var dragView = (void*)objc_msgSend_rect_ret_nint((void*)alloc, sel_registerName("initWithFrame:"), dragFrame);

        // NSViewWidthSizable(2) | NSViewHeightSizable(16): track the content view on resize.
        objc_msgSend_nint(dragView, sel_registerName("setAutoresizingMask:"), 2 | 16);

        // Add on top of the webview.
        objc_msgSend_ptr(contentView, sel_registerName("addSubview:"), dragView);
    }

    private static unsafe void EnsureDragViewClass()
    {
        if (_classRegistered) return;

        var superclass = objc_getClass("NSView");
        _dragViewClass = objc_allocateClassPair(superclass, "RynTitleBarDragView", 0);

        // hitTest: — grab only points inside a published drag region; return nil elsewhere so the event
        // falls through to the webview (a sibling added before this view).
        class_addMethod(
            _dragViewClass,
            sel_registerName("hitTest:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, NSPoint, nint>)&OnHitTest,
            "@@:{CGPoint=dd}");

        // Override mouseDown: to perform window drag
        class_addMethod(
            _dragViewClass,
            sel_registerName("mouseDown:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMouseDown,
            "v@:@");

        // Override mouseUp: to handle double-click maximize
        class_addMethod(
            _dragViewClass,
            sel_registerName("mouseUp:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMouseUp,
            "v@:@");

        objc_registerClassPair(_dragViewClass);
        _classRegistered = true;
    }

    // aPoint is in the superview (contentView) coordinate system: bottom-left origin, points. Convert to the
    // page's top-left CSS pixels (1:1 with points for WKWebView) and test against the published regions.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe nint OnHitTest(nint self, nint sel, NSPoint aPoint)
        => NativeGuard.Invoke("titlebar.hitTest", (nint)0, () =>
        {
            var window = objc_msgSend_ret_nint((void*)self, sel_registerName("window"));
            if (window == 0 || !DragRegions.TryGetValue(window, out var regions) || regions.Drag.Length < 4)
                return 0; // nothing draggable → fall through to the webview

            var superview = (void*)objc_msgSend_ret_nint((void*)self, sel_registerName("superview"));
            if (superview == null) return 0;
            var bounds = GetRect(superview, sel_registerName("bounds"));

            var cssX = aPoint.X;
            var cssY = bounds.Height - aPoint.Y; // flip: AppKit bottom-left → CSS top-left

            // Draggable only when the point is inside a drag region and not inside an interactive (ignore)
            // rect. Otherwise return nil so the click falls through to the webview.
            return TitleBarRegions.IsDraggable(regions.Drag, regions.Ignore, cssX, cssY) ? self : 0;
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMouseDown(nint self, nint sel, nint nsEvent)
        => NativeGuard.Invoke("titlebar.mouseDown", () =>
        {
            var window = objc_msgSend_ret_nint((void*)self, sel_registerName("window"));
            objc_msgSend_ptr((void*)window, sel_registerName("performWindowDragWithEvent:"), (void*)nsEvent);
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMouseUp(nint self, nint sel, nint nsEvent)
        => NativeGuard.Invoke("titlebar.mouseUp", () =>
        {
            // Double-click title bar = zoom (maximize/restore)
            var clickCount = objc_msgSend_ret_nint((void*)nsEvent, sel_registerName("clickCount"));
            if (clickCount == 2)
            {
                var window = objc_msgSend_ret_nint((void*)self, sel_registerName("window"));
                objc_msgSend_ptr((void*)window, sel_registerName("zoom:"), null);
            }
        });

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
