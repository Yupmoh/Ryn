using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Core;

public sealed unsafe class RynWindow : IRynWindow, IDisposable
{
    private readonly RynOptions _options;
    private readonly TaskCompletionSource _closeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private saucer_application* _app;
    private saucer_window* _window;
    private saucer_webview* _webview;
    private void* _selfHandle;

    private RynWebView? _rynWebView;
    private LocalWebServer? _localServer;
    private SystemThemeDetector? _themeDetector;
    private WindowStatePersistence? _statePersistence;

    private CommandDispatchHandler? _commandHandler;

    private volatile string _cachedTitle;
    private int _cachedWidth;
    private int _cachedHeight;
    private volatile bool _cachedResizable;
    private int _cachedX;
    private int _cachedY;
    // Last known NON-maximized geometry, tracked separately from the live caches above so a maximized close
    // persists the size/position to restore to rather than the maximized rect (ARC-05). Seeded at init from
    // the window's initial placement and only updated while the window is not maximized.
    private int _normalX;
    private int _normalY;
    private int _normalWidth;
    private int _normalHeight;
    private volatile bool _disposed;

    // GCHandles posted to saucer's UI thread via RunOnUi. RunPostedWindowAction normally claims-and-frees
    // each one; tracking them here lets DisposeNative reclaim any the run loop dropped at shutdown (INT-10),
    // mirroring RynWebView._postedCallbacks.
    private readonly ConcurrentDictionary<nint, byte> _postedHandles = new();

    // Main-thread work submitted (via IMainThreadDispatcher) before the native application exists. saucer's
    // post requires a live _app, so RunOnUi drops actions while _app == null. Buffer them here and drain them
    // — in submission order, on the UI thread — once InitializeNative brings the app up, so a plugin backend
    // (tray/audio) that fences AppKit calls during its InitializeAsync still has them run on the main thread
    // rather than silently dropped (Cluster C / INT-02). Guarded by _preReadyGate; nulled after the drain so
    // later posts go straight to RunOnUi. Drained on the UI thread inside InitializeNative.
    private readonly object _preReadyGate = new();
    private List<Action>? _preReadyQueue = [];
    private volatile bool _appReady;

    // Disposed after the saucer run loop exits so a late token cancellation can't quit a freed _app (PAP-14).
    private CancellationTokenRegistration _quitRegistration;

    /// <inheritdoc />
    public event EventHandler<WindowClosingEventArgs>? Closing;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public event EventHandler<WindowResizedEventArgs>? Resized;

    /// <inheritdoc />
    public event EventHandler? Focused;

    /// <inheritdoc />
    public event EventHandler? Blurred;

    /// <inheritdoc />
    public event EventHandler<WindowMovedEventArgs>? Moved;

    /// <inheritdoc />
    public event EventHandler<WindowStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <inheritdoc />
    public AppTheme Theme => _themeDetector?.Current ?? SystemThemeDetector.Detect();

    internal RynWindow(RynOptions options)
    {
        _options = options;
        _cachedTitle = options.Title;
        _cachedWidth = options.Width;
        _cachedHeight = options.Height;
        _cachedResizable = options.Resizable;
    }

    internal Action<nint>? OnNativeReady { get; set; }

    internal void SetCommandHandler(CommandDispatchHandler handler) => _commandHandler = handler;

    public IRynWebView WebView => _rynWebView ?? throw new InvalidOperationException("Window not initialized");

    public string Title
    {
        get => _cachedTitle;
        set
        {
            _cachedTitle = value;
            RunOnUi(() =>
            {
                if (_window == null) return;
                Span<byte> buf = stackalloc byte[256];
                var str = Utf8String.Create(value, buf);
                Saucer.saucer_window_set_title(_window, str.Pointer);
                str.Dispose();
            });
        }
    }

    public int Width
    {
        get => _cachedWidth;
        set
        {
            _cachedWidth = value;
            RunOnUi(() => { if (_window != null) Saucer.saucer_window_set_size(_window, value, _cachedHeight); });
        }
    }

    public int Height
    {
        get => _cachedHeight;
        set
        {
            _cachedHeight = value;
            RunOnUi(() => { if (_window != null) Saucer.saucer_window_set_size(_window, _cachedWidth, value); });
        }
    }

    public bool Resizable
    {
        get => _cachedResizable;
        set
        {
            _cachedResizable = value;
            RunOnUi(() => { if (_window != null) Saucer.saucer_window_set_resizable(_window, (byte)(value ? 1 : 0)); });
        }
    }

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
        => new(RunOnUiAsync(() => { if (_window != null) Saucer.saucer_window_show(_window); }));

    public ValueTask HideAsync(CancellationToken cancellationToken = default)
        => new(RunOnUiAsync(() => { if (_window != null) Saucer.saucer_window_hide(_window); }));

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        => new(RunOnUiAsync(() => { if (_window != null) Saucer.saucer_window_close(_window); }));

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        // Per-waiter cancellation: WaitAsync hangs the linked registration off a wrapper task, so cancelling
        // one caller's token never poisons the shared _closeTcs (ARC-07). Concurrent waiters — and any later
        // default-token wait — stay tied to the real close signal that OnFinish/OnWindowClosed completes.
        return cancellationToken.CanBeCanceled
            ? new ValueTask(_closeTcs.Task.WaitAsync(cancellationToken))
            : new ValueTask(_closeTcs.Task);
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        _rynWebView?.NavigateAsync(url, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _rynWebView?.EvaluateJavaScriptAsync(script, cancellationToken) ?? new ValueTask<string>(string.Empty);

    public void Close() => RunOnUi(() => { if (_window != null) Saucer.saucer_window_close(_window); });

    /// <summary>
    /// Requests an orderly close from any thread, used by the graceful-shutdown hook
    /// (<see cref="IRynApplicationLifetime.RequestShutdown"/> / <see cref="RynApplication.RequestShutdown"/>).
    /// Routes through <see cref="PostToUi"/> so it runs on the UI thread (queued if the loop is not up yet):
    /// closing the window fires the native CLOSE/CLOSED path, which quits the saucer loop so
    /// <see cref="RynApplication.RunAsync"/> returns and the normal disposal chain runs. A no-op after disposal.
    /// Falls back to quitting the application directly if the window handle is already gone but the app is still
    /// live, so a shutdown request is never silently dropped.
    /// </summary>
    internal void RequestClose() => PostToUi(() =>
    {
        if (_window != null) Saucer.saucer_window_close(_window);
        else if (_app != null) Saucer.saucer_application_quit(_app);
    });

    public void Minimize() => RunOnUi(() => { if (_window != null) Saucer.saucer_window_set_minimized(_window, 1); });

    public void ToggleMaximize() => RunOnUi(() =>
    {
        if (_window != null)
        {
            var isMax = Saucer.saucer_window_maximized(_window) != 0;
            Saucer.saucer_window_set_maximized(_window, (byte)(isMax ? 0 : 1));
        }
    });

    public void StartDrag() => RunOnUi(() => { if (_window != null) Saucer.saucer_window_start_drag(_window); });

    public void StartResize(WindowEdge edge) => RunOnUi(() => { if (_window != null) Saucer.saucer_window_start_resize(_window, (saucer_window_edge)edge); });

    /// <summary>
    /// Marshals a native window operation onto saucer's UI thread. Native window/AppKit calls are not
    /// thread-safe, so all mutating operations are posted to the application loop (a no-op deferral when
    /// already on the UI thread). Safe to call from any thread.
    /// </summary>
    private unsafe void RunOnUi(Action action)
    {
        if (_disposed || _app == null)
            return;

        var data = (nint)NativeCallbackHelper.Alloc(new PostedWindowAction(this, action));
        // Track the handle so DisposeNative can reclaim it if saucer drops the posted callback at shutdown
        // (INT-10). RunPostedWindowAction claims-and-removes it before freeing, so the two never double-free.
        _postedHandles[data] = 0;
        Saucer.saucer_application_post(
            _app,
            (delegate* unmanaged[Cdecl]<void*, void>)&RunPostedWindowAction,
            (void*)data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RunPostedWindowAction(void* userdata)
    {
        var handle = (nint)userdata;
        var payload = NativeCallbackHelper.Resolve<PostedWindowAction>(userdata);
        // Claim the handle so a DisposeNative racing at shutdown can't also free it; whoever removes it frees.
        if (!payload.Owner._postedHandles.TryRemove(handle, out _))
            return;
        NativeGuard.Invoke("RynWindow.RunOnUi", payload.Action);
        NativeCallbackHelper.Free(userdata);
    }

    /// <summary>Pairs a UI-thread action with its owning window, so the static native callback can deregister
    /// the tracked GCHandle (INT-10) before running and freeing it.</summary>
    private sealed record PostedWindowAction(RynWindow Owner, Action Action);

    /// <summary>
    /// Like <see cref="RunOnUi"/> but returns a Task that completes when the posted action has actually run
    /// on the UI thread (or faults if it throws) — so Show/Hide/CloseAsync genuinely await execution rather
    /// than returning a completed task immediately. Completes immediately if the loop isn't running.
    /// </summary>
    private Task RunOnUiAsync(Action action)
    {
        if (_disposed || _app == null)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// True when the caller is already on saucer's UI thread, so UI-thread work can run inline rather than be
    /// posted. Backed by <c>saucer_application_thread_safe</c>, which returns non-zero on the thread that owns
    /// the application/event loop. False before the app exists or after it is torn down (treat as "off thread"
    /// — the safe default is to post/queue, never to run native UI work inline on an unknown thread).
    /// </summary>
    private bool IsOnUiThread => _app != null && !_disposed && Saucer.saucer_application_thread_safe(_app) != 0;

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, used by <see cref="IMainThreadDispatcher"/> to fence
    /// native UI calls (tray/audio AppKit work) made from worker threads (Cluster C / INT-02). Runs inline when
    /// already on the UI thread; queues it when the native app is not up yet (drained in submission order once
    /// the loop starts); otherwise posts via saucer. Safe to call from any thread. A no-op after disposal.
    /// </summary>
    internal void PostToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return;

        // Already on the UI thread → run inline so ordering relative to surrounding UI work is preserved and
        // there is no needless deferral. Wrapped in NativeGuard so a throw is routed to the app's
        // unhandled-exception surface, matching the posted path (RunPostedWindowAction).
        if (IsOnUiThread)
        {
            NativeGuard.Invoke("RynWindow.PostToUi", action);
            return;
        }

        // Native app not up yet → buffer until InitializeNative drains the queue on the UI thread. Double-check
        // _appReady under the lock to close the race with FlushPreReadyQueue (which flips it while holding the
        // gate): if the app became ready between our volatile read and taking the lock, fall through to post.
        if (!_appReady)
        {
            lock (_preReadyGate)
            {
                if (!_appReady && _preReadyQueue is { } queue)
                {
                    queue.Add(action);
                    return;
                }
            }
        }

        RunOnUi(action);
    }

    /// <summary>
    /// Like <see cref="PostToUi"/> but returns a Task that completes when the action has run on the UI thread
    /// (or faults if it throws). Completes inline when already on the UI thread; completes without running if
    /// the app has been disposed. Used by <see cref="IMainThreadDispatcher.InvokeAsync"/>.
    /// </summary>
    internal Task InvokeOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return Task.CompletedTask;

        if (IsOnUiThread)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return Task.FromException(ex); }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Drains any work buffered by <see cref="PostToUi"/> before the native app existed, in submission order,
    /// on the UI thread. Called once from <see cref="InitializeNative"/> after the app/window are created.
    /// Flips <c>_appReady</c> under the gate so a concurrent <see cref="PostToUi"/> either lands in the queue we
    /// snapshot here or takes the post path — never lost, never run twice.
    /// </summary>
    private void FlushPreReadyQueue()
    {
        List<Action>? pending;
        lock (_preReadyGate)
        {
            pending = _preReadyQueue;
            _preReadyQueue = null;
            _appReady = true;
        }

        if (pending is null)
            return;

        // We're on the UI thread here (InitializeNative runs inside saucer's OnReady callback), so run inline
        // through the same NativeGuard barrier the posted path uses.
        foreach (var action in pending)
            NativeGuard.Invoke("RynWindow.PostToUi", action);
    }

    internal void Run(CancellationToken cancellationToken)
    {
        NativeLibraryResolver.Register();
        Span<byte> idBuf = stackalloc byte[256];
        var appIdStr = Utf8String.Create(_options.ApplicationId, idBuf);
        var appOpts = Saucer.saucer_application_options_new(appIdStr.Pointer);
        appIdStr.Dispose();
        int error = 0;
        _app = Saucer.saucer_application_new(appOpts, &error);
        Saucer.saucer_application_options_free(appOpts);
        if (_app == null) throw new InvalidOperationException($"Failed to create saucer application (error code: {error})");
        if (cancellationToken.CanBeCanceled)
            _quitRegistration = cancellationToken.Register(() => { if (_app != null) Saucer.saucer_application_quit(_app); });
        _selfHandle = NativeCallbackHelper.Alloc(this);
        var exitCode = Saucer.saucer_application_run(_app,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnReady,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnFinish,
            _selfHandle);
        _ = exitCode;
        // Dispose the cancellation registration before freeing _app: the registration is only meaningful while
        // the loop runs, and disposing it here closes the post-shutdown closure pin and the TOCTOU window where
        // a late cancellation could call saucer_application_quit on the freed _app (PAP-14).
        _quitRegistration.Dispose();
        _quitRegistration = default;
        DisposeNative();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnReady", () =>
        {
            try
            {
                self.InitializeNative();
            }
            catch
            {
                // Startup failed (webview/window creation, local-server bind, state-dir creation, …). Quit the
                // saucer loop so Run() can return and dispose cleanly, then rethrow into NativeGuard, which
                // routes the exception to RynApplication's UnhandledException surface instead of letting it
                // fail-fast across the native boundary (ARC-01/INT-01/PAP-09).
                if (self._app != null) Saucer.saucer_application_quit(self._app);
                self._closeTcs.TrySetResult();
                throw;
            }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFinish(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnFinish", () => self._closeTcs.TrySetResult());
    }

    private void InitializeNative()
    {
        int error = 0;
        _window = Saucer.saucer_window_new(_app, &error);
        if (_window == null) throw new InvalidOperationException($"Failed to create saucer window (error code: {error})");
        Span<byte> schemeBuf = stackalloc byte[32];
        var schemeStr = Utf8String.Create("ryn", schemeBuf);
        Saucer.saucer_webview_register_scheme(schemeStr.Pointer);
        schemeStr.Dispose();
        // App-declared custom schemes must be registered with the engine before the webview is created
        // (saucer silently no-ops handle_scheme for a scheme it wasn't told about pre-creation). The
        // reserved "ryn" scheme is registered above; skip it here so a stray duplicate can't double-register.
        // The buffer is hoisted out of the loop (CA2014); Utf8String.Create falls back to a pooled buffer
        // for any scheme name longer than it, so reusing one stack span across iterations is safe.
        Span<byte> customSchemeBuf = stackalloc byte[64];
        foreach (var customScheme in _options.CustomSchemes)
        {
            if (string.IsNullOrEmpty(customScheme) || string.Equals(customScheme, "ryn", StringComparison.OrdinalIgnoreCase))
                continue;
            var customSchemeStr = Utf8String.Create(customScheme, customSchemeBuf);
            Saucer.saucer_webview_register_scheme(customSchemeStr.Pointer);
            customSchemeStr.Dispose();
        }
        var webviewOpts = Saucer.saucer_webview_options_new(_window);
        _webview = Saucer.saucer_webview_new(webviewOpts, &error);
        Saucer.saucer_webview_options_free(webviewOpts);
        if (_webview == null) throw new InvalidOperationException($"Failed to create saucer webview (error code: {error})");
        ApplyWindowOptions();
        int ix, iy;
        Saucer.saucer_window_position(_window, &ix, &iy);
        _cachedX = ix;
        _cachedY = iy;
        _normalX = ix;
        _normalY = iy;
        _normalWidth = _cachedWidth;
        _normalHeight = _cachedHeight;
        _rynWebView = new RynWebView(_webview, _app);
        // Tell the webview which schemes were registered with the engine above, so RegisterCustomScheme can
        // attach handlers for them (and reject "ryn"/undeclared schemes). Mirrors the pre-creation loop.
        _rynWebView.SetDeclaredSchemes(_options.CustomSchemes);
        if (_commandHandler is not null) _rynWebView.SetCommandHandler(_commandHandler);
        if (_options.AllowedOrigins.Count > 0) _rynWebView.SetAllowedOrigins(_options.AllowedOrigins.ToList());
        else if (_options.Url is not null) _rynWebView.SetAllowedOrigins([_options.Url.GetLeftPart(UriPartial.Authority)]);
        if (_options.DevTools) _rynWebView.InjectConsoleForwardScript();
        _rynWebView.InjectFileDropScript();
        if (_options.TitleBarStyle is TitleBarStyle.Hidden or TitleBarStyle.Overlay) InjectTitleBarInsets();

        _themeDetector = new SystemThemeDetector();
        _themeDetector.ThemeChanged += t =>
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs { Theme = t });
        // Poll at the detector's default cadence (SystemThemeDetector.DefaultPollInterval, ~5s). Theme changes
        // are rare and user-initiated, so the leisurely interval keeps the per-tick child-process probe off the
        // hot path while still feeling responsive (PAP-11/ARC-18).
        _themeDetector.StartPolling();

        if (_options.PersistWindowState)
        {
            // Best-effort: a read-only/forbidden profile dir must degrade to no-persist, not abort startup at
            // the native boundary (PAP-09). WindowStatePersistence is itself disk-free at construction and
            // best-effort on Load/Save; this guard is belt-and-suspenders should that ever change.
            try
            {
                _statePersistence = new WindowStatePersistence(_options.ApplicationId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _statePersistence = null;
            }

            var state = _statePersistence?.Load();
            if (state is not null)
            {
                Saucer.saucer_window_set_size(_window, state.Width, state.Height);
                _cachedWidth = state.Width;
                _cachedHeight = state.Height;
                // Restore position too (ARC-05): saved X/Y were previously dropped. Clamp against the current
                // screen so a state file from a now-disconnected/secondary monitor can't lose the window.
                var (clampedX, clampedY) = ClampToScreen(state.X, state.Y, state.Width, state.Height);
                Saucer.saucer_window_set_position(_window, clampedX, clampedY);
                _cachedX = clampedX;
                _cachedY = clampedY;
                // Seed normal geometry from the restored (non-maximized) values so an immediate close round-trips.
                _normalX = clampedX;
                _normalY = clampedY;
                _normalWidth = state.Width;
                _normalHeight = state.Height;
                if (state.IsMaximized)
                {
                    Saucer.saucer_window_set_maximized(_window, 1);
                }
            }
        }

        SubscribeWindowEvents();
        if (_options.Url != null)
        {
            var url = _options.Url;
            var isLoopbackDev = url.Scheme == Uri.UriSchemeHttp
                && (url.IsLoopback || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

            if (isLoopbackDev)
            {
                // Dev-server (e.g. Vite) workflow: the UI is served by an external loopback dev server, so a
                // relative /ipc POST would hit the dev server (404) and IPC would silently break. Start an
                // IPC-only Ryn server and point the bridge at it (absolute URL) with CORS for the dev origin.
                var devOrigin = url.GetLeftPart(UriPartial.Authority);
                _localServer = new LocalWebServer(contentDirectory: null, _options.LocalServerPort, allowedCorsOrigin: devOrigin);
                _localServer.StartAsync().GetAwaiter().GetResult();
                _localServer.SetWebView(_rynWebView);
                var ipcBase = _localServer.Url.TrimEnd('/');
                _rynWebView.SetAllowedOrigins([devOrigin, ipcBase]);
                _rynWebView.SetIpcBaseOverride(ipcBase);
            }
            else
            {
                // Remote content (e.g. your HTTPS website). IPC via window.__ryn.invoke is only wired for
                // loopback dev URLs / local content; warn the developer rather than failing silently.
                _rynWebView.WarnInPageConsole(
                    "Ryn: loaded a remote URL. window.__ryn.invoke (IPC) is only available for loopback dev URLs or local content.");
            }

            Span<byte> urlBuf = stackalloc byte[256];
            var urlStr = Utf8String.Create(url.AbsoluteUri, urlBuf);
            Saucer.saucer_webview_set_url_str(_webview, urlStr.Pointer);
            urlStr.Dispose();
        }
        else if (_options.UseLocalServer && _options.ContentDirectory != null)
        {
            _localServer = new LocalWebServer(_options.ContentDirectory, _options.LocalServerPort);
            _localServer.StartAsync().GetAwaiter().GetResult();
            _localServer.SetWebView(_rynWebView);
            var serverUrl = _localServer.Url;
            _rynWebView.SetAllowedOrigins([serverUrl.TrimEnd('/')]);
            Span<byte> urlBuf = stackalloc byte[256];
            var urlStr = Utf8String.Create(serverUrl, urlBuf);
            Saucer.saucer_webview_set_url_str(_webview, urlStr.Pointer);
            urlStr.Dispose();
        }
        else if (_options.ContentDirectory != null) { _rynWebView.SetContentDirectory(_options.ContentDirectory); _rynWebView.NavigateToAppScheme(); }
        else if (_options.Html != null) { _rynWebView.SetHtmlContent(_options.Html); _rynWebView.NavigateToAppScheme(); }
        OnNativeReady?.Invoke((nint)_app);
        Saucer.saucer_window_show(_window);

        // The native app/window now exist and we're on the UI thread. Drain any main-thread work a plugin
        // backend buffered via IMainThreadDispatcher before the loop was up (Cluster C / INT-02), in order.
        FlushPreReadyQueue();
    }

    private void ApplyWindowOptions()
    {
        // Read the caches, not _options: a setter called before native readiness (e.g. window.Title = "X")
        // updates only the cache via RunOnUi's dropped post, so applying _options here would discard it. The
        // caches are seeded from _options in the ctor, so a window with no pre-ready edits is unaffected (ARC-17).
        Span<byte> buf = stackalloc byte[256];
        var titleStr = Utf8String.Create(_cachedTitle, buf);
        Saucer.saucer_window_set_title(_window, titleStr.Pointer);
        titleStr.Dispose();
        Saucer.saucer_window_set_size(_window, _cachedWidth, _cachedHeight);
        Saucer.saucer_window_set_resizable(_window, (byte)(_cachedResizable ? 1 : 0));
        if (_options.Transparent)
        {
            // Fully transparent window + webview backgrounds so the page's own (semi-)transparent content
            // shows through, instead of the opaque default chrome (ARC-04).
            Saucer.saucer_window_set_background(_window, 0, 0, 0, 0);
            Saucer.saucer_webview_set_background(_webview, 0, 0, 0, 0);
        }
        switch (_options.TitleBarStyle)
        {
            case TitleBarStyle.Hidden:
                if (OperatingSystem.IsMacOS()) ApplyMacOsTitleBar(overlay: false);
                else Saucer.saucer_window_set_decorations(_window, saucer_window_decoration.SAUCER_WINDOW_DECORATION_PARTIAL);
                break;
            case TitleBarStyle.Overlay:
                if (OperatingSystem.IsMacOS()) ApplyMacOsTitleBar(overlay: true);
                else Saucer.saucer_window_set_decorations(_window, saucer_window_decoration.SAUCER_WINDOW_DECORATION_PARTIAL);
                break;
            case TitleBarStyle.Frameless:
                Saucer.saucer_window_set_decorations(_window, saucer_window_decoration.SAUCER_WINDOW_DECORATION_NONE);
                break;
        }
        if (_options.IconPath is not null && File.Exists(_options.IconPath))
        {
            Span<byte> iconBuf = stackalloc byte[1024];
            var iconStr = Utf8String.Create(_options.IconPath, iconBuf);
            int iconError;
            System.Runtime.CompilerServices.Unsafe.SkipInit(out iconError);
            var icon = Saucer.saucer_icon_new_from_file(iconStr.Pointer, &iconError);
            iconStr.Dispose();
            if (icon != null && iconError == 0) { Saucer.saucer_window_set_icon(_window, icon); Saucer.saucer_icon_free(icon); }
        }
        else
        {
            ApplyDefaultIcon();
        }
        if (_options.DevTools) { Saucer.saucer_webview_set_dev_tools(_webview, 1); Saucer.saucer_webview_set_context_menu(_webview, 1); }
    }

    private static byte[]? _defaultIconBytes;
    private static bool _defaultIconLoaded;

    /// <summary>
    /// Sets the bundled Ryn icon as the window/taskbar icon when the app hasn't supplied its own via
    /// <see cref="RynOptions.IconPath"/>. The PNG is embedded in this assembly and loaded from memory
    /// (no temp file) through a saucer stash, so every Ryn app gets a branded default.
    /// </summary>
    private void ApplyDefaultIcon()
    {
        var data = DefaultIconBytes;
        if (data is null || data.Length == 0 || _window == null)
            return;

        fixed (byte* ptr = data)
        {
            var stash = Saucer.saucer_stash_new_from(ptr, (nuint)data.Length);
            int iconError;
            System.Runtime.CompilerServices.Unsafe.SkipInit(out iconError);
            var icon = Saucer.saucer_icon_new_from_stash(stash, &iconError);
            if (icon != null && iconError == 0)
            {
                Saucer.saucer_window_set_icon(_window, icon);
                Saucer.saucer_icon_free(icon);
            }
            // saucer copies the stash data into the icon; we still own and must free the stash.
            Saucer.saucer_stash_free(stash);
        }
    }

    private static byte[]? DefaultIconBytes
    {
        get
        {
            if (!_defaultIconLoaded)
            {
                _defaultIconLoaded = true;
                try
                {
                    using var stream = typeof(RynWindow).Assembly.GetManifestResourceStream("Ryn.Core.ryn-icon.png");
                    if (stream is not null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        _defaultIconBytes = ms.ToArray();
                    }
                }
                catch (IOException) { /* fall back to no default icon */ }
            }
            return _defaultIconBytes;
        }
    }

    private void InjectTitleBarInsets()
    {
        double left = 0, top = 0;
        if (OperatingSystem.IsMacOS()) (left, top) = GetMacOsInsets();
        if (left > 0 || top > 0)
        {
            var leftPx = left.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var topPx = top.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            var css = $"document.documentElement.style.setProperty('--ryn-titlebar-inset-left','{leftPx}px');" + $"document.documentElement.style.setProperty('--ryn-titlebar-inset-top','{topPx}px');";
#pragma warning disable CA2012
            _rynWebView!.InjectScriptAsync(css);
#pragma warning restore CA2012
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private (double Left, double Top) GetMacOsInsets()
    {
        nuint size; System.Runtime.CompilerServices.Unsafe.SkipInit(out size);
        Saucer.saucer_window_native(_window, 0, null, &size);
        // Require at least sizeof(nint) so MemoryMarshal.Read<nint> can't over-read the stack buffer if saucer
        // ever reports a smaller size; saucer returns 8 (a pointer) today, so this never trips in practice (INT-11).
        if (size < (nuint)sizeof(nint) || size > 64) return (70, 28);
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf) { Saucer.saucer_window_native(_window, 0, ptr, &size); var nsWindow = System.Runtime.InteropServices.MemoryMarshal.Read<nint>(buf); if (nsWindow != 0) return MacOsTitleBar.GetTrafficLightInsets(nsWindow); }
        return (70, 28);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void ApplyMacOsTitleBar(bool overlay)
    {
        nuint size; System.Runtime.CompilerServices.Unsafe.SkipInit(out size);
        Saucer.saucer_window_native(_window, 0, null, &size);
        // Require at least sizeof(nint) before reading the pointer; under-size would over-read the stack buffer
        // (INT-11). saucer reports 8 in practice, so this lower bound is defensive only.
        if (size < (nuint)sizeof(nint) || size > 64) return;
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf) { Saucer.saucer_window_native(_window, 0, ptr, &size); var nsWindow = System.Runtime.InteropServices.MemoryMarshal.Read<nint>(buf); if (nsWindow != 0) MacOsTitleBar.Apply(nsWindow, overlay); }
    }

    private void SubscribeWindowEvents()
    {
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_CLOSE, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, void*, saucer_policy>)&OnWindowClose, 1, _selfHandle);
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_CLOSED, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, void*, void>)&OnWindowClosed, 1, _selfHandle);
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_RESIZE, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, int, int, void*, void>)&OnWindowResize, 1, _selfHandle);
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_FOCUS, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, byte, void*, void>)&OnWindowFocus, 1, _selfHandle);
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_MAXIMIZE, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, byte, void*, void>)&OnWindowMaximize, 1, _selfHandle);
        Saucer.saucer_window_on(_window, saucer_window_event.SAUCER_WINDOW_EVENT_MINIMIZE, (void*)(delegate* unmanaged[Cdecl]<saucer_window*, byte, void*, void>)&OnWindowMinimize, 1, _selfHandle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static saucer_policy OnWindowClose(saucer_window* window, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        // On a throwing user Closing handler, default to ALLOW (let the window close) rather than crossing the
        // boundary — a stuck window would be worse than honoring the close (ARC-01/INT-01).
        return NativeGuard.Invoke("RynWindow.OnWindowClose", saucer_policy.SAUCER_POLICY_ALLOW, () =>
        {
            var args = new WindowClosingEventArgs();
            self.Closing?.Invoke(self, args);
            if (args.Cancel) { self._rynWebView?.EmitEvent("window.closeCancelled", "{}"); return saucer_policy.SAUCER_POLICY_BLOCK; }
            return saucer_policy.SAUCER_POLICY_ALLOW;
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowClosed(saucer_window* window, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnWindowClosed", () =>
        {
            self.SaveWindowState(window);
            self.Closed?.Invoke(self, EventArgs.Empty);
            self._closeTcs.TrySetResult();
            Saucer.saucer_application_quit(self._app);
        });
    }

    /// <summary>
    /// Captures the live window geometry at close and persists it (ARC-05). Reads position/size straight from
    /// saucer rather than the caches — there is no native "moved" event, so a drag-then-close would otherwise
    /// save stale coordinates. When maximized, saucer reports the maximized rect, so we persist the pre-maximize
    /// (cached "normal") size alongside the maximized flag, and let restore re-maximize.
    /// </summary>
    private void SaveWindowState(saucer_window* window)
    {
        if (_statePersistence is null) return;
        var isMaximized = Saucer.saucer_window_maximized(window) != 0;
        if (!isMaximized)
        {
            // Read the live geometry straight from saucer: there is no native "moved" event, so a drag-then-close
            // would otherwise persist stale coordinates. Refresh both the live and normal caches.
            int x, y, w, h;
            Saucer.saucer_window_position(window, &x, &y);
            Saucer.saucer_window_size(window, &w, &h);
            _cachedX = x; _cachedY = y; _cachedWidth = w; _cachedHeight = h;
            _normalX = x; _normalY = y; _normalWidth = w; _normalHeight = h;
        }
        // Persist the normal (non-maximized) geometry so a maximized close doesn't bake the maximized rect in as
        // the restore size; the IsMaximized flag drives re-maximizing on the next launch.
        _statePersistence.Save(new WindowStateData
        {
            Width = Volatile.Read(ref _normalWidth),
            Height = Volatile.Read(ref _normalHeight),
            X = Volatile.Read(ref _normalX),
            Y = Volatile.Read(ref _normalY),
            IsMaximized = isMaximized,
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowResize(saucer_window* window, int w, int h, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnWindowResize", () =>
        {
            self._cachedWidth = w;
            self._cachedHeight = h;
            // Only snapshot as the normal size when not maximized, so the persisted restore-size stays the
            // user's real window size rather than the maximized rect (ARC-05).
            if (Saucer.saucer_window_maximized(window) == 0) { self._normalWidth = w; self._normalHeight = h; }
            self.Resized?.Invoke(self, new WindowResizedEventArgs { Width = w, Height = h });
            self._rynWebView?.EmitEvent("window.resized", $"{{\"width\":{w},\"height\":{h}}}");
            self.CheckPositionChanged(window);
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowFocus(saucer_window* window, byte focused, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnWindowFocus", () =>
        {
            if (focused != 0) { self.Focused?.Invoke(self, EventArgs.Empty); self._rynWebView?.EmitEvent("window.focused", "{}"); self.CheckPositionChanged(window); }
            else { self.Blurred?.Invoke(self, EventArgs.Empty); self._rynWebView?.EmitEvent("window.blurred", "{}"); }
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowMaximize(saucer_window* window, byte maximized, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnWindowMaximize", () =>
        {
            var state = maximized != 0 ? WindowState.Maximized : WindowState.Normal;
            self.StateChanged?.Invoke(self, new WindowStateChangedEventArgs { State = state });
            var stateName = state == WindowState.Maximized ? "maximized" : "normal";
            self._rynWebView?.EmitEvent("window.stateChanged", $"{{\"state\":\"{stateName}\"}}");
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowMinimize(saucer_window* window, byte minimized, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        NativeGuard.Invoke("RynWindow.OnWindowMinimize", () =>
        {
            var state = minimized != 0 ? WindowState.Minimized : WindowState.Normal;
            self.StateChanged?.Invoke(self, new WindowStateChangedEventArgs { State = state });
            var stateName = state == WindowState.Minimized ? "minimized" : "normal";
            self._rynWebView?.EmitEvent("window.stateChanged", $"{{\"state\":\"{stateName}\"}}");
        });
    }

    private void CheckPositionChanged(saucer_window* window)
    {
        int x, y;
        Saucer.saucer_window_position(window, &x, &y);
        var prevX = _cachedX; var prevY = _cachedY;
        _cachedX = x; _cachedY = y;
        // Track the non-maximized position for persistence (ARC-05); a maximized window's origin is the
        // screen corner, not where the user wants it restored.
        if (Saucer.saucer_window_maximized(window) == 0) { _normalX = x; _normalY = y; }
        if (prevX != x || prevY != y) { Moved?.Invoke(this, new WindowMovedEventArgs { X = x, Y = y }); _rynWebView?.EmitEvent("window.moved", $"{{\"x\":{x},\"y\":{y}}}"); }
    }

    /// <summary>
    /// Clamps a restored window's top-left so it stays on the window's current screen (ARC-05). A state file
    /// saved on a larger or now-disconnected monitor would otherwise place the window partly or wholly
    /// off-screen. Falls back to the requested coordinates if the screen bounds can't be read.
    /// </summary>
    private (int X, int Y) ClampToScreen(int x, int y, int width, int height)
    {
        var screen = Saucer.saucer_window_screen(_window);
        if (screen == null) return (x, y);
        int sx, sy, sw, sh;
        Saucer.saucer_screen_position(screen, &sx, &sy);
        Saucer.saucer_screen_size(screen, &sw, &sh);
        Saucer.saucer_screen_free(screen);
        if (sw <= 0 || sh <= 0) return (x, y);
        // Keep the whole window on screen where it fits; if it's wider/taller than the screen, pin to the
        // top-left so the title bar / window controls stay reachable.
        var maxX = sx + Math.Max(0, sw - width);
        var maxY = sy + Math.Max(0, sh - height);
        return (Math.Clamp(x, sx, maxX), Math.Clamp(y, sy, maxY));
    }

    private void DisposeNative()
    {
        if (_localServer is not null) { _localServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _localServer = null; }
        // Stop the theme poller deterministically once the saucer loop returns, rather than relying on the
        // public Dispose() (which Run() calls anyway) or the finalizer backstop. This ends the per-tick
        // child-process probe spawns promptly at teardown (PAP-11/ARC-18). Dispose is idempotent, so the
        // later Dispose() call is a harmless no-op; nulling the field makes that explicit.
        _themeDetector?.Dispose(); _themeDetector = null;
        _rynWebView?.Dispose(); _rynWebView = null;
        // The saucer run loop has stopped by the time DisposeNative runs (it's called after
        // saucer_application_run returns), so no RunPostedWindowAction can still fire. Reclaim any GCHandles
        // saucer never drained — RunPostedWindowAction is what normally frees them, so without this they leak
        // (INT-10). The TryRemove claim means a callback that did run can't be double-freed here.
        foreach (var handle in _postedHandles.Keys)
        {
            if (_postedHandles.TryRemove(handle, out _))
                NativeCallbackHelper.Free(handle);
        }
        if (_webview != null) { Saucer.saucer_webview_free(_webview); _webview = null; }
        if (_window != null) { Saucer.saucer_window_free(_window); _window = null; }
        if (_selfHandle != null) { NativeCallbackHelper.Free(_selfHandle); _selfHandle = null; }
        if (_app != null) { Saucer.saucer_application_free(_app); _app = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Defensive: Run() disposes this after the loop exits, but disposing here too (a no-op on a default
        // registration) ensures a token cancellation after Dispose can never call quit on a freed app (PAP-14).
        _quitRegistration.Dispose();
        _themeDetector?.Dispose();
        _localServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _rynWebView?.Dispose();
        _closeTcs.TrySetCanceled();
    }
}
