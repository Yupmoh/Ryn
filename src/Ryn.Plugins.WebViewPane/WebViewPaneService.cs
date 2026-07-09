using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Plugins.WebViewPane;

// CA1054/CA1055/CA1056 (use System.Uri): suppressed deliberately — pane URLs are URL-bar strings. They come
// from and go back to JS as raw text (possibly scheme-less or partial), and saucer's C API takes strings;
// round-tripping through System.Uri would reject or rewrite inputs the engine accepts.
/// <summary>
/// Owns secondary native webviews ("panes") positioned inside the main window's client area — real
/// WKWebView/WebView2/WebKitGTK instances created through saucer, so panes render the full web with the OS
/// engine and no embedded Chromium. All native calls are marshalled onto the UI thread; pane state lives in
/// a registry keyed by pane id, and engine events are forwarded to the application's JS as
/// <c>webviewPane.*</c> events.
/// </summary>
[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
    Justification = "Pane URLs are engine-bound URL-bar strings, not validated URIs.")]
[SuppressMessage("Design", "CA1055:URI-like return values should not be strings",
    Justification = "Pane URLs are engine-bound URL-bar strings, not validated URIs.")]
public sealed partial class WebViewPaneService : IDisposable
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 5.0;
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(10);

    private readonly IMainThreadDispatcher _mainThread;
    private readonly IServiceProvider _services;

    private readonly object _lock = new();
    private readonly Dictionary<int, PaneState> _panes = [];
    private int _nextPaneId = 1;
    private long _nextEvalId = 1;
    private bool _disposed;

    internal Action<string, string>? EmitEvent { get; set; }

    // Pinned per pane (via Handle) so native callbacks can recover the state; freed on close.
    internal sealed class PaneState
    {
        public required WebViewPaneService Service { get; init; }
        public required int Id { get; init; }
        public nint Webview;
        public GCHandle Handle;
        public double Zoom = 1.0;
        public string Url = "";
        public readonly ConcurrentDictionary<long, TaskCompletionSource<string>> PendingEvals = new();
    }

    internal WebViewPaneService(IMainThreadDispatcher mainThread, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _mainThread = mainThread;
        _services = services;
    }

    /// <summary>Opens a pane and returns its id. Throws if the application window is not up yet.</summary>
    public async Task<int> OpenAsync(PaneOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var created = -1;
        Exception? failure = null;
        await _mainThread.InvokeAsync(() =>
        {
            try
            {
                created = OpenOnUi(request);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }).ConfigureAwait(false);

        if (failure is not null) throw failure;
        if (created < 0)
            throw new InvalidOperationException("Cannot open a webview pane before the application is running.");
        return created;
    }

    private unsafe int OpenOnUi(PaneOpenRequest request)
    {
        var window = _services.GetService<RynWindowAccessor>()?.Window;
        var windowHandle = window?.SaucerWindowHandle ?? 0;
        if (windowHandle == 0) return -1;

        var opts = Saucer.saucer_webview_options_new((saucer_window*)windowHandle);
        if (!string.IsNullOrEmpty(request.UserAgent))
        {
            fixed (byte* p = Utf8z(request.UserAgent))
                Saucer.saucer_webview_options_set_user_agent(opts, (sbyte*)p);
        }
        if (!string.IsNullOrEmpty(request.StoragePath))
        {
            Directory.CreateDirectory(request.StoragePath);
            fixed (byte* p = Utf8z(request.StoragePath))
                Saucer.saucer_webview_options_set_storage_path(opts, (sbyte*)p);
            Saucer.saucer_webview_options_set_persistent_cookies(opts, 1);
        }

        int error = 0;
        var webview = Saucer.saucer_webview_new(opts, &error);
        Saucer.saucer_webview_options_free(opts);
        if (webview == null)
            throw new InvalidOperationException($"Failed to create webview pane (error code: {error}).");

        int id;
        lock (_lock)
        {
            id = _nextPaneId++;
        }
        var state = new PaneState { Service = this, Id = id, Webview = (nint)webview };
        state.Zoom = Math.Clamp(request.Zoom, MinZoom, MaxZoom);
        state.Handle = GCHandle.Alloc(state);

        var userdata = (void*)GCHandle.ToIntPtr(state.Handle);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_NAVIGATED,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, saucer_url*, void*, void>)&OnNavigated, 1, userdata);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_TITLE,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, sbyte*, nuint, void*, void>)&OnTitleChanged, 1, userdata);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_LOAD,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, saucer_state, void*, void>)&OnLoadStateChanged, 1, userdata);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_DOM_READY,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, void*, void>)&OnDomReady, 1, userdata);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_FAVICON,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, saucer_icon*, void*, void>)&OnFavicon, 1, userdata);
        Saucer.saucer_webview_on(webview, saucer_webview_event.SAUCER_WEBVIEW_EVENT_MESSAGE,
            (void*)(delegate* unmanaged[Cdecl]<saucer_webview*, sbyte*, nuint, void*, saucer_status>)&OnMessage, 1, userdata);

        Saucer.saucer_webview_set_bounds(webview, request.X, request.Y, request.Width, request.Height);

        if (request.DevTools)
            Saucer.saucer_webview_set_dev_tools(webview, 1);

        if (state.Zoom != 1.0)
            ApplyZoomOnUi(state);

        if (!string.IsNullOrEmpty(request.Url))
        {
            state.Url = request.Url;
            fixed (byte* p = Utf8z(request.Url))
                Saucer.saucer_webview_set_url_str(webview, (sbyte*)p);
        }

        lock (_lock)
        {
            _panes[id] = state;
        }
        return id;
    }

    /// <summary>Closes a pane and releases its native webview. Returns false for unknown ids.</summary>
    public async Task<bool> CloseAsync(int id)
    {
        if (_disposed) return false;
        PaneState? state;
        lock (_lock)
        {
            if (!_panes.Remove(id, out state)) return false;
        }
        await _mainThread.InvokeAsync(() => CloseOnUi(state)).ConfigureAwait(false);
        Emit("webviewPane.closed", JsonSerializer.Serialize(
            new PaneClosedEvent(id), WebViewPaneJsonContext.Default.PaneClosedEvent));
        return true;
    }

    private static unsafe void CloseOnUi(PaneState state)
    {
        var webview = (saucer_webview*)state.Webview;
        if (webview != null)
        {
            foreach (var evt in Enum.GetValues<saucer_webview_event>())
                Saucer.saucer_webview_off_all(webview, evt);
            Saucer.saucer_webview_free(webview);
            state.Webview = 0;
        }
        if (state.Handle.IsAllocated)
            state.Handle.Free();

        foreach (var pending in state.PendingEvals.Values)
            pending.TrySetException(new InvalidOperationException("The pane was closed."));
        state.PendingEvals.Clear();
    }

    public void SetBounds(int id, int x, int y, int width, int height) =>
        WithPane(id, (wv, _) => SetBoundsOnUi(wv, x, y, width, height));

    private static unsafe void SetBoundsOnUi(nint webview, int x, int y, int width, int height) =>
        Saucer.saucer_webview_set_bounds((saucer_webview*)webview, x, y, width, height);

    public void Navigate(int id, string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        WithPane(id, (wv, state) =>
        {
            state.Url = url;
            NavigateOnUi(wv, url);
        });
    }

    private static unsafe void NavigateOnUi(nint webview, string url)
    {
        fixed (byte* p = Utf8z(url))
            Saucer.saucer_webview_set_url_str((saucer_webview*)webview, (sbyte*)p);
    }

    public void Back(int id) => WithPane(id, (wv, _) => BackOnUi(wv));
    private static unsafe void BackOnUi(nint webview) => Saucer.saucer_webview_back((saucer_webview*)webview);

    public void Forward(int id) => WithPane(id, (wv, _) => ForwardOnUi(wv));
    private static unsafe void ForwardOnUi(nint webview) => Saucer.saucer_webview_forward((saucer_webview*)webview);

    public void Reload(int id) => WithPane(id, (wv, _) => ReloadOnUi(wv));
    private static unsafe void ReloadOnUi(nint webview) => Saucer.saucer_webview_reload((saucer_webview*)webview);

    public void SetDevTools(int id, bool enabled) => WithPane(id, (wv, _) => SetDevToolsOnUi(wv, enabled));
    private static unsafe void SetDevToolsOnUi(nint webview, bool enabled) =>
        Saucer.saucer_webview_set_dev_tools((saucer_webview*)webview, enabled ? (byte)1 : (byte)0);

    /// <summary>
    /// Sets the pane zoom (clamped to 0.25–5.0). Native page zoom on macOS (WKWebView.pageZoom); elsewhere a
    /// CSS zoom that is re-applied automatically after each navigation.
    /// </summary>
    public void SetZoom(int id, double factor)
    {
        var clamped = Math.Clamp(factor, MinZoom, MaxZoom);
        WithPane(id, (_, state) =>
        {
            state.Zoom = clamped;
            ApplyZoomOnUi(state);
        });
    }

    /// <summary>
    /// Overrides the pane's user agent for subsequent navigations. Applies immediately on macOS/Linux;
    /// WebView2 picks it up on the next navigation (reload to see it take effect).
    /// </summary>
    public async Task SetUserAgentAsync(int id, string userAgent)
    {
        ArgumentException.ThrowIfNullOrEmpty(userAgent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PaneState? state;
        lock (_lock)
        {
            _panes.TryGetValue(id, out state);
        }
        if (state is null) throw new ArgumentException($"Unknown pane id {id}.", nameof(id));

        Exception? failure = null;
        await _mainThread.InvokeAsync(() =>
        {
            try
            {
                if (state.Webview != 0)
                    SetUserAgentOnUi(state.Webview, userAgent);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }).ConfigureAwait(false);
        if (failure is not null) throw failure;
    }

    private static unsafe void SetUserAgentOnUi(nint webview, string userAgent) =>
        PaneEngineInterop.SetUserAgent((saucer_webview*)webview, userAgent);

    /// <summary>Runs JavaScript in the pane, fire-and-forget.</summary>
    public void Execute(int id, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        WithPane(id, (wv, _) => ExecuteOnUi(wv, code));
    }

    private static unsafe void ExecuteOnUi(nint webview, string code)
    {
        fixed (byte* p = Utf8z(code))
            Saucer.saucer_webview_execute((saucer_webview*)webview, (sbyte*)p);
    }

    /// <summary>
    /// Evaluates JavaScript in the pane and returns the JSON-serialized result. Awaitable results are
    /// awaited. Throws on script errors, unknown panes, and a 10s timeout.
    /// </summary>
    public async Task<string> EvalAsync(int id, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PaneState? state;
        lock (_lock)
        {
            _panes.TryGetValue(id, out state);
        }
        if (state is null) throw new ArgumentException($"Unknown pane id {id}.", nameof(id));

        var evalId = Interlocked.Increment(ref _nextEvalId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.PendingEvals[evalId] = tcs;

        var script = BuildEvalScript(evalId, code);
        await _mainThread.InvokeAsync(() =>
        {
            if (state.Webview != 0)
                ExecuteOnUi(state.Webview, script);
        }).ConfigureAwait(false);

        try
        {
            return await tcs.Task.WaitAsync(EvalTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"webviewPane.eval did not complete within {EvalTimeout.TotalSeconds:0}s.");
        }
        finally
        {
            state.PendingEvals.TryRemove(evalId, out _);
        }
    }

    /// <summary>The pane's current URL as last reported by navigation events.</summary>
    public string GetUrl(int id)
    {
        lock (_lock)
        {
            return _panes.TryGetValue(id, out var state) ? state.Url : "";
        }
    }

    /// <summary>Ids of all open panes.</summary>
    public int[] List()
    {
        lock (_lock)
        {
            return [.. _panes.Keys];
        }
    }

    /// <summary>Closes every pane. Called on dispose and when the owning window closes.</summary>
    public void CloseAll()
    {
        List<PaneState> panes;
        lock (_lock)
        {
            panes = [.. _panes.Values];
            _panes.Clear();
        }
        if (panes.Count == 0) return;
        _mainThread.InvokeAsync(() =>
        {
            foreach (var pane in panes)
                CloseOnUi(pane);
        }).GetAwaiter().GetResult();
    }

    private void WithPane(int id, Action<nint, PaneState> action)
    {
        if (_disposed) return;
        PaneState? state;
        lock (_lock)
        {
            _panes.TryGetValue(id, out state);
        }
        if (state is null) return;
        _mainThread.Post(() =>
        {
            if (state.Webview != 0) action(state.Webview, state);
        });
    }

    private static unsafe void ApplyZoomOnUi(PaneState state)
    {
        var wv = (saucer_webview*)state.Webview;
        if (wv == null) return;

        if (OperatingSystem.IsMacOS())
        {
            // WKWebView.pageZoom (macOS 11+): true page zoom, survives navigation.
            var wkWebView = GetNativeWebViewHandle(wv);
            if (wkWebView != 0)
            {
                objc_msgSend_double((void*)wkWebView, sel_registerName("setPageZoom:"), state.Zoom);
                return;
            }
        }

        // CSS zoom fallback (WebView2/WebKitGTK): cleared by navigation, so OnDomReady re-applies it.
        var js = $"document.documentElement.style.zoom={state.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)};";
        ExecuteOnUi(state.Webview, js);
    }

    private static unsafe nint GetNativeWebViewHandle(saucer_webview* webview)
    {
        nuint size;
        Unsafe.SkipInit(out size);
        Saucer.saucer_webview_native(webview, 0, null, &size);
        if (size < (nuint)sizeof(nint) || size > 64) return 0;
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf)
        {
            Saucer.saucer_webview_native(webview, 0, ptr, &size);
            return MemoryMarshal.Read<nint>(buf);
        }
    }

    /// <summary>
    /// Wraps user code so its (awaited) result comes back through saucer's message channel tagged with the
    /// eval id. The code is inlined as a JavaScript expression — not passed through <c>eval()</c>, which
    /// strict-CSP sites (GitHub, banks) block — so statements must be wrapped in an IIFE by the caller:
    /// <c>(() =&gt; { ...; return x; })()</c>. Errors are captured and reported rather than thrown into the page.
    /// </summary>
    internal static string BuildEvalScript(long evalId, string code)
    {
        return
            $$"""
            (async function() {
              var send = function(payload) {
                try { window.saucer.internal.message(JSON.stringify(payload)); } catch (e) { }
              };
              try {
                var result = await (
            {{code}}
                );
                var body;
                try { body = JSON.stringify(result === undefined ? null : result); }
                catch (e) { body = JSON.stringify(String(result)); }
                send({ __rynPaneEval: {{evalId}}, ok: true, result: JSON.parse(body) });
              } catch (e) {
                send({ __rynPaneEval: {{evalId}}, ok: false, error: String(e) });
              }
            })();
            """;
    }

    /// <summary>
    /// Parses a saucer message as an eval envelope. Returns false for anything that isn't ours so other
    /// message consumers (or none) see it.
    /// </summary>
    internal static bool TryParseEvalMessage(string message, out long evalId, out bool ok, out string payload)
    {
        evalId = 0;
        ok = false;
        payload = "";
        if (!message.Contains("__rynPaneEval", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("__rynPaneEval", out var idProp)
                || idProp.ValueKind != JsonValueKind.Number
                || !idProp.TryGetInt64(out evalId))
            {
                return false;
            }
            ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            payload = ok
                ? (root.TryGetProperty("result", out var result) ? result.GetRawText() : "null")
                : (root.TryGetProperty("error", out var error) ? error.GetString() ?? "eval failed" : "eval failed");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // --- native event callbacks (UI thread) ---

    private static unsafe PaneState? Resolve(void* userdata)
    {
        if (userdata == null) return null;
        var handle = GCHandle.FromIntPtr((nint)userdata);
        return handle.IsAllocated ? handle.Target as PaneState : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNavigated(saucer_webview* webview, saucer_url* url, void* userdata)
    {
        var urlString = ReadUrl(url);
        NativeGuard.Invoke("WebViewPaneService.OnNavigated", () =>
        {
            var state = Resolve(userdata);
            if (state is null) return;

            state.Url = urlString;
            state.Service.Emit("webviewPane.navigated", JsonSerializer.Serialize(
                new PaneNavigatedEvent(state.Id, urlString), WebViewPaneJsonContext.Default.PaneNavigatedEvent));
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnTitleChanged(saucer_webview* webview, sbyte* title, nuint length, void* userdata)
    {
        var titleString = length == 0 || title == null
            ? ""
            : Encoding.UTF8.GetString((byte*)title, checked((int)length));
        NativeGuard.Invoke("WebViewPaneService.OnTitleChanged", () =>
        {
            var state = Resolve(userdata);
            if (state is null) return;

            state.Service.Emit("webviewPane.titleChanged", JsonSerializer.Serialize(
                new PaneTitleChangedEvent(state.Id, titleString), WebViewPaneJsonContext.Default.PaneTitleChangedEvent));
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLoadStateChanged(saucer_webview* webview, saucer_state loadState, void* userdata)
        => NativeGuard.Invoke("WebViewPaneService.OnLoadStateChanged", () =>
        {
            var state = Resolve(userdata);
            if (state is null) return;

            var name = loadState == saucer_state.SAUCER_STATE_STARTED ? "started" : "finished";
            state.Service.Emit("webviewPane.loadStateChanged", JsonSerializer.Serialize(
                new PaneLoadStateEvent(state.Id, name), WebViewPaneJsonContext.Default.PaneLoadStateEvent));
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDomReady(saucer_webview* webview, void* userdata)
        => NativeGuard.Invoke("WebViewPaneService.OnDomReady", () =>
        {
            var state = Resolve(userdata);
            if (state is null) return;

            // Re-apply CSS zoom lost to the navigation (no-op on macOS where zoom is native).
            if (state.Zoom != 1.0 && !OperatingSystem.IsMacOS())
                ApplyZoomOnUi(state);

            state.Service.Emit("webviewPane.domReady", JsonSerializer.Serialize(
                new PaneDomReadyEvent(state.Id), WebViewPaneJsonContext.Default.PaneDomReadyEvent));
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnFavicon(saucer_webview* webview, saucer_icon* icon, void* userdata)
    {
        // The icon's lifetime ends with the callback; extract its bytes before entering managed dispatch.
        // Cap the size — favicons are small, and an outsized buffer would balloon the event payload.
        if (icon == null || Saucer.saucer_icon_empty(icon) != 0) return;
        var stash = Saucer.saucer_icon_data(icon);
        if (stash == null) return;
        string dataUrl;
        try
        {
            var size = Saucer.saucer_stash_size(stash);
            if (size == 0 || size > 512 * 1024) return;
            var data = Saucer.saucer_stash_data(stash);
            if (data == null) return;
            var bytes = new ReadOnlySpan<byte>(data, checked((int)size));
            dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        finally
        {
            Saucer.saucer_stash_free(stash);
        }

        NativeGuard.Invoke("WebViewPaneService.OnFavicon", () =>
        {
            var state = Resolve(userdata);
            if (state is null) return;

            state.Service.Emit("webviewPane.faviconChanged", JsonSerializer.Serialize(
                new PaneFaviconEvent(state.Id, dataUrl), WebViewPaneJsonContext.Default.PaneFaviconEvent));
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe saucer_status OnMessage(saucer_webview* webview, sbyte* message, nuint length, void* userdata)
    {
        if (message == null || length == 0) return saucer_status.SAUCER_STATE_UNHANDLED;
        var text = Encoding.UTF8.GetString((byte*)message, checked((int)length));
        return NativeGuard.Invoke("WebViewPaneService.OnMessage", saucer_status.SAUCER_STATE_UNHANDLED, () =>
        {
            var state = Resolve(userdata);
            if (state is null) return saucer_status.SAUCER_STATE_UNHANDLED;

            if (!TryParseEvalMessage(text, out var evalId, out var ok, out var payload))
                return saucer_status.SAUCER_STATE_UNHANDLED;

            if (state.PendingEvals.TryRemove(evalId, out var tcs))
            {
                if (ok) tcs.TrySetResult(payload);
                else tcs.TrySetException(new InvalidOperationException($"Pane eval failed: {payload}"));
            }
            return saucer_status.SAUCER_STATE_HANDLED;
        });
    }

    private static unsafe string ReadUrl(saucer_url* url)
    {
        if (url == null) return "";
        nuint size;
        Unsafe.SkipInit(out size);
        Saucer.saucer_url_string(url, null, &size);
        if (size == 0 || size > 64 * 1024) return "";
        var buf = new byte[(int)size];
        fixed (byte* ptr = buf)
        {
            Saucer.saucer_url_string(url, (sbyte*)ptr, &size);
        }
        var span = buf.AsSpan(0, (int)size);
        var nul = span.IndexOf((byte)0);
        if (nul >= 0) span = span[..nul];
        return Encoding.UTF8.GetString(span);
    }

    private void Emit(string eventName, string json) => EmitEvent?.Invoke(eventName, json);

    private static byte[] Utf8z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    public void Dispose()
    {
        if (_disposed) return;
        CloseAll();
        _disposed = true;
    }

    // --- ObjC runtime (macOS page zoom) ---

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_double(void* receiver, nint selector, double value);
}
