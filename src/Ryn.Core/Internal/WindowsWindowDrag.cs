using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

internal static partial class WindowsWindowDrag
{
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMove = 0xF010;
    private const nuint HtCaption = 2;
    private const uint GaRoot = 2;

    [SupportedOSPlatform("windows")]
    internal static void Start(nint hwnd)
    {
        if (hwnd == 0) return;
        var root = GetAncestor(hwnd, GaRoot);
        if (root != 0) hwnd = root;

        // Queue the move until after the WebView IPC callback returns. Entering the system move loop
        // synchronously from that callback is re-entrant and Windows immediately rejects later drags.
        ReleaseCapture();
        PostMessageW(hwnd, WmSysCommand, ScMove | HtCaption, 0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
