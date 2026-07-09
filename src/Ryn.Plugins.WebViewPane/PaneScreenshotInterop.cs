using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;
using Ryn.Interop;
using Ryn.Plugins.WebViewPane.Native;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Captures a PNG of a pane's visible viewport with the engine's native snapshot API:
/// <c>WKWebView.takeSnapshot</c> (ObjC block completion), <c>ICoreWebView2.CapturePreview</c> into a
/// memory IStream (COM completed-handler), and <c>webkit_web_view_get_snapshot</c> → cairo PNG.
/// Start on the UI thread; the completion fires later on the engine's callback thread with either
/// PNG bytes or an error message — never both, never neither.
/// </summary>
internal static unsafe partial class PaneScreenshotInterop
{
    internal sealed class Capture
    {
        public required Action<byte[]?, string?> Completion { get; init; }
        public nint Stream; // Windows: IStream* being written by CapturePreview
    }

    public static void Start(saucer_webview* webview, Action<byte[]?, string?> completion)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0)
        {
            completion(null, "The pane's native webview handle is unavailable.");
            return;
        }

        if (OperatingSystem.IsMacOS()) StartMac(native, completion);
        else if (OperatingSystem.IsWindows()) StartWindows(native, completion);
        else if (OperatingSystem.IsLinux()) StartLinux(native, completion);
        else completion(null, "webviewPane.screenshot is not supported on this OS.");
    }

    // --- macOS ---

    [SupportedOSPlatform("macos")]
    private static void StartMac(nint wkWebView, Action<byte[]?, string?> completion)
    {
        var context = GCHandle.Alloc(new Capture { Completion = completion });
        var block = MacBlocks.Create(
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnMacSnapshot,
            GCHandle.ToIntPtr(context));
        try
        {
            // takeSnapshotWithConfiguration:nil → visible viewport at device scale. WebKit copies the block.
            objc_msgSend_2(wkWebView,
                PaneEngineInterop.sel_registerName("takeSnapshotWithConfiguration:completionHandler:"), 0, block);
        }
        finally
        {
            MacBlocks.Free(block);
        }
    }

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMacSnapshot(nint block, nint nsImage, nint nsError)
    {
        var handle = GCHandle.FromIntPtr(MacBlocks.GetContext(block));
        var capture = (Capture)handle.Target!;
        handle.Free();

        NativeGuard.Invoke("PaneScreenshotInterop.OnMacSnapshot", () =>
        {
            if (nsImage == 0)
            {
                capture.Completion(null, DescribeNSError(nsError));
                return;
            }
            var png = EncodeNSImageAsPng(nsImage);
            if (png is null) capture.Completion(null, "Failed to encode the snapshot as PNG.");
            else capture.Completion(png, null);
        });
    }

    [SupportedOSPlatform("macos")]
    private static byte[]? EncodeNSImageAsPng(nint nsImage)
    {
        var sel = PaneEngineInterop.sel_registerName("CGImageForProposedRect:context:hints:");
        var cgImage = objc_msgSend_3(nsImage, sel, 0, 0, 0);
        if (cgImage == 0) return null;

        var rep = objc_msgSend_1(
            objc_msgSend_0(PaneEngineInterop.objc_getClass("NSBitmapImageRep"),
                PaneEngineInterop.sel_registerName("alloc")),
            PaneEngineInterop.sel_registerName("initWithCGImage:"), cgImage);
        if (rep == 0) return null;

        try
        {
            // NSBitmapImageFileTypePNG = 4; returns autoreleased NSData.
            var data = objc_msgSend_nuint_ptr(rep,
                PaneEngineInterop.sel_registerName("representationUsingType:properties:"), 4, 0);
            if (data == 0) return null;

            var bytes = objc_msgSend_0(data, PaneEngineInterop.sel_registerName("bytes"));
            var length = (long)objc_msgSend_0(data, PaneEngineInterop.sel_registerName("length"));
            if (bytes == 0 || length <= 0 || length > int.MaxValue) return null;

            var result = new byte[length];
            Marshal.Copy(bytes, result, 0, (int)length);
            return result;
        }
        finally
        {
            _ = objc_msgSend_0(rep, PaneEngineInterop.sel_registerName("release"));
        }
    }

    private static string DescribeNSError(nint nsError)
    {
        if (nsError == 0) return "The engine returned no snapshot.";
        var description = objc_msgSend_0(nsError, PaneEngineInterop.sel_registerName("localizedDescription"));
        if (description == 0) return "The engine returned no snapshot.";
        var utf8 = objc_msgSend_0(description, PaneEngineInterop.sel_registerName("UTF8String"));
        return utf8 == 0 ? "The engine returned no snapshot." : Marshal.PtrToStringUTF8(utf8) ?? "Snapshot failed.";
    }

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_0(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_1(nint receiver, nint selector, nint arg1);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_2(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_3(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_nuint_ptr(nint receiver, nint selector, nuint arg1, nint arg2);

    // --- Windows ---

    private static readonly Guid IidCapturePreviewCompletedHandler = new("697e05e9-3d8f-45fa-96f4-8ffe1ededaf5");
    private const int SlotWebView2CapturePreview = 30; // ICoreWebView2::CapturePreview
    private const int SlotStreamRead = 3;              // ISequentialStream::Read
    private const int SlotStreamSeek = 5;              // IStream::Seek

    [SupportedOSPlatform("windows")]
    private static void StartWindows(nint coreWebView2, Action<byte[]?, string?> completion)
    {
        var stream = SHCreateMemStream(0, 0);
        if (stream == 0)
        {
            completion(null, "Failed to allocate a capture stream.");
            return;
        }

        var capture = new Capture { Completion = completion, Stream = stream };
        var handler = ComCallback.Create(IidCapturePreviewCompletedHandler,
            (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&OnWindowsCaptureCompleted, capture);

        var capturePreview = (delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)
            (*(nint**)coreWebView2)[SlotWebView2CapturePreview];
        var hr = capturePreview(coreWebView2, 0 /* PNG */, stream, handler);
        ComCallback.Release(handler); // WebView2 holds its own reference until Invoke

        if (hr < 0)
        {
            PaneEngineInterop.ComRelease(stream);
            completion(null, $"CapturePreview failed (HRESULT 0x{hr:X8}).");
        }
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindowsCaptureCompleted(nint comThis, int errorCode)
    {
        var capture = ComCallback.GetCallback<Capture>(comThis);
        NativeGuard.Invoke("PaneScreenshotInterop.OnWindowsCaptureCompleted", () =>
        {
            var stream = capture.Stream;
            try
            {
                if (errorCode < 0)
                {
                    capture.Completion(null, $"CapturePreview failed (HRESULT 0x{errorCode:X8}).");
                    return;
                }
                var png = ReadStreamToEnd(stream);
                if (png is null) capture.Completion(null, "Failed to read the capture stream.");
                else capture.Completion(png, null);
            }
            finally
            {
                PaneEngineInterop.ComRelease(stream);
            }
        });
        return 0;
    }

    // CA1508: `got` is written through a pointer by the native Read call; the analyzer cannot see it.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    [SupportedOSPlatform("windows")]
    private static byte[]? ReadStreamToEnd(nint stream)
    {
        var vtbl = *(nint**)stream;
        var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)vtbl[SlotStreamSeek];
        if (seek(stream, 0, 0 /* STREAM_SEEK_SET */, null) < 0) return null;

        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtbl[SlotStreamRead];
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            uint got = 0;
            int hr;
            fixed (byte* p = chunk)
            {
                hr = read(stream, p, (uint)chunk.Length, &got);
            }
            if (hr < 0) return null;
            if (got == 0) break;
            buffer.Write(chunk, 0, (int)got);
            if (hr == 1 /* S_FALSE: end of stream */) break;
        }
        return buffer.ToArray();
    }

    [LibraryImport("shlwapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SHCreateMemStream(nint pInit, uint cbInit);

    // --- Linux ---

    [SupportedOSPlatform("linux")]
    private static void StartLinux(nint webKitWebView, Action<byte[]?, string?> completion)
    {
        var context = GCHandle.Alloc(new Capture { Completion = completion });
        // WEBKIT_SNAPSHOT_REGION_VISIBLE = 0, WEBKIT_SNAPSHOT_OPTIONS_NONE = 0
        webkit_web_view_get_snapshot(webKitWebView, 0, 0, 0,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnLinuxSnapshotReady,
            GCHandle.ToIntPtr(context));
    }

    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLinuxSnapshotReady(nint sourceObject, nint asyncResult, nint userData)
    {
        var handle = GCHandle.FromIntPtr(userData);
        var capture = (Capture)handle.Target!;
        handle.Free();

        NativeGuard.Invoke("PaneScreenshotInterop.OnLinuxSnapshotReady", () =>
        {
            nint error = 0;
            var surface = webkit_web_view_get_snapshot_finish(sourceObject, asyncResult, &error);
            if (surface == 0)
            {
                capture.Completion(null, DescribeGError(error));
                return;
            }
            try
            {
                var path = Path.Combine(Path.GetTempPath(), $"ryn-pane-{Guid.NewGuid():N}.png");
                try
                {
                    // CAIRO_STATUS_SUCCESS = 0
                    if (cairo_surface_write_to_png(surface, path) != 0)
                    {
                        capture.Completion(null, "cairo failed to encode the snapshot as PNG.");
                        return;
                    }
                    capture.Completion(File.ReadAllBytes(path), null);
                }
                finally
                {
                    try { File.Delete(path); } catch (IOException) { }
                }
            }
            finally
            {
                cairo_surface_destroy(surface);
            }
        });
    }

    [SupportedOSPlatform("linux")]
    private static string DescribeGError(nint error)
    {
        if (error == 0) return "The engine returned no snapshot.";
        try
        {
            // GError layout: { GQuark domain (uint32 + pad); int code; char* message }
            var message = Marshal.PtrToStringUTF8(*(nint*)(error + 8));
            return string.IsNullOrEmpty(message) ? "The engine returned no snapshot." : message;
        }
        finally
        {
            g_error_free(error);
        }
    }

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void webkit_web_view_get_snapshot(nint webView, int region, int options,
        nint cancellable, nint callback, nint userData);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint webkit_web_view_get_snapshot_finish(nint webView, nint result, nint* error);

    [LibraryImport("cairo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int cairo_surface_write_to_png(nint surface,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

    [LibraryImport("cairo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void cairo_surface_destroy(nint surface);

    [LibraryImport("glib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void g_error_free(nint error);
}
