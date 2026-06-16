using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;

namespace Ryn.Plugins.Tray.Backends;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTrayBackend : ITrayBackend
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmCommand = 0x0111;
    private const int WmDestroy = 0x0002;

    private const int NimAdd = 0x00;
    private const int NimModify = 0x01;
    private const int NimDelete = 0x02;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;

    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private const int TpmLeftAlign = 0x0000;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfGrayed = 0x0001;

    private Thread? _thread;
    private nint _hwnd;
    private nint _hIcon;
    private nint _hMenu;
    private bool _iconAdded;
    private bool _disposed;

    private readonly WndProcDelegate _wndProcRef;
    private readonly ManualResetEventSlim _readyEvent = new();
    private readonly object _menuLock = new();
    private readonly List<TrayMenuItem> _menuItems = [];
    private readonly Dictionary<int, string> _menuIdMap = [];

    public event Action? IconClicked;
    public event Action<string>? MenuItemClicked;

    internal WindowsTrayBackend()
    {
        _wndProcRef = WndProc;
    }

    public void Show(string? iconPath, string tooltip)
    {
        if (_thread is null)
        {
            _thread = new Thread(() => RunMessageLoop(iconPath, tooltip))
            {
                IsBackground = true,
                Name = "RynTray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _readyEvent.Wait();
        }
        else if (!_iconAdded && _hwnd != 0)
        {
            var nid = CreateNotifyIconData();
            nid.uFlags = NifMessage | NifIcon | NifTip;
            nid.uCallbackMessage = WmTrayCallback;
            nid.hIcon = _hIcon;
            nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            ShellNotifyIcon(NimAdd, ref nid);
            _iconAdded = true;
        }
        else if (_iconAdded)
        {
            SetTooltip(tooltip);
        }
    }

    public void Hide()
    {
        if (!_iconAdded || _hwnd == 0) return;

        var nid = CreateNotifyIconData();
        ShellNotifyIcon(NimDelete, ref nid);
        _iconAdded = false;
    }

    public void SetTooltip(string tooltip)
    {
        if (!_iconAdded || _hwnd == 0) return;

        var nid = CreateNotifyIconData();
        nid.uFlags = NifTip;
        nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        ShellNotifyIcon(NimModify, ref nid);
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        lock (_menuLock)
        {
            _menuItems.Clear();
            _menuItems.AddRange(items);
        }
    }

    public void ShowNotification(string title, string message)
    {
        if (!_iconAdded || _hwnd == 0) return;

        var nid = CreateNotifyIconData();
        nid.uFlags = NifInfo;
        nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        nid.szInfo = message.Length > 255 ? message[..255] : message;
        nid.dwInfoFlags = 0x01; // NIIF_INFO
        ShellNotifyIcon(NimModify, ref nid);
    }

    private void RunMessageLoop(string? iconPath, string tooltip)
    {
        var className = $"RynTray_{Environment.ProcessId}";
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, nint.Zero, nint.Zero, wc.hInstance, nint.Zero);

        if (iconPath is not null && File.Exists(iconPath))
        {
            _hIcon = LoadImage(nint.Zero, iconPath, 1, 0, 0, 0x0010 | 0x0040); // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE
        }

        var nid = CreateNotifyIconData();
        nid.uFlags = NifMessage | NifIcon | NifTip;
        nid.uCallbackMessage = WmTrayCallback;
        nid.hIcon = _hIcon;
        nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        ShellNotifyIcon(NimAdd, ref nid);
        _iconAdded = true;

        _readyEvent.Set();

        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        // On a throwing handler, return 0 ("message handled") rather than crossing the boundary; the onError
        // value is evaluated eagerly, so it must be a plain default and not a re-entrant DefWindowProc call.
        => NativeGuard.Invoke("WindowsTrayBackend.WndProc", (nint)0, () =>
        {
            if (msg == WmTrayCallback)
            {
                var mouseMsg = (int)(lParam & 0xFFFF);
                if (mouseMsg == WmLButtonUp)
                {
                    IconClicked?.Invoke();
                }
                else if (mouseMsg == WmRButtonUp)
                {
                    ShowContextMenu();
                }
                return 0;
            }

            if (msg == WmCommand)
            {
                var menuId = (int)(wParam & 0xFFFF);
                string? itemId;
                lock (_menuLock)
                {
                    _menuIdMap.TryGetValue(menuId, out itemId);
                }
                if (itemId is not null)
                    MenuItemClicked?.Invoke(itemId);
                return 0;
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        });

    private void ShowContextMenu()
    {
        if (_hMenu != 0)
            DestroyMenu(_hMenu);

        _hMenu = CreatePopupMenu();
        lock (_menuLock)
        {
            _menuIdMap.Clear();
            for (var i = 0; i < _menuItems.Count; i++)
            {
                var item = _menuItems[i];
                var menuId = i + 1;
                _menuIdMap[menuId] = item.Id;

                if (item.Separator)
                {
                    AppendMenu(_hMenu, MfSeparator, 0, null);
                }
                else
                {
                    var flags = MfString;
                    if (!item.Enabled) flags |= MfGrayed;
                    AppendMenu(_hMenu, flags, menuId, item.Label);
                }
            }
        }

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        TrackPopupMenuEx(_hMenu, TpmLeftAlign, pt.X, pt.Y, _hwnd, nint.Zero);
        PostMessage(_hwnd, 0, nint.Zero, nint.Zero); // WM_NULL to dismiss
    }

    private NotifyIconData CreateNotifyIconData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
    };

    ~WindowsTrayBackend() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        if (_iconAdded)
        {
            var nid = CreateNotifyIconData();
            ShellNotifyIcon(NimDelete, ref nid);
            _iconAdded = false;
        }

        if (_hMenu != 0) { DestroyMenu(_hMenu); _hMenu = 0; }
        if (_hIcon != 0) { DestroyIcon(_hIcon); _hIcon = 0; }
        if (_hwnd != 0) { PostMessage(_hwnd, WmDestroy, nint.Zero, nint.Zero); _hwnd = 0; }

        _readyEvent.Dispose();
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int w, int h, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial void TranslateMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint hMenu, int uFlags, int uIdNewItem, string? lpNewItem);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TrackPopupMenuEx(nint hMenu, int uFlags, int x, int y, nint hwnd, nint lptpm);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point lpPoint);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
