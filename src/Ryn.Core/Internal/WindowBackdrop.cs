using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Core.Internal;

/// <summary>
/// Applies a translucent window backdrop from the native window handle: an NSVisualEffectView behind the
/// webview on macOS, the DWM system backdrop on Windows 11 (22H2+). Returns the material actually applied
/// so callers can report degradation — Windows 10 and Linux fall back to <see cref="BackdropMaterial.None"/>.
/// </summary>
internal static partial class WindowBackdrop
{
    /// <summary>Applies <paramref name="material"/> to the window; returns the effective material.</summary>
    public static BackdropMaterial Apply(nint nativeWindow, BackdropMaterial material)
    {
        if (nativeWindow == 0) return BackdropMaterial.None;
        if (material == BackdropMaterial.None)
        {
            if (OperatingSystem.IsMacOS()) MacRemove(nativeWindow);
            else if (OperatingSystem.IsWindows()) WindowsApply(nativeWindow, BackdropMaterial.None);
            return BackdropMaterial.None;
        }

        if (OperatingSystem.IsMacOS()) return MacApply(nativeWindow, material);
        if (OperatingSystem.IsWindows()) return WindowsApply(nativeWindow, material);
        return BackdropMaterial.None; // Linux: compositor-dependent, out of scope
    }

    // ---------- macOS: NSVisualEffectView behind the content ----------

    [SupportedOSPlatform("macos")]
    private static BackdropMaterial MacApply(nint nsWindow, BackdropMaterial material)
    {
        var contentView = objc_msgSend(nsWindow, Sel("contentView"));
        if (contentView == 0) return BackdropMaterial.None;

        // Make the window non-opaque with a clear background so the effect view shows the desktop.
        objc_msgSend_b(nsWindow, Sel("setOpaque:"), 0);
        var clear = objc_msgSend(objc_getClass("NSColor"), Sel("clearColor"));
        objc_msgSend_p(nsWindow, Sel("setBackgroundColor:"), clear);

        var effectView = FindEffectView(contentView);
        var isNew = effectView == 0;
        if (isNew)
            effectView = objc_msgSend(objc_msgSend(objc_getClass("NSVisualEffectView"), Sel("alloc")), Sel("init"));

        // NSVisualEffectBlendingModeBehindWindow = 0, NSVisualEffectStateActive = 1.
        objc_msgSend_n(effectView, Sel("setBlendingMode:"), 0);
        objc_msgSend_n(effectView, Sel("setState:"), 1);
        objc_msgSend_n(effectView, Sel("setMaterial:"), MacMaterial(material));
        // NSViewWidthSizable(2) | NSViewHeightSizable(16) so it tracks the content view on resize.
        objc_msgSend_n(effectView, Sel("setAutoresizingMask:"), 2 | 16);
        objc_msgSend_r(effectView, Sel("setFrame:"), GetRect(contentView, Sel("bounds")));

        if (isNew)
        {
            SetIdentifier(effectView);
            // Insert below every existing subview (the webview) so it renders behind. NSWindowBelow = -1.
            var subviews = objc_msgSend(contentView, Sel("subviews"));
            var first = subviews != 0 ? objc_msgSend(subviews, Sel("firstObject")) : 0;
            if (first != 0)
                objc_msgSend_pnp(contentView, Sel("addSubview:positioned:relativeTo:"), effectView, -1, first);
            else
                objc_msgSend_p(contentView, Sel("addSubview:"), effectView);
        }
        return material;
    }

    [SupportedOSPlatform("macos")]
    private static void MacRemove(nint nsWindow)
    {
        var contentView = objc_msgSend(nsWindow, Sel("contentView"));
        if (contentView == 0) return;
        var existing = FindEffectView(contentView);
        if (existing != 0) objc_msgSend(existing, Sel("removeFromSuperview"));
        objc_msgSend_b(nsWindow, Sel("setOpaque:"), 1);
    }

    // NSVisualEffectMaterial: 3 = .menu (light blur), 6 = .hudWindow (strong),
    // 18 = .underWindowBackground (mica-like subtle desktop tint).
    private static nint MacMaterial(BackdropMaterial material) => material switch
    {
        BackdropMaterial.Acrylic => 6,
        BackdropMaterial.Mica => 18,
        _ => 3, // Blur
    };

    [SupportedOSPlatform("macos")]
    private static nint FindEffectView(nint contentView)
    {
        var subviews = objc_msgSend(contentView, Sel("subviews"));
        if (subviews == 0) return 0;
        var count = (long)objc_msgSend(subviews, Sel("count"));
        for (long i = 0; i < count; i++)
        {
            var view = objc_msgSend_i(subviews, Sel("objectAtIndex:"), (nuint)i);
            if (view == 0) continue;
            var ident = objc_msgSend(view, Sel("identifier"));
            if (ident != 0 && Marshal.PtrToStringUTF8(objc_msgSend(ident, Sel("UTF8String"))) == "rynBackdrop")
                return view;
        }
        return 0;
    }

    [SupportedOSPlatform("macos")]
    private static void SetIdentifier(nint view)
    {
        var ident = objc_msgSend_s(objc_getClass("NSString"), Sel("stringWithUTF8String:"), "rynBackdrop");
        objc_msgSend_p(view, Sel("setIdentifier:"), ident);
    }

    // ---------- Windows: DWM system backdrop (Win11 22H2+) ----------

    [SupportedOSPlatform("windows")]
    private static BackdropMaterial WindowsApply(nint hwnd, BackdropMaterial material)
    {
        // DWMSBT: 2 None, 3 MainWindow (Mica), 4 TransientWindow (Acrylic), 5 TabbedWindow.
        int dwmsbt = material switch
        {
            BackdropMaterial.None => 2,
            BackdropMaterial.Mica => 3,
            _ => 4, // Blur/Acrylic → acrylic
        };
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref dwmsbt, sizeof(int));
        // The attribute exists only on Windows 11 22H2+; a non-zero HRESULT means the OS ignored it.
        return hr == 0 && material != BackdropMaterial.None ? material : BackdropMaterial.None;
    }

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // ---------- ObjC runtime ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, Width, Height; }

    private static nint Sel(string name) => sel_registerName(name);

    // NSRect struct-return ABI differs by arch: arm64 returns it via the ordinary objc_msgSend trampoline
    // (no _stret variant), x86_64 requires objc_msgSend_stret with a hidden out-pointer. Mirrors MacOsTitleBar.
    private static NSRect GetRect(nint receiver, nint selector)
    {
        if (RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            NSRect rect;
            objc_msgSend_stret_x64(out rect, receiver, selector);
            return rect;
        }
        return objc_msgSend_rect_arm64(receiver, selector);
    }

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_i(nint receiver, nint selector, nuint index);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_s(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_n(nint receiver, nint selector, nint value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_b(nint receiver, nint selector, byte value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_p(nint receiver, nint selector, nint arg);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_pnp(nint receiver, nint selector, nint a, nint b, nint c);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_r(nint receiver, nint selector, NSRect rect);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial NSRect objc_msgSend_rect_arm64(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend_stret")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_stret_x64(out NSRect outRect, nint receiver, nint selector);
}
