using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core;
using Ryn.Core.Internal;

namespace Ryn.Plugins.MenuBar.Backends;

/// <summary>
/// Win32 menu bar attached to the main saucer window via <c>SetMenu</c>. Menu clicks arrive as
/// <c>WM_COMMAND</c> on the window's procedure, which saucer owns — so the window is subclassed with
/// comctl32's <c>SetWindowSubclass</c> (the supported way to prepend a handler without stealing the wndproc).
/// Accelerator strings are displayed (the <c>\t</c> column) but not globally bound; key events reach the
/// focused webview, where the app can act on them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsMenuBarBackend : IMenuBarBackend
{
    private const int WmCommand = 0x0111;

    private const int MfString = 0x0000;
    private const int MfGrayed = 0x0001;
    private const int MfPopup = 0x0010;
    private const int MfSeparator = 0x0800;

    private const nuint SubclassId = 0x52594E; // "RYN"

    private readonly IMainThreadDispatcher _mainThread;
    private readonly Func<nint> _windowHandleProvider;

    private nint _hwnd;
    private nint _hMenu;
    private bool _subclassed;
    private bool _disposed;

    private GCHandle _selfHandle;

    // Menu snapshot plus the per-rebuild command-id maps. Guarded by _lock; command ids start at 1 and are
    // assigned depth-first during BuildMenu.
    private readonly List<MenuBarItem> _menuItems = [];
    private readonly Dictionary<int, string> _customIdMap = [];
    private readonly Dictionary<int, string> _roleIdMap = [];
    private readonly object _lock = new();

    public event Action<string>? MenuItemClicked;
    public event Action<string>? RoleActivated;

    internal WindowsMenuBarBackend(IMainThreadDispatcher mainThread, Func<nint> windowHandleProvider)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        ArgumentNullException.ThrowIfNull(windowHandleProvider);
        _mainThread = mainThread;
        _windowHandleProvider = windowHandleProvider;
    }

    public void SetMenu(IReadOnlyList<MenuBarItem> items)
    {
        lock (_lock)
        {
            _menuItems.Clear();
            _menuItems.AddRange(items);
        }
        // Menus belong to the thread that owns the window; marshal onto the UI thread. Post (not InvokeAsync)
        // because SetMenu is fire-and-forget for callers; the snapshot above is already taken.
        _mainThread.Post(RebuildMenu);
    }

    private unsafe void RebuildMenu()
    {
        if (_disposed) return;

        if (_hwnd == 0)
            _hwnd = _windowHandleProvider();
        if (_hwnd == 0) return; // no native window yet — nothing to attach to

        EnsureSubclass();

        nint newMenu = 0;
        lock (_lock)
        {
            _customIdMap.Clear();
            _roleIdMap.Clear();

            if (_menuItems.Count > 0)
            {
                newMenu = CreateMenu();
                var nextId = 1;
                foreach (var topItem in _menuItems)
                {
                    if (topItem.Separator) continue; // separators are meaningless at the top level
                    var submenu = BuildPopup(topItem.Items ?? [], ref nextId);
                    AppendMenu(newMenu, MfPopup, submenu, topItem.Label ?? topItem.Id ?? string.Empty);
                }
            }
        }

        SetMenu(_hwnd, newMenu);
        DrawMenuBar(_hwnd);

        if (_hMenu != 0)
            DestroyMenu(_hMenu);
        _hMenu = newMenu;
    }

    // Builds one popup menu (caller-owned until attached via MF_POPUP, after which the parent owns it and
    // DestroyMenu on the root frees the whole tree). Caller holds _lock.
    private nint BuildPopup(IReadOnlyList<MenuBarItem> items, ref int nextId)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Separator)
            {
                AppendMenu(menu, MfSeparator, 0, null);
                continue;
            }

            // Submenu only when it has children; an empty (non-null) Items — a JSON `items: []` leaf — must
            // stay a clickable command, not a dead popup. See MenuBarItem.IsSubmenu.
            if (item.IsSubmenu)
            {
                var submenu = BuildPopup(item.Items!, ref nextId);
                AppendMenu(menu, MfPopup, submenu, item.Label ?? item.Id ?? string.Empty);
                continue;
            }

            string label;
            string? accelerator;
            var id = nextId++;
            if (item.Role is not null && MenuBarRoles.TryGet(item.Role, out var role))
            {
                if (role.Kind == RoleKind.MacOnly) { nextId--; continue; } // hide/showAll/... have no Windows meaning
                label = MenuBarRoles.ResolveLabel(role, item.Label, AppName());
                accelerator = item.Accelerator ?? role.DefaultAccelerator;
                _roleIdMap[id] = role.Name;
            }
            else
            {
                label = item.Label ?? item.Id ?? string.Empty;
                accelerator = item.Accelerator;
                _customIdMap[id] = item.Id ?? label;
            }

            if (AcceleratorParser.TryParse(accelerator, preferCommand: false, out var parsed))
                label = $"{label}\t{parsed.ToDisplayString()}";

            var flags = MfString;
            if (!item.Enabled) flags |= MfGrayed;
            AppendMenu(menu, flags, id, label);
        }
        return menu;
    }

    private static string AppName() =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Application";

    private unsafe void EnsureSubclass()
    {
        if (_subclassed) return;

        if (!_selfHandle.IsAllocated)
            _selfHandle = GCHandle.Alloc(this);

        _subclassed = SetWindowSubclass(
            _hwnd,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nint, nint>)&SubclassProc,
            SubclassId,
            GCHandle.ToIntPtr(_selfHandle));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nint refData)
        // On a throwing handler fall through to the original wndproc so the window keeps functioning; the
        // onError value is evaluated lazily via the DefSubclassProc call below only on the non-throwing path,
        // so use 0 as the eager fallback (message treated as handled).
        => NativeGuard.Invoke("WindowsMenuBarBackend.SubclassProc", (nint)0, () =>
        {
            if (msg == WmCommand && (lParam == 0))
            {
                var backend = GCHandle.FromIntPtr(refData).Target as WindowsMenuBarBackend;
                if (backend is not null)
                {
                    var commandId = (int)(wParam & 0xFFFF);
                    string? customId;
                    string? roleName;
                    lock (backend._lock)
                    {
                        backend._customIdMap.TryGetValue(commandId, out customId);
                        backend._roleIdMap.TryGetValue(commandId, out roleName);
                    }
                    if (customId is not null)
                    {
                        backend.MenuItemClicked?.Invoke(customId);
                        return 0;
                    }
                    if (roleName is not null)
                    {
                        backend.RoleActivated?.Invoke(roleName);
                        return 0;
                    }
                }
            }

            return DefSubclassProc(hwnd, msg, wParam, lParam);
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Teardown must run on the window's thread; block so the GCHandle is freed only after the subclass
        // can no longer fire into it. If the loop is already gone the dispatcher drops the work.
        _mainThread.InvokeAsync(DisposeOnUi).GetAwaiter().GetResult();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    private unsafe void DisposeOnUi()
    {
        if (_hwnd != 0)
        {
            if (_subclassed)
            {
                RemoveWindowSubclass(
                    _hwnd,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nint, nint>)&SubclassProc,
                    SubclassId);
                _subclassed = false;
            }
            SetMenu(_hwnd, 0);
        }

        if (_hMenu != 0)
        {
            DestroyMenu(_hMenu);
            _hMenu = 0;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial nint CreateMenu();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint hMenu, int uFlags, nint uIdNewItem, string? lpNewItem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "SetMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenu(nint hWnd, nint hMenu);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawMenuBar(nint hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, nint pfnSubclass, nuint uIdSubclass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
