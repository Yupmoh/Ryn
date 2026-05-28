using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

[SupportedOSPlatform("macos")]
internal static partial class MacOsTitleBar
{
    private static nint _dragViewClass;
    private static bool _classRegistered;

    internal static unsafe void Apply(nint nsWindowPtr, bool overlay)
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

            // Native drag view on top of webview in the title bar region
            AddDragView(nsWindow);
        }
        // Hidden: no fullSizeContentView — title bar stays as a separate native strip
        // with drag and traffic lights. Content renders below it.
    }

    private static unsafe void AddDragView(void* nsWindow)
    {
        EnsureDragViewClass();

        // Get the window's content view frame to determine width
        var contentView = (void*)objc_msgSend_ret_nint(nsWindow, sel_registerName("contentView"));
        var frame = objc_msgSend_rect(contentView, sel_registerName("frame"));

        // Position: right of traffic lights (x=70), top of window, full remaining width, 28px tall
        var dragFrame = new NSRect { X = 70, Y = frame.Height - 28, Width = frame.Width - 70, Height = 28 };

        // Create the drag view
        var alloc = objc_msgSend_ret_nint((void*)_dragViewClass, sel_registerName("alloc"));
        var dragView = (void*)objc_msgSend_rect_ret_nint((void*)alloc, sel_registerName("initWithFrame:"), dragFrame);

        // Autoresizing: stick to top, flex width
        // NSViewWidthSizable = 2, NSViewMinYMargin = 8
        objc_msgSend_nint(dragView, sel_registerName("setAutoresizingMask:"), 2 | 8);

        // Add on top of the webview
        objc_msgSend_ptr(contentView, sel_registerName("addSubview:"), dragView);
    }

    private static unsafe void EnsureDragViewClass()
    {
        if (_classRegistered) return;

        var superclass = objc_getClass("NSView");
        _dragViewClass = objc_allocateClassPair(superclass, "RynTitleBarDragView", 0);

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

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMouseDown(nint self, nint sel, nint nsEvent)
    {
        var window = objc_msgSend_ret_nint((void*)self, sel_registerName("window"));
        objc_msgSend_ptr((void*)window, sel_registerName("performWindowDragWithEvent:"), (void*)nsEvent);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMouseUp(nint self, nint sel, nint nsEvent)
    {
        // Double-click title bar = zoom (maximize/restore)
        var clickCount = objc_msgSend_ret_nint((void*)nsEvent, sel_registerName("clickCount"));
        if (clickCount == 2)
        {
            var window = objc_msgSend_ret_nint((void*)self, sel_registerName("window"));
            objc_msgSend_ptr((void*)window, sel_registerName("zoom:"), null);
        }
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
    private static unsafe partial NSRect objc_msgSend_rect(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_rect_ret_nint(void* receiver, nint selector, NSRect frame);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }
}
