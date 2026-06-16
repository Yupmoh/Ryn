using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ryn.Core;
using Ryn.Core.Internal;

namespace Ryn.Plugins.Tray.Backends;

// CA2216 (declare a finalizer): intentionally omitted. The native objects this owns (NSStatusItem/NSMenu/
// the ObjC handler) may only be released on the AppKit main thread, and the GCHandle is freed in lockstep
// with that release. A finalizer would run on the GC finalizer thread and touch AppKit off the main thread
// (undefined behavior) — so cleanup is driven solely through Dispose, which marshals onto the main thread
// (PAP-02). The DI singleton is disposed deterministically at app shutdown, so Dispose is not skipped.
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer",
    Justification = "Native teardown must run on the AppKit main thread; a finalizer cannot. See Dispose.")]
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsTrayBackend : ITrayBackend
{
    private readonly IMainThreadDispatcher _mainThread;

    private nint _statusItem;
    private nint _menu;
    private nint _handlerObject;
    private bool _disposed;

    // Pins this backend so the native ObjC handler object can recover the managed instance from its ivar
    // (see EnsureHandlerClass / the click callbacks). Allocated on Show, freed on Dispose. Replaces the old
    // static _instance field so two TrayService instances cannot steal each other's callbacks (PAP-02).
    private GCHandle _selfHandle;

    private readonly List<TrayMenuItem> _menuItems = [];
    private readonly object _lock = new();

    private static nint _handlerClass;
    private static bool _classRegistered;

    // Name of the ivar added to the dynamically-built RynTrayHandler class that stores the GCHandle pointer
    // back to the owning MacOsTrayBackend. Must be a stable C string for object_get/setInstanceVariable.
    private const string BackendIvarName = "rynBackend";

    public event Action? IconClicked;
    public event Action<string>? MenuItemClicked;

    public MacOsTrayBackend(IMainThreadDispatcher mainThread)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _mainThread = mainThread;
    }

    public void Show(string? iconPath, string tooltip)
    {
        if (_statusItem != 0) return;

        // Creating an NSStatusItem touches NSStatusBar/NSStatusItem (AppKit) which must run on the main
        // thread. Use InvokeAsync so the status item exists before any later Hide/SetTooltip/SetMenu runs,
        // preserving the ordering the old synchronous code relied on.
        _ = _mainThread.InvokeAsync(() => ShowOnUi(iconPath, tooltip));
    }

    private unsafe void ShowOnUi(string? iconPath, string tooltip)
    {
        if (_statusItem != 0 || _disposed) return;

        EnsureHandlerClass();

        // Root this instance for the lifetime of the native handler so the click callbacks can resolve it
        // from the ivar. Freed in DisposeOnUi.
        if (!_selfHandle.IsAllocated)
            _selfHandle = GCHandle.Alloc(this);

        var pool = objc_autoreleasePoolPush();
        try
        {
            var handlerAlloc = objc_msgSend_ret_nint((void*)_handlerClass, sel_registerName("alloc"));
            _handlerObject = objc_msgSend_ret_nint((void*)handlerAlloc, sel_registerName("init"));

            // Stash the GCHandle pointer in the handler's ivar so OnStatusItemClicked/OnMenuItemClicked can
            // recover *this* backend (per-instance) instead of a process-wide static.
            object_setInstanceVariable(
                (void*)_handlerObject, BackendIvarName, (void*)GCHandle.ToIntPtr(_selfHandle));

            var statusBar = (void*)objc_msgSend_ret_nint(
                (void*)objc_getClass("NSStatusBar"),
                sel_registerName("systemStatusBar"));

            _statusItem = objc_msgSend_double_ret_nint(
                statusBar, sel_registerName("statusItemWithLength:"), -1.0);

            // NSStatusItem is owned by the system status bar (removeStatusItem: releases it); retain it so our
            // field stays valid until Hide/Dispose explicitly removes it.
            objc_msgSend_ret_nint((void*)_statusItem, sel_registerName("retain"));

            var button = (void*)objc_msgSend_ret_nint(
                (void*)_statusItem, sel_registerName("button"));

            if (iconPath is not null && File.Exists(iconPath))
            {
                var pathStr = CreateNSString(iconPath);
                var imgAlloc = objc_msgSend_ret_nint(
                    (void*)objc_getClass("NSImage"), sel_registerName("alloc"));
                var image = objc_msgSend_ptr_ret_nint(
                    (void*)imgAlloc, sel_registerName("initWithContentsOfFile:"), (void*)pathStr);
                if (image != 0)
                {
                    objc_msgSend_bool((void*)image, sel_registerName("setTemplate:"), 1);
                    objc_msgSend_ptr(button, sel_registerName("setImage:"), (void*)image);
                    // initWithContentsOfFile: returns a +1 owned image; setImage: retains it, so release ours.
                    objc_msgSend_ret_nint((void*)image, sel_registerName("release"));
                }
            }
            else
            {
                var title = CreateNSString("●");
                objc_msgSend_ptr(button, sel_registerName("setTitle:"), (void*)title);
            }

            var tooltipStr = CreateNSString(tooltip);
            objc_msgSend_ptr(button, sel_registerName("setToolTip:"), (void*)tooltipStr);

            objc_msgSend_ptr(button, sel_registerName("setTarget:"), (void*)_handlerObject);
            objc_msgSend_nint(button, sel_registerName("setAction:"),
                sel_registerName("statusItemClicked:"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void Hide()
    {
        if (_statusItem == 0) return;
        _ = _mainThread.InvokeAsync(HideOnUi);
    }

    private unsafe void HideOnUi()
    {
        if (_statusItem == 0) return;

        var statusBar = (void*)objc_msgSend_ret_nint(
            (void*)objc_getClass("NSStatusBar"),
            sel_registerName("systemStatusBar"));
        objc_msgSend_ptr(statusBar, sel_registerName("removeStatusItem:"), (void*)_statusItem);
        // Balance the retain taken in ShowOnUi (removeStatusItem: drops the status bar's own reference).
        objc_msgSend_ret_nint((void*)_statusItem, sel_registerName("release"));
        _statusItem = 0;
    }

    public void SetTooltip(string tooltip)
    {
        if (_statusItem == 0) return;
        _mainThread.Post(() => SetTooltipOnUi(tooltip));
    }

    private unsafe void SetTooltipOnUi(string tooltip)
    {
        if (_statusItem == 0) return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var button = (void*)objc_msgSend_ret_nint(
                (void*)_statusItem, sel_registerName("button"));
            var tooltipStr = CreateNSString(tooltip);
            objc_msgSend_ptr(button, sel_registerName("setToolTip:"), (void*)tooltipStr);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        lock (_lock)
        {
            _menuItems.Clear();
            _menuItems.AddRange(items);
        }
        // Rebuilding the NSMenu/NSMenuItem tree is AppKit work; marshal it onto the main thread. Post (not
        // InvokeAsync) because SetMenu is fire-and-forget for callers; the snapshot above is already taken.
        _mainThread.Post(RebuildMenu);
    }

    public void ShowNotification(string title, string message)
    {
        var escapedTitle = title
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var escapedMsg = message
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var script = $"display notification \"{escapedMsg}\" with title \"{escapedTitle}\"";
        try
        {
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            Process.Start(psi)?.WaitForExit();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private unsafe void RebuildMenu()
    {
        if (_statusItem == 0) return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            if (_menu != 0)
                objc_msgSend_ret_nint((void*)_menu, sel_registerName("release"));

            var menuAlloc = objc_msgSend_ret_nint(
                (void*)objc_getClass("NSMenu"), sel_registerName("alloc"));
            _menu = objc_msgSend_ret_nint((void*)menuAlloc, sel_registerName("init"));

            objc_msgSend_bool((void*)_menu, sel_registerName("setAutoenablesItems:"), 0);

            lock (_lock)
            {
                for (var i = 0; i < _menuItems.Count; i++)
                {
                    var item = _menuItems[i];

                    if (item.Separator)
                    {
                        var separator = (void*)objc_msgSend_ret_nint(
                            (void*)objc_getClass("NSMenuItem"),
                            sel_registerName("separatorItem"));
                        objc_msgSend_ptr((void*)_menu, sel_registerName("addItem:"), separator);
                    }
                    else
                    {
                        var titleStr = CreateNSString(item.Label);
                        var keyStr = CreateNSString("");
                        var action = sel_registerName("menuItemClicked:");

                        var miAlloc = objc_msgSend_ret_nint(
                            (void*)objc_getClass("NSMenuItem"), sel_registerName("alloc"));
                        var menuItem = (void*)objc_msgSend_3nint_ret_nint(
                            (void*)miAlloc,
                            sel_registerName("initWithTitle:action:keyEquivalent:"),
                            titleStr, action, keyStr);

                        objc_msgSend_ptr(menuItem, sel_registerName("setTarget:"),
                            (void*)_handlerObject);
                        objc_msgSend_nint(menuItem, sel_registerName("setTag:"), i);

                        if (!item.Enabled)
                            objc_msgSend_bool(menuItem, sel_registerName("setEnabled:"), 0);

                        objc_msgSend_ptr((void*)_menu, sel_registerName("addItem:"), menuItem);

                        // INT-07: alloc/initWithTitle: returns a +1 owned NSMenuItem and does NOT sit in the
                        // surrounding autorelease pool. addItem: retains it (the menu now owns it), so release
                        // our +1 here; without this every SetMenu leaks one retain per non-separator item.
                        objc_msgSend_ret_nint(menuItem, sel_registerName("release"));
                    }
                }
            }

            objc_msgSend_ptr((void*)_menu, sel_registerName("setDelegate:"),
                (void*)_handlerObject);
            objc_msgSend_ptr((void*)_statusItem, sel_registerName("setMenu:"), (void*)_menu);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static unsafe void EnsureHandlerClass()
    {
        if (_classRegistered) return;

        var superclass = objc_getClass("NSObject");
        _handlerClass = objc_allocateClassPair(superclass, "RynTrayHandler", 0);

        // Per-instance pointer-sized ivar holding GCHandle.ToIntPtr(...) of the owning backend (PAP-02).
        // "^v" is the ObjC type encoding for void*; size/alignment are pointer-sized.
        class_addIvar(
            _handlerClass, BackendIvarName, (nuint)sizeof(nint), (byte)nint.Log2(sizeof(nint)), "^v");

        class_addMethod(
            _handlerClass,
            sel_registerName("statusItemClicked:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnStatusItemClicked,
            "v@:@");

        class_addMethod(
            _handlerClass,
            sel_registerName("menuItemClicked:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuItemClicked,
            "v@:@");

        class_addMethod(
            _handlerClass,
            sel_registerName("menuWillOpen:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuWillOpen,
            "v@:@");

        objc_registerClassPair(_handlerClass);
        _classRegistered = true;
    }

    // Recover the owning backend from the handler object's ivar. Returns null if the handler is gone or the
    // GCHandle has been freed (Dispose raced the callback).
    private static unsafe MacOsTrayBackend? ResolveBackend(nint self)
    {
        if (self == 0) return null;
        object_getInstanceVariable((void*)self, BackendIvarName, out var raw);
        if (raw == 0) return null;
        var handle = GCHandle.FromIntPtr(raw);
        return handle.IsAllocated ? handle.Target as MacOsTrayBackend : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnStatusItemClicked(nint self, nint sel, nint sender)
        => NativeGuard.Invoke("MacOsTrayBackend.OnStatusItemClicked",
            () => ResolveBackend(self)?.IconClicked?.Invoke());

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMenuItemClicked(nint self, nint sel, nint sender)
        => NativeGuard.Invoke("MacOsTrayBackend.OnMenuItemClicked", () =>
        {
            var instance = ResolveBackend(self);
            if (instance is null) return;

            var tag = (int)objc_msgSend_ret_nint((void*)sender, sel_registerName("tag"));
            string? itemId;
            lock (instance._lock)
            {
                if (tag >= 0 && tag < instance._menuItems.Count)
                    itemId = instance._menuItems[tag].Id;
                else
                    return;
            }
            instance.MenuItemClicked?.Invoke(itemId);
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnMenuWillOpen(nint self, nint sel, nint menu)
        => NativeGuard.Invoke("MacOsTrayBackend.OnMenuWillOpen",
            () => ResolveBackend(self)?.IconClicked?.Invoke());

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Native teardown must run on the main thread. Block until it completes so the GCHandle is freed only
        // after the native handler can no longer fire a callback into it. The singleton is disposed during
        // app shutdown when the UI loop is still draining, so this completes promptly; if the loop is already
        // gone the dispatcher drops the work and we free the handle below.
        _mainThread.InvokeAsync(DisposeOnUi).GetAwaiter().GetResult();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    private unsafe void DisposeOnUi()
    {
        HideOnUi();

        if (_menu != 0)
        {
            objc_msgSend_ret_nint((void*)_menu, sel_registerName("release"));
            _menu = 0;
        }

        if (_handlerObject != 0)
        {
            // Clear the ivar first so a late callback resolves to null rather than a freed handle.
            object_setInstanceVariable((void*)_handlerObject, BackendIvarName, null);
            objc_msgSend_ret_nint((void*)_handlerObject, sel_registerName("release"));
            _handlerObject = 0;
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
    private static partial nint objc_allocateClassPair(
        nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool class_addMethod(
        nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool class_addIvar(
        nint cls, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint size, byte alignment,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint object_setInstanceVariable(
        void* obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, void* value);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint object_getInstanceVariable(
        void* obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out nint outValue);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_bool(void* receiver, nint selector, byte value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_nint(void* receiver, nint selector, nint value);

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

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_double_ret_nint(
        void* receiver, nint selector, double value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_3nint_ret_nint(
        void* receiver, nint selector, nint arg1, nint arg2, nint arg3);
}
