using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Core;

public sealed unsafe class RynWindow : IRynWindow, IDisposable
{
    // The application host that owns the native saucer_application, the run loop, and the UI-thread
    // marshalling. Per-window operations delegate their post/quit/scheme-registration through it. Null only
    // for a standalone test instance constructed without a host (no native work is ever issued in that case).
    private readonly NativeAppHost? _host;
    private readonly RynOptions _options;
    private readonly TaskCompletionSource _closeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>Constructs a standalone window with no application host. Native operations are inert; used by
    /// tests that exercise the managed surface (events, accessor wiring) without a running event loop.</summary>
    internal RynWindow(RynOptions options) : this(host: null, id: 0, options) { }

    internal RynWindow(NativeAppHost? host, int id, RynOptions options)
    {
        _host = host;
        Id = id;
        _options = options;
        _cachedTitle = options.Title;
        _cachedWidth = options.Width;
        _cachedHeight = options.Height;
        _cachedResizable = options.Resizable;
    }

    /// <summary>Stable per-application window identifier, assigned by the host when the window is created.</summary>
    public int Id { get; }

    internal void SetCommandHandler(CommandDispatchHandler handler) => _commandHandler = handler;

    /// <summary>Completes the window's close signal so <see cref="WaitForCloseAsync"/> awaiters unblock. Used
    /// by the host's OnFinish/startup-failure paths to defensively release a still-open waiter.</summary>
    internal void CompleteCloseSignal() => _closeTcs.TrySetResult();

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
        else _host?.Quit();
    });

    /// <summary>Closes the native window. Must be called on the UI thread (used by host-driven shutdown). A
    /// no-op before the native window exists.</summary>
    internal void CloseNativeWindow()
    {
        if (_window != null) Saucer.saucer_window_close(_window);
    }

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
    /// Marshals a native window operation onto saucer's UI thread via the application host. Native
    /// window/AppKit calls are not thread-safe, so mutating operations are posted to the application loop
    /// (a no-op deferral when already on the UI thread). Safe to call from any thread; inert without a host.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_disposed)
            return;
        _host?.RunOnUi(action);
    }

    /// <summary>
    /// Like <see cref="RunOnUi"/> but returns a Task that completes when the posted action has actually run
    /// on the UI thread (or faults if it throws) — so Show/Hide/CloseAsync genuinely await execution rather
    /// than returning a completed task immediately. Completes immediately if the loop isn't running.
    /// </summary>
    private Task RunOnUiAsync(Action action)
    {
        if (_disposed || _host is not { IsRunning: true })
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
    /// Runs <paramref name="action"/> on the UI thread, used by <see cref="IMainThreadDispatcher"/> to fence
    /// native UI calls (tray/audio AppKit work) made from worker threads (Cluster C / INT-02). Delegates to the
    /// host, which runs inline when already on the UI thread, queues it when the native app is not up yet, or
    /// posts via saucer otherwise. Safe to call from any thread. A no-op after disposal or without a host.
    /// </summary>
    internal void PostToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return;
        _host?.PostToUi(action);
    }

    /// <summary>
    /// Like <see cref="PostToUi"/> but returns a Task that completes when the action has run on the UI thread
    /// (or faults if it throws). Used by <see cref="IMainThreadDispatcher.InvokeAsync"/>. Completes without
    /// running if the window has been disposed or has no host.
    /// </summary>
    internal Task InvokeOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return Task.CompletedTask;
        return _host?.InvokeOnUiAsync(action) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Creates the native window and webview, applies options, wires events and content, and shows the window.
    /// Called by the host on the UI thread (inside OnReady) after the application exists. Throws on a native
    /// creation failure; the host's OnReady catch quits the loop and surfaces the exception.
    /// </summary>
    internal void InitializeNative()
    {
        var app = _host!.App;
        // Allocate the per-window GCHandle that backs this window's native event callbacks (CLOSE/CLOSED/…),
        // freed in DisposeNative. The host owns a separate handle for the app-level OnReady/OnFinish callbacks.
        _selfHandle = NativeCallbackHelper.Alloc(this);
        int error = 0;
        _window = Saucer.saucer_window_new(app, &error);
        if (_window == null) throw new InvalidOperationException($"Failed to create saucer window (error code: {error})");
        // Schemes are registered with the engine process-globally and must exist before the webview is created
        // (saucer silently no-ops handle_scheme for a scheme it wasn't told about pre-creation). The host
        // dedupes, so the reserved "ryn" scheme and any scheme shared across windows are registered once.
        _host.RegisterScheme("ryn");
        foreach (var customScheme in _options.CustomSchemes)
        {
            if (!string.Equals(customScheme, "ryn", StringComparison.OrdinalIgnoreCase))
                _host.RegisterScheme(customScheme);
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
        _rynWebView = new RynWebView(_webview, app);
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
        Saucer.saucer_window_show(_window);
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
            // Quit the loop only when the last window has closed (the host owns that decision); a multi-window
            // app keeps running while any other window is open. Replaces the old unconditional quit-on-any-close.
            self._host?.OnWindowClosedInternal(self);
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

    /// <summary>
    /// Frees this window's native handles after the saucer run loop returns: webview, then native window, then
    /// the per-window GCHandle. The host calls it for each window during its own teardown, then frees the
    /// application handle. The app-level posted-callback GCHandles are reclaimed by the host (INT-10).
    /// </summary>
    internal void DisposeNative()
    {
        if (_localServer is not null) { _localServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _localServer = null; }
        // Stop the theme poller deterministically once the saucer loop returns, rather than relying on the
        // public Dispose() or the finalizer backstop. This ends the per-tick child-process probe spawns
        // promptly at teardown (PAP-11/ARC-18). Dispose is idempotent, so a later Dispose() call is a harmless
        // no-op; nulling the field makes that explicit.
        _themeDetector?.Dispose(); _themeDetector = null;
        _rynWebView?.Dispose(); _rynWebView = null;
        if (_webview != null) { Saucer.saucer_webview_free(_webview); _webview = null; }
        if (_window != null) { Saucer.saucer_window_free(_window); _window = null; }
        if (_selfHandle != null) { NativeCallbackHelper.Free(_selfHandle); _selfHandle = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _themeDetector?.Dispose();
        _localServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _rynWebView?.Dispose();
        _closeTcs.TrySetCanceled();
    }
}
