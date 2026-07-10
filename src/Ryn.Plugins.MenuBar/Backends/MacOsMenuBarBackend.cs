using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ryn.Core;
using Ryn.Core.Internal;

namespace Ryn.Plugins.MenuBar.Backends;

// CA2216 (declare a finalizer): intentionally omitted, same reasoning as MacOsTrayBackend — the native
// objects this owns (NSMenu tree / the ObjC handler) may only be released on the AppKit main thread, and the
// GCHandle is freed in lockstep with that release. Cleanup is driven solely through Dispose, which marshals
// onto the main thread; the DI singleton is disposed deterministically at app shutdown.
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer",
    Justification = "Native teardown must run on the AppKit main thread; a finalizer cannot. See Dispose.")]
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsMenuBarBackend : IMenuBarBackend
{
    // NSEventModifierFlag values (NSEvent.h).
    private const nint ModifierShift = 1 << 17;
    private const nint ModifierControl = 1 << 18;
    private const nint ModifierOption = 1 << 19;
    private const nint ModifierCommand = 1 << 20;

    private readonly IMainThreadDispatcher _mainThread;
    private readonly string _appName;

    private nint _handlerObject;
    private bool _disposed;

    // Pins this backend so the native ObjC handler object can recover the managed instance from its ivar
    // (see EnsureHandlerClass / OnMenuItemClicked). Allocated on first SetMenu, freed on Dispose.
    private GCHandle _selfHandle;

    // Snapshot of the menu to build, and the ids of custom items in the order their NSMenuItem tags were
    // assigned during the last rebuild. Both guarded by _lock; tags index into _customIds.
    private readonly List<MenuBarItem> _menuItems = [];
    private readonly List<string> _customIds = [];
    private readonly object _lock = new();

    private static nint _handlerClass;
    private static bool _classRegistered;

    // Name of the ivar added to the dynamically-built RynMenuBarHandler class that stores the GCHandle
    // pointer back to the owning backend. Must be a stable C string for object_get/setInstanceVariable.
    private const string BackendIvarName = "rynBackend";

    public event Action<string>? MenuItemClicked;

    // Roles dispatch natively through the responder chain on macOS, so this backend never raises it.
#pragma warning disable CS0067
    public event Action<string>? RoleActivated;
#pragma warning restore CS0067

    public MacOsMenuBarBackend(IMainThreadDispatcher mainThread, string appName)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _mainThread = mainThread;
        _appName = appName;
    }

    public void SetMenu(IReadOnlyList<MenuBarItem> items)
    {
        lock (_lock)
        {
            _menuItems.Clear();
            _menuItems.AddRange(items);
        }
        // Building the NSMenu tree is AppKit work; marshal it onto the main thread. Post (not InvokeAsync)
        // because SetMenu is fire-and-forget for callers; the snapshot above is already taken. Work posted
        // before the event loop starts (the default menu applied during plugin init) is buffered by the host
        // and drained once the loop runs.
        _mainThread.Post(RebuildMenu);
    }

    private unsafe void RebuildMenu()
    {
        if (_disposed) return;

        EnsureHandlerClass();

        if (!_selfHandle.IsAllocated)
            _selfHandle = GCHandle.Alloc(this);

        var pool = objc_autoreleasePoolPush();
        try
        {
            if (_handlerObject == 0)
            {
                var handlerAlloc = objc_msgSend_ret_nint((void*)_handlerClass, sel_registerName("alloc"));
                _handlerObject = objc_msgSend_ret_nint((void*)handlerAlloc, sel_registerName("init"));
                object_setInstanceVariable(
                    (void*)_handlerObject, BackendIvarName, (void*)GCHandle.ToIntPtr(_selfHandle));
            }

            nint mainMenu;
            lock (_lock)
            {
                _customIds.Clear();

                var menuAlloc = objc_msgSend_ret_nint(
                    (void*)objc_getClass("NSMenu"), sel_registerName("alloc"));
                mainMenu = objc_msgSend_ret_nint((void*)menuAlloc, sel_registerName("init"));

                foreach (var topItem in _menuItems)
                {
                    if (topItem.Separator) continue; // separators are meaningless at the top level

                    var title = topItem.Label ?? topItem.Id ?? string.Empty;
                    var holderAlloc = objc_msgSend_ret_nint(
                        (void*)objc_getClass("NSMenuItem"), sel_registerName("alloc"));
                    var holder = (void*)objc_msgSend_3nint_ret_nint(
                        (void*)holderAlloc,
                        sel_registerName("initWithTitle:action:keyEquivalent:"),
                        CreateNSString(title), 0, CreateNSString(""));

                    var submenu = BuildMenu(title, topItem.Items ?? []);
                    objc_msgSend_ptr(holder, sel_registerName("setSubmenu:"), (void*)submenu);
                    // setSubmenu: retains; balance our alloc/init +1.
                    objc_msgSend_ret_nint((void*)submenu, sel_registerName("release"));

                    objc_msgSend_ptr((void*)mainMenu, sel_registerName("addItem:"), holder);
                    // addItem: retains (the menu owns it); balance our alloc/init +1.
                    objc_msgSend_ret_nint(holder, sel_registerName("release"));
                }
            }

            var nsApp = (void*)objc_msgSend_ret_nint(
                (void*)objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            objc_msgSend_ptr(nsApp, sel_registerName("setMainMenu:"), (void*)mainMenu);
            // setMainMenu: retains; balance our alloc/init +1.
            objc_msgSend_ret_nint((void*)mainMenu, sel_registerName("release"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    // Builds one NSMenu (returned at +1) from a list of items, recursing into submenus. Caller must be on
    // the main thread inside an autorelease pool and already hold _lock (custom-item tags are assigned here).
    private unsafe nint BuildMenu(string title, IReadOnlyList<MenuBarItem> items)
    {
        var menuAlloc = objc_msgSend_ret_nint((void*)objc_getClass("NSMenu"), sel_registerName("alloc"));
        var menu = objc_msgSend_ptr_ret_nint(
            (void*)menuAlloc, sel_registerName("initWithTitle:"), (void*)CreateNSString(title));

        // Explicit enablement: without this, items with a managed target would be auto-disabled whenever
        // AppKit can't validate them. Role items (nil target) stay always-enabled as a consequence.
        objc_msgSend_bool((void*)menu, sel_registerName("setAutoenablesItems:"), 0);

        foreach (var item in items)
        {
            if (item.Separator)
            {
                var separator = (void*)objc_msgSend_ret_nint(
                    (void*)objc_getClass("NSMenuItem"), sel_registerName("separatorItem"));
                objc_msgSend_ptr((void*)menu, sel_registerName("addItem:"), separator);
                continue;
            }

            // A submenu only when it actually has children; an empty (non-null) Items — a JSON `items: []`
            // leaf — must stay a clickable custom item, not a dead targetless submenu. See MenuBarItem.IsSubmenu.
            if (item.IsSubmenu)
            {
                var holderAlloc = objc_msgSend_ret_nint(
                    (void*)objc_getClass("NSMenuItem"), sel_registerName("alloc"));
                var holder = (void*)objc_msgSend_3nint_ret_nint(
                    (void*)holderAlloc,
                    sel_registerName("initWithTitle:action:keyEquivalent:"),
                    CreateNSString(item.Label ?? item.Id ?? string.Empty), 0, CreateNSString(""));

                var submenu = BuildMenu(item.Label ?? string.Empty, item.Items!);
                objc_msgSend_ptr(holder, sel_registerName("setSubmenu:"), (void*)submenu);
                objc_msgSend_ret_nint((void*)submenu, sel_registerName("release"));

                objc_msgSend_ptr((void*)menu, sel_registerName("addItem:"), holder);
                objc_msgSend_ret_nint(holder, sel_registerName("release"));
                continue;
            }

            string label;
            nint action;
            var resolvedRole = item.Role is not null && MenuBarRoles.TryGet(item.Role, out var role)
                ? role
                : null;
            if (resolvedRole is not null)
            {
                label = MenuBarRoles.ResolveLabel(resolvedRole, item.Label, _appName);
                action = sel_registerName(resolvedRole.MacSelector);
            }
            else
            {
                label = item.Label ?? item.Id ?? string.Empty;
                action = sel_registerName("menuItemClicked:");
            }

            var (keyEquivalent, modifierMask) = ResolveKeyEquivalent(item.Accelerator ?? resolvedRole?.DefaultAccelerator);

            var miAlloc = objc_msgSend_ret_nint(
                (void*)objc_getClass("NSMenuItem"), sel_registerName("alloc"));
            var menuItem = (void*)objc_msgSend_3nint_ret_nint(
                (void*)miAlloc,
                sel_registerName("initWithTitle:action:keyEquivalent:"),
                CreateNSString(label), action, CreateNSString(keyEquivalent));

            if (modifierMask != 0)
                objc_msgSend_nint(menuItem, sel_registerName("setKeyEquivalentModifierMask:"), modifierMask);

            if (resolvedRole is not null)
            {
                // Nil target: the action dispatches through the responder chain, which is what makes copy/
                // paste/etc. reach native fields and the webview alike. initWithTitle: leaves target nil.
            }
            else
            {
                objc_msgSend_ptr(menuItem, sel_registerName("setTarget:"), (void*)_handlerObject);
                objc_msgSend_nint(menuItem, sel_registerName("setTag:"), _customIds.Count);
                _customIds.Add(item.Id ?? label);
            }

            if (!item.Enabled)
                objc_msgSend_bool(menuItem, sel_registerName("setEnabled:"), 0);

            objc_msgSend_ptr((void*)menu, sel_registerName("addItem:"), menuItem);
            // addItem: retains (the menu owns it); balance our alloc/init +1.
            objc_msgSend_ret_nint(menuItem, sel_registerName("release"));
        }

        return menu;
    }

    /// <summary>
    /// Maps an accelerator string to an NSMenuItem key equivalent plus modifier mask. Returns ("", 0) when
    /// the accelerator is absent or unparsable — the item simply has no shortcut.
    /// </summary>
    internal static (string KeyEquivalent, nint ModifierMask) ResolveKeyEquivalent(string? accelerator)
    {
        if (!AcceleratorParser.TryParse(accelerator, preferCommand: true, out var parsed))
            return ("", 0);

        nint mask = 0;
        if (parsed.Command) mask |= ModifierCommand;
        if (parsed.Control) mask |= ModifierControl;
        if (parsed.Alt) mask |= ModifierOption;
        if (parsed.Shift) mask |= ModifierShift;

        var key = parsed.Key switch
        {
            // NSxxxFunctionKey constants (NSEvent.h) — private-use unicode points AppKit renders as key glyphs.
            "up" => "\uF700",
            "down" => "\uF701",
            "left" => "\uF702",
            "right" => "\uF703",
            "home" => "\uF729",
            "end" => "\uF72B",
            "pageup" => "\uF72C",
            "pagedown" => "\uF72D",
            "delete" => "\uF728",
            "escape" => "\u001B",
            "enter" => "\r",
            "tab" => "\t",
            "space" => " ",
            "backspace" => "\u0008",
            var k when AcceleratorParser.IsFunctionKey(k) =>
                char.ConvertFromUtf32(0xF704 + int.Parse(k.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1),
            var k => k, // single character, already lowercase (uppercase would imply an implicit Shift)
        };

        return (key, mask);
    }

    private static unsafe void EnsureHandlerClass()
    {
        if (_classRegistered) return;

        var superclass = objc_getClass("NSObject");
        _handlerClass = objc_allocateClassPair(superclass, "RynMenuBarHandler", 0);

        // Per-instance pointer-sized ivar holding GCHandle.ToIntPtr(...) of the owning backend.
        // "^v" is the ObjC type encoding for void*; size/alignment are pointer-sized.
        class_addIvar(
            _handlerClass, BackendIvarName, (nuint)sizeof(nint), (byte)nint.Log2(sizeof(nint)), "^v");

        class_addMethod(
            _handlerClass,
            sel_registerName("menuItemClicked:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMenuItemClicked,
            "v@:@");

        objc_registerClassPair(_handlerClass);
        _classRegistered = true;
    }

    // Recover the owning backend from the handler object's ivar. Returns null if the handler is gone or the
    // GCHandle has been freed (Dispose raced the callback).
    private static unsafe MacOsMenuBarBackend? ResolveBackend(nint self)
    {
        if (self == 0) return null;
        object_getInstanceVariable((void*)self, BackendIvarName, out var raw);
        if (raw == 0) return null;
        var handle = GCHandle.FromIntPtr(raw);
        return handle.IsAllocated ? handle.Target as MacOsMenuBarBackend : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnMenuItemClicked(nint self, nint sel, nint sender)
        => NativeGuard.Invoke("MacOsMenuBarBackend.OnMenuItemClicked", () =>
        {
            var instance = ResolveBackend(self);
            if (instance is null) return;

            var tag = (int)objc_msgSend_ret_nint((void*)sender, sel_registerName("tag"));
            string? itemId;
            lock (instance._lock)
            {
                if (tag >= 0 && tag < instance._customIds.Count)
                {
                    itemId = instance._customIds[tag];
                }
                else
                {
                    // A click on an item whose tag no longer maps (e.g. a rebuild raced the click). Surface it
                    // rather than dropping silently — this class of no-op cost a downstream integrator an hour.
                    System.Diagnostics.Trace.TraceWarning(
                        $"MenuBar: click dropped — item tag {tag} out of range (custom items: {instance._customIds.Count}).");
                    return;
                }
            }
            instance.MenuItemClicked?.Invoke(itemId);
        });

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
        // after the native handler can no longer fire a callback into it; if the loop is already gone the
        // dispatcher drops the work and we free the handle below.
        _mainThread.InvokeAsync(DisposeOnUi).GetAwaiter().GetResult();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    private unsafe void DisposeOnUi()
    {
        if (_handlerObject != 0)
        {
            // Clear the ivar first so a late callback resolves to null rather than a freed handle. The menu
            // itself stays installed — the app is shutting down and AppKit owns it via setMainMenu:'s retain.
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
    private static unsafe partial nint objc_msgSend_3nint_ret_nint(
        void* receiver, nint selector, nint arg1, nint arg2, nint arg3);
}
