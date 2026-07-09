using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;
using Ryn.Interop;
using Ryn.Plugins.WebViewPane.Native;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Chrome DevTools Protocol passthrough, Windows-only. WebView2 exposes a real CDP endpoint
/// (<c>CallDevToolsProtocolMethod</c> / <c>GetDevToolsProtocolEventReceiver</c>); WKWebView and
/// WebKitGTK have no public equivalent, so those platforms throw a typed "unsupported" error rather
/// than hang. The native handle saucer hands out on Windows is already <c>ICoreWebView2_2*</c>, which
/// derives from <c>ICoreWebView2</c> where these methods live.
/// </summary>
internal static unsafe partial class PaneCdpInterop
{
    private const int SlotCallDevToolsProtocolMethod = 36; // IUnknown(3) + ICoreWebView2 local 33
    private const int SlotGetDevToolsProtocolEventReceiver = 42; // local 39
    private const int SlotReceiverAddEvent = 3;              // ICoreWebView2DevToolsProtocolEventReceiver local 0
    private const int SlotEventArgsGetParameterJson = 3;     // ICoreWebView2DevToolsProtocolEventReceivedEventArgs local 0

    private static readonly Guid IidCallCompletedHandler = new("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574");
    private static readonly Guid IidEventReceivedHandler = new("e2fda4be-5456-406c-a261-3d452138362c");

    /// <summary>Boxes a value type for ComCallback's reference-typed callback slot.</summary>
    private sealed class Ref<T>(T value) where T : class { public T Value { get; } = value; }

    private static long s_nextCallId;
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<string>> PendingCalls = new();

    /// <summary>
    /// Calls a CDP method and completes with the JSON result. Throws
    /// <see cref="PlatformNotSupportedException"/> off Windows.
    /// </summary>
    public static Task<string> CallAsync(saucer_webview* webview, string method, string paramsJson)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromException<string>(new PlatformNotSupportedException(
                "webviewPane.cdpCall is only supported on Windows (WebView2). macOS and Linux have no public DevTools Protocol."));

        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0)
            return Task.FromException<string>(new InvalidOperationException("The pane's native webview handle is unavailable."));

        return CallWindows(native, method, string.IsNullOrEmpty(paramsJson) ? "{}" : paramsJson);
    }

    /// <summary>
    /// Subscribes to a CDP event; each occurrence invokes <paramref name="onEvent"/> with the event's
    /// parameter JSON. Throws <see cref="PlatformNotSupportedException"/> off Windows.
    /// </summary>
    public static void Subscribe(saucer_webview* webview, string eventName, Action<string> onEvent)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "webviewPane.cdpSubscribe is only supported on Windows (WebView2).");

        var native = PaneEngineInterop.GetNativeHandle(webview);
        if (native == 0)
            throw new InvalidOperationException("The pane's native webview handle is unavailable.");

        SubscribeWindows(native, eventName, onEvent);
    }

    [SupportedOSPlatform("windows")]
    private static Task<string> CallWindows(nint coreWebView2, string method, string paramsJson)
    {
        var callId = Interlocked.Increment(ref s_nextCallId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingCalls[callId] = tcs;

        var handler = ComCallback.Create(IidCallCompletedHandler,
            (nint)(delegate* unmanaged[Stdcall]<nint, int, char*, int>)&OnCallCompleted, new Ref<TaskCompletionSource<string>>(tcs));

        var call = (delegate* unmanaged[Stdcall]<nint, char*, char*, nint, int>)(*(nint**)coreWebView2)[SlotCallDevToolsProtocolMethod];
        int hr;
        fixed (char* m = method)
        fixed (char* p = paramsJson)
        {
            hr = call(coreWebView2, m, p, handler);
        }
        ComCallback.Release(handler);

        if (hr < 0)
        {
            PendingCalls.TryRemove(callId, out _);
            tcs.TrySetException(new InvalidOperationException($"CallDevToolsProtocolMethod failed (HRESULT 0x{hr:X8})."));
        }
        return tcs.Task;
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnCallCompleted(nint comThis, int errorCode, char* returnJson)
    {
        var tcs = ComCallback.GetCallback<Ref<TaskCompletionSource<string>>>(comThis).Value;
        NativeGuard.Invoke("PaneCdpInterop.OnCallCompleted", () =>
        {
            if (errorCode < 0)
                tcs.TrySetException(new InvalidOperationException($"CDP method failed (HRESULT 0x{errorCode:X8})."));
            else
                tcs.TrySetResult(returnJson == null ? "{}" : new string(returnJson));
        });
        return 0;
    }

    // CA1508: receiver is written through the out-pointer by the native getter.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "Out-params are written by native COM calls the analyzer cannot see.")]
    [SupportedOSPlatform("windows")]
    private static void SubscribeWindows(nint coreWebView2, string eventName, Action<string> onEvent)
    {
        var getReceiver = (delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)(*(nint**)coreWebView2)[SlotGetDevToolsProtocolEventReceiver];
        nint receiver = 0;
        int hr;
        fixed (char* e = eventName)
        {
            hr = getReceiver(coreWebView2, e, &receiver);
        }
        if (hr < 0 || receiver == 0)
            throw new InvalidOperationException($"GetDevToolsProtocolEventReceiver failed (HRESULT 0x{hr:X8}).");

        try
        {
            var handler = ComCallback.Create(IidEventReceivedHandler,
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&OnEventReceived, new Ref<Action<string>>(onEvent));
            var add = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)(*(nint**)receiver)[SlotReceiverAddEvent];
            long token = 0;
            _ = add(receiver, handler, &token);
            ComCallback.Release(handler); // the receiver holds its reference
        }
        finally
        {
            PaneEngineInterop.ComRelease(receiver);
        }
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnEventReceived(nint comThis, nint sender, nint args)
    {
        var onEvent = ComCallback.GetCallback<Ref<Action<string>>>(comThis).Value;
        NativeGuard.Invoke("PaneCdpInterop.OnEventReceived", () =>
        {
            if (args == 0) return;
            var getJson = (delegate* unmanaged[Stdcall]<nint, char**, int>)(*(nint**)args)[SlotEventArgsGetParameterJson];
            char* json = null;
            if (getJson(args, &json) >= 0 && json != null)
            {
                var payload = new string(json);
                Marshal.FreeCoTaskMem((nint)json);
                onEvent(payload);
            }
        });
        return 0;
    }
}
