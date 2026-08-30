using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

/// <summary>Bridges platform movement notifications into Ryn's managed window event surface.</summary>
internal static unsafe partial class WindowMoveObserver
{
    internal enum Backend
    {
        None,
        Windows,
        MacOS,
        X11Polling,
    }

    private const uint WmExitSizeMove = 0x0232;
    private const uint LinuxPollIntervalMs = 100;
    private const nuint WindowsSubclassId = 0x52594E4D; // "RYNM"

    private static readonly ConcurrentDictionary<nint, nint> MacWindows = new();
    private static readonly object MacObserverLock = new();
    private static nint s_macObserver;

    internal static Backend SelectBackend(bool isWindows, bool isMacOS, bool isLinux, bool isNativeWayland) =>
        isWindows ? Backend.Windows :
        isMacOS ? Backend.MacOS :
        isLinux && !isNativeWayland ? Backend.X11Polling :
        Backend.None;

    internal static nuint Install(nint nativeWindow, void* userdata, bool isNativeWayland)
    {
        if (nativeWindow == 0 || userdata == null)
            return 0;
        if (OperatingSystem.IsWindows())
            return InstallWindows(nativeWindow, userdata);
        if (OperatingSystem.IsMacOS())
            return InstallMacOS(nativeWindow, userdata);
        if (OperatingSystem.IsLinux() && !isNativeWayland)
            return g_timeout_add_full(0, LinuxPollIntervalMs,
                (nint)(delegate* unmanaged[Cdecl]<void*, int>)&OnLinuxPoll, (nint)userdata, 0);
        return 0;
    }

    internal static void Uninstall(nint nativeWindow, nuint registration, bool isNativeWayland)
    {
        if (OperatingSystem.IsWindows())
        {
            RemoveWindowSubclass(nativeWindow, &OnWindowsMessage, WindowsSubclassId);
        }
        else if (OperatingSystem.IsMacOS())
        {
            UninstallMacOS(nativeWindow);
        }
        else if (OperatingSystem.IsLinux() && !isNativeWayland && registration != 0)
        {
            g_source_remove(registration);
        }
    }

    [SupportedOSPlatform("windows")]
    private static nuint InstallWindows(nint hwnd, void* userdata) =>
        SetWindowSubclass(hwnd, &OnWindowsMessage, WindowsSubclassId, (nuint)userdata) ? 1u : 0u;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnWindowsMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint referenceData)
    {
        if (message == WmExitSizeMove)
        {
            var window = NativeCallbackHelper.Resolve<RynWindow>((void*)referenceData);
            NativeGuard.Invoke("WindowMoveObserver.Windows", window.HandleNativeMove);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [SupportedOSPlatform("macos")]
    private static nuint InstallMacOS(nint nsWindow, void* userdata)
    {
        EnsureMacObserver();
        MacWindows[nsWindow] = (nint)userdata;
        var center = objc_msgSend_ret_nint(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        objc_msgSend_observe(center, sel_registerName("addObserver:selector:name:object:"), s_macObserver,
            sel_registerName("onWindowMoved:"), CreateNSString("NSWindowDidMoveNotification"), nsWindow);
        return 1;
    }

    [SupportedOSPlatform("macos")]
    private static void EnsureMacObserver()
    {
        if (Volatile.Read(ref s_macObserver) != 0)
            return;

        lock (MacObserverLock)
        {
            if (Volatile.Read(ref s_macObserver) != 0)
                return;
            var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RynWindowMoveObserver", 0);
            class_addMethod(cls, sel_registerName("onWindowMoved:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMacWindowMoved, "v@:@");
            objc_registerClassPair(cls);
            s_macObserver = objc_msgSend_ret_nint(objc_msgSend_ret_nint(cls, sel_registerName("alloc")), sel_registerName("init"));
        }
    }

    [SupportedOSPlatform("macos")]
    private static void UninstallMacOS(nint nsWindow)
    {
        MacWindows.TryRemove(nsWindow, out _);
        if (s_macObserver == 0)
            return;
        var center = objc_msgSend_ret_nint(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        objc_msgSend_remove(center, sel_registerName("removeObserver:name:object:"), s_macObserver,
            CreateNSString("NSWindowDidMoveNotification"), nsWindow);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMacWindowMoved(nint self, nint selector, nint notification)
    {
        NativeGuard.Invoke("WindowMoveObserver.MacOS", () =>
        {
            var nsWindow = objc_msgSend_ret_nint(notification, sel_registerName("object"));
            if (MacWindows.TryGetValue(nsWindow, out var userdata))
                NativeCallbackHelper.Resolve<RynWindow>((void*)userdata).HandleNativeMove();
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnLinuxPoll(void* userdata)
    {
        var window = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("WindowMoveObserver.X11", window.HandleNativeMove);
        return 1;
    }

    [SupportedOSPlatform("macos")]
    private static nint CreateNSString(string value) =>
        objc_msgSend_string(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint referenceData);

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);

    [LibraryImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("libglib-2.0.so.0")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint g_timeout_add_full(int priority, uint interval, nint function, nint data, nint notify);

    [LibraryImport("libglib-2.0.so.0")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool g_source_remove(nuint tag);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool class_addMethod(nint cls, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_ret_nint(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_observe(nint receiver, nint selector, nint observer, nint callback, nint name, nint obj);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_remove(nint receiver, nint selector, nint observer, nint name, nint obj);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_string(nint receiver, nint selector, string value);
}
