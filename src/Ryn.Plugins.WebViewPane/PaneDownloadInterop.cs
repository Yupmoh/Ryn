using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;
using Ryn.Interop;
using Ryn.Plugins.WebViewPane.Native;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Surfaces a pane's downloads to the app for a save-or-cancel decision, then writes to the chosen
/// path. macOS turns non-displayable navigation responses into <c>WKDownload</c>s via delegate methods
/// added to saucer's navigation delegate, routed to a runtime-built <c>WKDownloadDelegate</c>; Windows
/// subscribes <c>DownloadStarting</c> and drives the <c>DownloadOperation</c> over COM; Linux connects
/// <c>download-started</c> on the WebKitWebView. Progress is real on Windows; macOS and Linux emit
/// start and completion (granular progress is best-effort and currently omitted).
/// </summary>
internal static unsafe partial class PaneDownloadInterop
{
    /// <summary>Per-pane hooks; the interop assigns the download id and passes it back through these.</summary>
    internal sealed class Callbacks
    {
        public required int PaneId { get; init; }
        public required Action<long, string, string> OnRequested { get; init; } // (downloadId, url, suggestedName)
        public required Action<long, long, long> OnProgress { get; init; }      // (downloadId, received, total)
        public required Action<long, string> OnCompleted { get; init; }         // (downloadId, path)
        public required Action<long, string> OnFailed { get; init; }            // (downloadId, error)
    }

    /// <summary>Boxes a value type so it can ride ComCallback's reference-typed callback slot.</summary>
    private sealed class Boxed<T>(T value) where T : struct { public T Value { get; } = value; }

    private static long s_nextDownloadId;
    private static long NextDownloadId() => Interlocked.Increment(ref s_nextDownloadId);

    // native webview handle (idx 0) → its pane's callbacks
    private static readonly ConcurrentDictionary<nint, Callbacks> Registrations = new();

    public static void RegisterPane(saucer_webview* webview, Callbacks callbacks)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0) return;
        Registrations[native] = callbacks;

        if (OperatingSystem.IsMacOS()) InstallMac(native);
        else if (OperatingSystem.IsWindows()) InstallWindows(native);
        else if (OperatingSystem.IsLinux()) InstallLinux(native);
    }

    public static void UnregisterPane(saucer_webview* webview)
    {
        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native != 0) Registrations.TryRemove(native, out _);
    }

    /// <summary>Resolves a pending download: writes to <paramref name="path"/> or cancels. Idempotent.</summary>
    public static void Resolve(long downloadId, bool allow, string? path)
    {
        if (OperatingSystem.IsMacOS()) ResolveMac(downloadId, allow, path);
        else if (OperatingSystem.IsWindows()) ResolveWindows(downloadId, allow, path);
        else if (OperatingSystem.IsLinux()) ResolveLinux(downloadId, allow, path);
    }

    private static Callbacks? Lookup(nint nativeWebView) =>
        Registrations.TryGetValue(nativeWebView, out var cb) ? cb : null;

    // ======================= macOS =======================

    private static int s_macNavHookInstalled;
    private static nint s_downloadDelegate; // shared RynDownloadDelegate instance

    private sealed class MacDownload
    {
        public required long DownloadId { get; init; }
        public required Callbacks Callbacks { get; init; }
        public nint CompletionBlock;   // retained void(^)(NSURL*) awaiting a destination
        public string Path = "";
    }

    private static readonly ConcurrentDictionary<long, MacDownload> MacById = new();
    private static readonly ConcurrentDictionary<nint, MacDownload> MacByDownload = new(); // WKDownload* → record

    [SupportedOSPlatform("macos")]
    private static void InstallMac(nint wkWebView)
    {
        EnsureDownloadDelegate();

        var navDelegate = objc_msgSend_ret(wkWebView, PaneEngineInterop.sel_registerName("navigationDelegate"));
        if (navDelegate == 0) return;

        if (Interlocked.Exchange(ref s_macNavHookInstalled, 1) == 0)
        {
            var cls = object_getClass(navDelegate);
            // saucer implements decidePolicyForNavigationAction but none of these three; clean adds.
            class_addMethod(cls, PaneEngineInterop.sel_registerName("webView:decidePolicyForNavigationResponse:decisionHandler:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnDecideResponse, "v@:@@@?");
            class_addMethod(cls, PaneEngineInterop.sel_registerName("webView:navigationResponse:didBecomeDownload:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnResponseBecameDownload, "v@:@@@");
            class_addMethod(cls, PaneEngineInterop.sel_registerName("webView:navigationAction:didBecomeDownload:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnActionBecameDownload, "v@:@@@");
        }

        // Re-assign so WebKit re-reads the delegate's respondsToSelector set (see PaneLifecycleInterop).
        objc_msgSend_set(wkWebView, PaneEngineInterop.sel_registerName("setNavigationDelegate:"), navDelegate);
    }

    [SupportedOSPlatform("macos")]
    private static void EnsureDownloadDelegate()
    {
        if (s_downloadDelegate != 0) return;

        var cls = objc_allocateClassPair(objc_getClass("NSObject"), "RynPaneDownloadDelegate", 0);
        class_addMethod(cls, PaneEngineInterop.sel_registerName("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&OnDecideDestination, "v@:@@@?");
        class_addMethod(cls, PaneEngineInterop.sel_registerName("downloadDidFinish:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDownloadFinished, "v@:@");
        class_addMethod(cls, PaneEngineInterop.sel_registerName("download:didFailWithError:resumeData:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnDownloadFailed, "v@:@@@");
        objc_registerClassPair(cls);

        s_downloadDelegate = objc_msgSend_ret(
            objc_msgSend_ret(cls, PaneEngineInterop.sel_registerName("alloc")),
            PaneEngineInterop.sel_registerName("init"));
    }

    // WKNavigationResponsePolicyAllow = 1, WKNavigationResponsePolicyDownload = 2
    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDecideResponse(nint self, nint sel, nint webView, nint navResponse, nint decisionHandler)
    {
        NativeGuard.Invoke("PaneDownloadInterop.OnDecideResponse", () =>
        {
            // canShowMIMEType == false → the engine can't render it, so download it.
            var canShow = objc_msgSend_bool_ret(navResponse, PaneEngineInterop.sel_registerName("canShowMIMEType"));
            var policy = canShow ? (nint)1 : (nint)2;
            MacBlocks.InvokeVoidPtr(decisionHandler, policy);
        });
    }

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResponseBecameDownload(nint self, nint sel, nint webView, nint navResponse, nint download)
        => AttachDownloadDelegate(download);

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnActionBecameDownload(nint self, nint sel, nint webView, nint navAction, nint download)
        => AttachDownloadDelegate(download);

    [SupportedOSPlatform("macos")]
    private static void AttachDownloadDelegate(nint download) =>
        NativeGuard.Invoke("PaneDownloadInterop.AttachDownloadDelegate", () =>
            objc_msgSend_set(download, PaneEngineInterop.sel_registerName("setDelegate:"), s_downloadDelegate));

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDecideDestination(nint self, nint sel, nint download, nint response, nint suggestedName, nint completionHandler)
    {
        var name = PtrToString(objc_msgSend_ret(suggestedName, PaneEngineInterop.sel_registerName("UTF8String")));
        var block = MacBlocks.Copy(completionHandler);
        NativeGuard.Invoke("PaneDownloadInterop.OnDecideDestination", () =>
        {
            var webView = objc_msgSend_ret(download, PaneEngineInterop.sel_registerName("webView"));
            var callbacks = webView != 0 ? Lookup(webView) : null;
            if (callbacks is null)
            {
                MacBlocks.InvokeVoidPtr(block, 0); // no owner → cancel
                MacBlocks.ReleaseCopy(block);
                return;
            }

            var url = PtrToString(objc_msgSend_ret(
                objc_msgSend_ret(objc_msgSend_ret(download, PaneEngineInterop.sel_registerName("originalRequest")),
                    PaneEngineInterop.sel_registerName("URL")),
                PaneEngineInterop.sel_registerName("absoluteString"))
                is var u && u != 0 ? u : 0);

            var id = NextDownloadId();
            var record = new MacDownload { DownloadId = id, Callbacks = callbacks, CompletionBlock = block };
            MacById[id] = record;
            MacByDownload[download] = record;
            callbacks.OnRequested(id, url, name);
        });
    }

    [SupportedOSPlatform("macos")]
    private static void ResolveMac(long downloadId, bool allow, string? path)
    {
        if (!MacById.TryGetValue(downloadId, out var record)) return;
        var block = record.CompletionBlock;
        record.CompletionBlock = 0;
        if (block == 0) return; // already resolved

        if (allow && !string.IsNullOrEmpty(path))
        {
            record.Path = path;
            var nsPath = PaneEngineInterop.CreateNSString(path);
            var nsUrl = objc_msgSend_ret_arg(objc_getClass("NSURL"),
                PaneEngineInterop.sel_registerName("fileURLWithPath:"), nsPath);
            MacBlocks.InvokeVoidPtr(block, nsUrl);
        }
        else
        {
            MacBlocks.InvokeVoidPtr(block, 0); // nil destination cancels
            MacById.TryRemove(downloadId, out _);
        }
        MacBlocks.ReleaseCopy(block);
    }

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadFinished(nint self, nint sel, nint download)
        => NativeGuard.Invoke("PaneDownloadInterop.OnDownloadFinished", () =>
        {
            if (!MacByDownload.TryRemove(download, out var record)) return;
            MacById.TryRemove(record.DownloadId, out _);
            record.Callbacks.OnCompleted(record.DownloadId, record.Path);
        });

    [SupportedOSPlatform("macos")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDownloadFailed(nint self, nint sel, nint download, nint error, nint resumeData)
        => NativeGuard.Invoke("PaneDownloadInterop.OnDownloadFailed", () =>
        {
            if (!MacByDownload.TryRemove(download, out var record)) return;
            MacById.TryRemove(record.DownloadId, out _);
            var message = error == 0 ? "download failed" : PtrToString(objc_msgSend_ret(
                objc_msgSend_ret(error, PaneEngineInterop.sel_registerName("localizedDescription")),
                PaneEngineInterop.sel_registerName("UTF8String")));
            record.Callbacks.OnFailed(record.DownloadId, string.IsNullOrEmpty(message) ? "download failed" : message);
        });

    private static string PtrToString(nint utf8) => utf8 == 0 ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint object_getClass(nint obj);

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
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint cls, nint selector, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_ret(nint receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_msgSend_ret_arg(nint receiver, nint selector, nint arg);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_msgSend_set(nint receiver, nint selector, nint value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool objc_msgSend_bool_ret(nint receiver, nint selector);

    // ======================= Windows =======================

    private static readonly Guid IidDownloadStartingEventHandler = new("efedc989-c396-41ca-83f7-07f845a55724");
    private static readonly Guid IidStateChangedEventHandler = new("81336594-7ede-4ba9-bf71-acf0a95b58dd");
    // ICoreWebView2_4::add_DownloadStarting: IUnknown(3) + ICoreWebView2(58) + _2(7) + _3(5) + FrameCreated pair(2).
    private const int SlotAddDownloadStarting = 75;
    // ICoreWebView2DownloadStartingEventArgs (IUnknown + local index):
    private const int SlotDlArgsGetOperation = 3;
    private const int SlotDlArgsPutCancel = 5;
    private const int SlotDlArgsGetResultFilePath = 6;
    private const int SlotDlArgsPutResultFilePath = 7;
    private const int SlotDlArgsGetDeferral = 10;
    private const int SlotDeferralComplete = 3;
    private const int SlotDlOpAddStateChanged = 7;      // DownloadOperation::add_StateChanged
    private const int SlotDlOpGetUri = 9;
    private const int SlotDlOpGetResultFilePath = 15;
    private const int SlotDlOpGetState = 16;
    private const int SlotDlOpGetBytesReceived = 13;
    private const int SlotDlOpGetTotalBytes = 12;
    private static readonly Guid IidCoreWebView2_4 = new("20d02d59-6df2-42dc-bd06-f98a694b1302");

    private sealed class WinDownload
    {
        public required long DownloadId { get; init; }
        public required Callbacks Callbacks { get; init; }
        public nint Args;      // ICoreWebView2DownloadStartingEventArgs*, AddRef'd until resolve
        public nint Deferral;
        public nint Operation;
        public bool Resolved;
    }

    private static readonly ConcurrentDictionary<long, WinDownload> WinById = new();

    // CA1508: out-pointers written by native COM calls; the analyzer cannot see the writes.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    [SupportedOSPlatform("windows")]
    private static void InstallWindows(nint coreWebView2)
    {
        var iid = IidCoreWebView2_4;
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)coreWebView2)[0];
        nint core4 = 0;
        if (qi(coreWebView2, &iid, &core4) < 0 || core4 == 0) return;
        try
        {
            var handler = ComCallback.Create(IidDownloadStartingEventHandler,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&OnWindowsDownloadStarting, new Boxed<nint>(coreWebView2));
            var add = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)(*(nint**)core4)[SlotAddDownloadStarting];
            long token = 0;
            _ = add(core4, handler, &token);
            ComCallback.Release(handler);
        }
        finally
        {
            PaneEngineInterop.ComRelease(core4);
        }
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindowsDownloadStarting(nint comThis, nint sender, nint args)
    {
        NativeGuard.Invoke("PaneDownloadInterop.OnWindowsDownloadStarting", () =>
        {
            var coreWebView2 = ComCallback.GetCallback<Boxed<nint>>(comThis).Value;
            var callbacks = Lookup(coreWebView2);
            if (callbacks is null || args == 0) return;

            var argsVtbl = *(nint**)args;
            nint op = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)argsVtbl[SlotDlArgsGetOperation])(args, &op);

            // AddRef args + take a deferral so both survive until the app resolves. put_ResultFilePath /
            // put_Cancel must be set on the args (not the operation), which the deferral keeps alive.
            var addRef = (delegate* unmanaged[Stdcall]<nint, uint>)argsVtbl[1];
            _ = addRef(args);
            nint deferral = 0;
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)argsVtbl[SlotDlArgsGetDeferral])(args, &deferral);

            var id = NextDownloadId();
            WinById[id] = new WinDownload { DownloadId = id, Callbacks = callbacks, Args = args, Deferral = deferral, Operation = op };

            var url = ReadBstrProp(op, SlotDlOpGetUri);
            var suggested = Path.GetFileName(ReadBstrProp(args, SlotDlArgsGetResultFilePath));
            callbacks.OnRequested(id, url, suggested);
        });
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void ResolveWindows(long downloadId, bool allow, string? path)
    {
        if (!WinById.TryGetValue(downloadId, out var record) || record.Resolved) return;
        record.Resolved = true;

        var argsVtbl = *(nint**)record.Args;
        if (allow)
        {
            if (!string.IsNullOrEmpty(path))
            {
                var putPath = (delegate* unmanaged[Stdcall]<nint, char*, int>)argsVtbl[SlotDlArgsPutResultFilePath];
                fixed (char* p = path) _ = putPath(record.Args, p);
            }
            SubscribeWindowsProgress(record);
        }
        else
        {
            var putCancel = (delegate* unmanaged[Stdcall]<nint, int, int>)argsVtbl[SlotDlArgsPutCancel];
            _ = putCancel(record.Args, 1);
            WinById.TryRemove(downloadId, out _);
        }

        if (record.Deferral != 0)
        {
            var complete = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)record.Deferral)[SlotDeferralComplete];
            _ = complete(record.Deferral);
            PaneEngineInterop.ComRelease(record.Deferral);
            record.Deferral = 0;
        }
        PaneEngineInterop.ComRelease(record.Args);
    }

    [SupportedOSPlatform("windows")]
    private static void SubscribeWindowsProgress(WinDownload record)
    {
        var handler = ComCallback.Create(IidStateChangedEventHandler,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&OnWindowsDownloadStateChanged, new Boxed<long>(record.DownloadId));
        var add = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)(*(nint**)record.Operation)[SlotDlOpAddStateChanged];
        long token = 0;
        _ = add(record.Operation, handler, &token);
        ComCallback.Release(handler);
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindowsDownloadStateChanged(nint comThis, nint sender, nint args)
    {
        NativeGuard.Invoke("PaneDownloadInterop.OnWindowsDownloadStateChanged", () =>
        {
            var id = ComCallback.GetCallback<Boxed<long>>(comThis).Value;
            if (!WinById.TryGetValue(id, out var record) || record.Operation == 0) return;

            var opVtbl = *(nint**)record.Operation;
            int state = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)opVtbl[SlotDlOpGetState])(record.Operation, &state);
            long received = 0, total = 0;
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)opVtbl[SlotDlOpGetBytesReceived])(record.Operation, &received);
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)opVtbl[SlotDlOpGetTotalBytes])(record.Operation, &total);

            // CoreWebView2DownloadState: 0 InProgress, 1 Interrupted, 2 Completed
            if (state == 0) record.Callbacks.OnProgress(id, received, total);
            else if (state == 2)
            {
                WinById.TryRemove(id, out _);
                record.Callbacks.OnCompleted(id, ReadBstrProp(record.Operation, SlotDlOpGetResultFilePath));
            }
            else
            {
                WinById.TryRemove(id, out _);
                record.Callbacks.OnFailed(id, "download interrupted");
            }
        });
        return 0;
    }

    // CA1508: bstr is written through the out-pointer by the native getter.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    [SupportedOSPlatform("windows")]
    private static string ReadBstrProp(nint com, int slot)
    {
        nint bstr = 0;
        _ = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)com)[slot])(com, &bstr);
        if (bstr == 0) return "";
        var s = Marshal.PtrToStringUni(bstr) ?? "";
        Marshal.FreeCoTaskMem(bstr); // CoTaskMemFree matches WebView2's LPWSTR allocation
        return s;
    }

    // ======================= Linux =======================

    private sealed class LinuxDownload
    {
        public required long DownloadId { get; init; }
        public required Callbacks Callbacks { get; init; }
        public nint Download;
        public bool Resolved;
    }

    private static readonly ConcurrentDictionary<long, LinuxDownload> LinuxById = new();
    private static readonly ConcurrentDictionary<nint, LinuxDownload> LinuxByDownload = new();

    [SupportedOSPlatform("linux")]
    private static void InstallLinux(nint webKitWebView)
    {
        _ = g_signal_connect_data(webKitWebView, "download-started",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnLinuxDownloadStarted, 0, 0, 0);
    }

    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLinuxDownloadStarted(nint webView, nint download, nint userData)
        => NativeGuard.Invoke("PaneDownloadInterop.OnLinuxDownloadStarted", () =>
        {
            var callbacks = Lookup(webView);
            if (callbacks is null) return;

            var id = NextDownloadId();
            var record = new LinuxDownload { DownloadId = id, Callbacks = callbacks, Download = download };
            LinuxById[id] = record;
            LinuxByDownload[download] = record;

            // decide-destination fires next; hold it by connecting before returning.
            _ = g_signal_connect_data(download, "decide-destination",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnLinuxDecideDestination, 0, 0, 0);
            _ = g_signal_connect_data(download, "finished",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnLinuxFinished, 0, 0, 0);
            _ = g_signal_connect_data(download, "failed",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnLinuxFailed, 0, 0, 0);

            var uriPtr = webkit_uri_request_get_uri(webkit_download_get_request(download));
            callbacks.OnRequested(id, PtrToString(uriPtr), "");
        });

    // decide-destination returns TRUE to indicate we'll set the destination (possibly later).
    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnLinuxDecideDestination(nint download, nint suggestedName, nint userData) => 1;

    [SupportedOSPlatform("linux")]
    private static void ResolveLinux(long downloadId, bool allow, string? path)
    {
        if (!LinuxById.TryGetValue(downloadId, out var record) || record.Resolved) return;
        record.Resolved = true;
        if (allow && !string.IsNullOrEmpty(path))
        {
            webkit_download_set_destination(record.Download, "file://" + path);
        }
        else
        {
            webkit_download_cancel(record.Download);
            LinuxById.TryRemove(downloadId, out _);
        }
    }

    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLinuxFinished(nint download, nint userData)
        => NativeGuard.Invoke("PaneDownloadInterop.OnLinuxFinished", () =>
        {
            if (!LinuxByDownload.TryRemove(download, out var record)) return;
            LinuxById.TryRemove(record.DownloadId, out _);
            var dest = PtrToString(webkit_download_get_destination(download));
            if (dest.StartsWith("file://", StringComparison.Ordinal)) dest = dest["file://".Length..];
            record.Callbacks.OnCompleted(record.DownloadId, dest);
        });

    [SupportedOSPlatform("linux")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLinuxFailed(nint download, nint error, nint userData)
        => NativeGuard.Invoke("PaneDownloadInterop.OnLinuxFailed", () =>
        {
            if (!LinuxByDownload.TryRemove(download, out var record)) return;
            LinuxById.TryRemove(record.DownloadId, out _);
            record.Callbacks.OnFailed(record.DownloadId, "download failed");
        });

    [LibraryImport("gobject", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint g_signal_connect_data(nint instance, string detailedSignal, nint callback, nint data, nint destroyData, int flags);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint webkit_download_get_request(nint download);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint webkit_uri_request_get_uri(nint request);

    [LibraryImport("webkitgtk", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void webkit_download_set_destination(nint download, string uri);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint webkit_download_get_destination(nint download);

    [LibraryImport("webkitgtk")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void webkit_download_cancel(nint download);
}
