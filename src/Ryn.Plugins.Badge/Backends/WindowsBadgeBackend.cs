using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core;

namespace Ryn.Plugins.Badge.Backends;

/// <summary>
/// Windows taskbar overlay badge via <c>ITaskbarList3.SetOverlayIcon</c> (Windows has no dock badge; the
/// overlay in the taskbar button's corner is the platform convention). The label is rendered into a 16×16
/// icon with GDI — a filled red circle with white text, matching what browsers and chat apps do.
/// COM interop is raw vtable calls (no built-in COM interop under NativeAOT).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsBadgeBackend : IBadgeBackend
{
    private const int IconSize = 16;
    private const int SetOverlayIconVtableIndex = 15; // IUnknown(3) + ITaskbarList(5) + ITaskbarList2(1) + 6 into ITaskbarList3

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    private readonly IMainThreadDispatcher _mainThread;
    private readonly Func<nint> _windowHandleProvider;

    private nint _taskbarList; // ITaskbarList3*
    private nint _currentIcon;
    private bool _disposed;

    public WindowsBadgeBackend(IMainThreadDispatcher mainThread, Func<nint> windowHandleProvider)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        ArgumentNullException.ThrowIfNull(windowHandleProvider);
        _mainThread = mainThread;
        _windowHandleProvider = windowHandleProvider;
    }

    public void SetLabel(string? label)
    {
        if (_disposed) return;
        // COM apartment affinity: the ITaskbarList3 instance is created and used only on the UI thread.
        _mainThread.Post(() => SetLabelOnUi(label));
    }

    private void SetLabelOnUi(string? label)
    {
        if (_disposed) return;

        var hwnd = _windowHandleProvider();
        if (hwnd == 0) return;

        if (!EnsureTaskbarList()) return;

        nint icon = 0;
        if (label is not null)
        {
            icon = CreateBadgeIcon(label);
            if (icon == 0) return; // GDI failure — keep the previous overlay rather than clearing it
        }

        // SetOverlayIcon(hwnd, hIcon, description): null hIcon clears. The taskbar copies the icon, so ours
        // can be destroyed as soon as the call returns.
        var vtable = *(nint**)_taskbarList;
        var setOverlayIcon = (delegate* unmanaged[Stdcall]<nint, nint, nint, char*, int>)vtable[SetOverlayIconVtableIndex];
        fixed (char* description = label ?? string.Empty)
        {
            setOverlayIcon(_taskbarList, hwnd, icon, description);
        }

        if (_currentIcon != 0) DestroyIcon(_currentIcon);
        _currentIcon = icon;
    }

    private bool EnsureTaskbarList()
    {
        if (_taskbarList != 0) return true;

        // The saucer UI thread is already COM-initialized (WebView2 requires it); S_FALSE/RPC_E_CHANGED_MODE
        // just mean "already initialized", which is fine.
        _ = CoInitializeEx(0, 0x2 /* COINIT_APARTMENTTHREADED */);

        var clsid = ClsidTaskbarList;
        var iid = IidTaskbarList3;
        if (CoCreateInstance(in clsid, 0, 0x1 /* CLSCTX_INPROC_SERVER */, in iid, out var instance) != 0 || instance == 0)
            return false;

        // HrInit (vtable slot 3) must be called once before any other method.
        var vtable = *(nint**)instance;
        var hrInit = (delegate* unmanaged[Stdcall]<nint, int>)vtable[3];
        if (hrInit(instance) != 0)
        {
            Release(instance);
            return false;
        }

        _taskbarList = instance;
        return true;
    }

    private static void Release(nint unknown)
    {
        var vtable = *(nint**)unknown;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        _ = release(unknown);
    }

    /// <summary>Renders the label into a 16×16 HICON: red filled circle, white text. Returns 0 on failure.</summary>
    private static nint CreateBadgeIcon(string label)
    {
        // Overlay icons are tiny; more than three characters is unreadable. "99+" is the longest sane label.
        if (label.Length > 3) label = label[..3];

        var screenDc = GetDC(0);
        if (screenDc == 0) return 0;

        nint colorDc = 0, colorBitmap = 0, maskBitmap = 0, font = 0, brush = 0, icon = 0;
        try
        {
            colorDc = CreateCompatibleDC(screenDc);
            colorBitmap = CreateCompatibleBitmap(screenDc, IconSize, IconSize);
            // Monochrome mask; all-zero bits = fully opaque icon, which is what we want for a filled circle
            // on a transparent background handled via the color key below. Simplest reliable approach: make
            // the whole square part of the icon and rely on the circle filling it edge-to-edge.
            maskBitmap = CreateBitmap(IconSize, IconSize, 1, 1, null);
            if (colorDc == 0 || colorBitmap == 0 || maskBitmap == 0) return 0;

            var oldBitmap = SelectObject(colorDc, colorBitmap);

            // Background: fill with black (becomes the masked area color; the circle covers nearly all of it).
            var rect = new Rect { Left = 0, Top = 0, Right = IconSize, Bottom = IconSize };
            brush = CreateSolidBrush(0x00000000);
            FillRect(colorDc, in rect, brush);
            DeleteObject(brush);

            // Red badge circle (COLORREF is 0x00BBGGRR).
            brush = CreateSolidBrush(0x002419E8); // #E81924
            var oldBrush = SelectObject(colorDc, brush);
            var pen = GetStockObject(8 /* NULL_PEN */);
            var oldPen = SelectObject(colorDc, pen);
            Ellipse(colorDc, 0, 0, IconSize + 1, IconSize + 1);
            SelectObject(colorDc, oldPen);
            SelectObject(colorDc, oldBrush);

            // White label, centered. Segoe UI at ~10px fits 1–3 characters in 16px.
            font = CreateFont(
                label.Length >= 3 ? -8 : -10, 0, 0, 0, 700 /* FW_BOLD */, 0, 0, 0,
                1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
            var oldFont = SelectObject(colorDc, font);
            _ = SetBkMode(colorDc, 1 /* TRANSPARENT */);
            _ = SetTextColor(colorDc, 0x00FFFFFF);
            DrawText(colorDc, label, label.Length, ref rect,
                0x1 /* DT_CENTER */ | 0x4 /* DT_VCENTER */ | 0x20 /* DT_SINGLELINE */);
            SelectObject(colorDc, oldFont);

            SelectObject(colorDc, oldBitmap);

            var iconInfo = new IconInfo
            {
                IsIcon = 1,
                MaskBitmap = maskBitmap,
                ColorBitmap = colorBitmap,
            };
            icon = CreateIconIndirect(ref iconInfo);
            return icon;
        }
        finally
        {
            if (font != 0) DeleteObject(font);
            if (colorBitmap != 0) DeleteObject(colorBitmap);
            if (maskBitmap != 0) DeleteObject(maskBitmap);
            if (colorDc != 0) DeleteDC(colorDc);
            _ = ReleaseDC(0, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Clear the overlay and release COM on the UI thread before flipping _disposed, so the posted work
        // isn't dropped by the guard. If the loop is already gone the dispatcher drops it and the taskbar
        // clears the overlay itself when the window is destroyed.
        _mainThread.InvokeAsync(() =>
        {
            SetLabelOnUi(null);
            if (_currentIcon != 0)
            {
                DestroyIcon(_currentIcon);
                _currentIcon = 0;
            }
            if (_taskbarList != 0)
            {
                Release(_taskbarList);
                _taskbarList = 0;
            }
        }).GetAwaiter().GetResult();

        _disposed = true;
    }

    // --- P/Invoke ---

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, in Rect rect, nint brush);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(nint dc, string text, int count, ref Rect rect, uint format);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ref IconInfo iconInfo);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static unsafe partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPel, void* bits);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Ellipse(nint dc, int left, int top, int right, int bottom);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }
}
