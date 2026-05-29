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
    {
        RunOnUi(() => { if (_window != null) Saucer.saucer_window_show(_window); });
        return ValueTask.CompletedTask;
    }

    public ValueTask HideAsync(CancellationToken cancellationToken = default)
    {
        RunOnUi(() => { if (_window != null) Saucer.saucer_window_hide(_window); });
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        RunOnUi(() => { if (_window != null) Saucer.saucer_window_close(_window); });
        return ValueTask.CompletedTask;
    }

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => _closeTcs.TrySetCanceled(cancellationToken));
        return new ValueTask(_closeTcs.Task);
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        _rynWebView?.NavigateAsync(url, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _rynWebView?.EvaluateJavaScriptAsync(script, cancellationToken) ?? new ValueTask<string>(string.Empty);

    public void Close() => RunOnUi(() => { if (_window != null) Saucer.saucer_window_close(_window); });

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

        var data = NativeCallbackHelper.Alloc(action);
        Saucer.saucer_application_post(
            _app,
            (delegate* unmanaged[Cdecl]<void*, void>)&RunPostedWindowAction,
            data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RunPostedWindowAction(void* userdata)
    {
        var action = NativeCallbackHelper.Resolve<Action>(userdata);
        NativeCallbackHelper.Free(userdata);
        action();
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
            cancellationToken.Register(() => { if (_app != null) Saucer.saucer_application_quit(_app); });
        _selfHandle = NativeCallbackHelper.Alloc(this);
        var exitCode = Saucer.saucer_application_run(_app,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnReady,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnFinish,
            _selfHandle);
        _ = exitCode;
        DisposeNative();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self.InitializeNative();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFinish(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self._closeTcs.TrySetResult();
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
        var webviewOpts = Saucer.saucer_webview_options_new(_window);
        _webview = Saucer.saucer_webview_new(webviewOpts, &error);
        Saucer.saucer_webview_options_free(webviewOpts);
        if (_webview == null) throw new InvalidOperationException($"Failed to create saucer webview (error code: {error})");
        ApplyWindowOptions();
        int ix, iy;
        Saucer.saucer_window_position(_window, &ix, &iy);
        _cachedX = ix;
        _cachedY = iy;
        _rynWebView = new RynWebView(_webview, _app);
        if (_commandHandler is not null) _rynWebView.SetCommandHandler(_commandHandler);
        if (_options.AllowedOrigins.Count > 0) _rynWebView.SetAllowedOrigins(_options.AllowedOrigins.ToList());
        else if (_options.Url is not null) _rynWebView.SetAllowedOrigins([_options.Url.GetLeftPart(UriPartial.Authority)]);
        if (_options.DevTools) _rynWebView.InjectConsoleForwardScript();
        _rynWebView.InjectFileDropScript();
        if (_options.TitleBarStyle is TitleBarStyle.Hidden or TitleBarStyle.Overlay) InjectTitleBarInsets();

        _themeDetector = new SystemThemeDetector();
        _themeDetector.ThemeChanged += t =>
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs { Theme = t });
        _themeDetector.StartPolling(TimeSpan.FromSeconds(2));

        if (_options.PersistWindowState)
        {
            _statePersistence = new WindowStatePersistence(_options.ApplicationId);
            var state = _statePersistence.Load();
            if (state is not null)
            {
                Saucer.saucer_window_set_size(_window, state.Width, state.Height);
                _cachedWidth = state.Width;
                _cachedHeight = state.Height;
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
                _localServer = new LocalWebServer(contentDirectory: null, useHttps: false, allowedCorsOrigin: devOrigin);
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
            _localServer = new LocalWebServer(_options.ContentDirectory, _options.UseHttps);
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
    }

    private void ApplyWindowOptions()
    {
        Span<byte> buf = stackalloc byte[256];
        var titleStr = Utf8String.Create(_options.Title, buf);
        Saucer.saucer_window_set_title(_window, titleStr.Pointer);
        titleStr.Dispose();
        Saucer.saucer_window_set_size(_window, _options.Width, _options.Height);
        Saucer.saucer_window_set_resizable(_window, (byte)(_options.Resizable ? 1 : 0));
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
        if (_options.DevTools) { Saucer.saucer_webview_set_dev_tools(_webview, 1); Saucer.saucer_webview_set_context_menu(_webview, 1); }
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
        if (size == 0 || size > 64) return (70, 28);
        Span<byte> buf = stackalloc byte[(int)size];
        fixed (byte* ptr = buf) { Saucer.saucer_window_native(_window, 0, ptr, &size); var nsWindow = System.Runtime.InteropServices.MemoryMarshal.Read<nint>(buf); if (nsWindow != 0) return MacOsTitleBar.GetTrafficLightInsets(nsWindow); }
        return (70, 28);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void ApplyMacOsTitleBar(bool overlay)
    {
        nuint size; System.Runtime.CompilerServices.Unsafe.SkipInit(out size);
        Saucer.saucer_window_native(_window, 0, null, &size);
        if (size == 0 || size > 64) return;
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
        var args = new WindowClosingEventArgs();
        self.Closing?.Invoke(self, args);
        if (args.Cancel) { self._rynWebView?.EmitEvent("window.closeCancelled", "{}"); return saucer_policy.SAUCER_POLICY_BLOCK; }
        return saucer_policy.SAUCER_POLICY_ALLOW;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowClosed(saucer_window* window, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self._statePersistence?.Save(new WindowStateData
        {
            Width = Volatile.Read(ref self._cachedWidth),
            Height = Volatile.Read(ref self._cachedHeight),
            X = Volatile.Read(ref self._cachedX),
            Y = Volatile.Read(ref self._cachedY),
        });
        self.Closed?.Invoke(self, EventArgs.Empty);
        self._closeTcs.TrySetResult();
        Saucer.saucer_application_quit(self._app);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowResize(saucer_window* window, int w, int h, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self._cachedWidth = w;
        self._cachedHeight = h;
        self.Resized?.Invoke(self, new WindowResizedEventArgs { Width = w, Height = h });
        self._rynWebView?.EmitEvent("window.resized", $"{{\"width\":{w},\"height\":{h}}}");
        self.CheckPositionChanged(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowFocus(saucer_window* window, byte focused, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        if (focused != 0) { self.Focused?.Invoke(self, EventArgs.Empty); self._rynWebView?.EmitEvent("window.focused", "{}"); self.CheckPositionChanged(window); }
        else { self.Blurred?.Invoke(self, EventArgs.Empty); self._rynWebView?.EmitEvent("window.blurred", "{}"); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowMaximize(saucer_window* window, byte maximized, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        var state = maximized != 0 ? WindowState.Maximized : WindowState.Normal;
        self.StateChanged?.Invoke(self, new WindowStateChangedEventArgs { State = state });
        var stateName = state == WindowState.Maximized ? "maximized" : "normal";
        self._rynWebView?.EmitEvent("window.stateChanged", $"{{\"state\":\"{stateName}\"}}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowMinimize(saucer_window* window, byte minimized, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        var state = minimized != 0 ? WindowState.Minimized : WindowState.Normal;
        self.StateChanged?.Invoke(self, new WindowStateChangedEventArgs { State = state });
        var stateName = state == WindowState.Minimized ? "minimized" : "normal";
        self._rynWebView?.EmitEvent("window.stateChanged", $"{{\"state\":\"{stateName}\"}}");
    }

    private void CheckPositionChanged(saucer_window* window)
    {
        int x, y;
        Saucer.saucer_window_position(window, &x, &y);
        var prevX = _cachedX; var prevY = _cachedY;
        _cachedX = x; _cachedY = y;
        if (prevX != x || prevY != y) { Moved?.Invoke(this, new WindowMovedEventArgs { X = x, Y = y }); _rynWebView?.EmitEvent("window.moved", $"{{\"x\":{x},\"y\":{y}}}"); }
    }

    private void DisposeNative()
    {
        if (_localServer is not null) { _localServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _localServer = null; }
        _rynWebView?.Dispose(); _rynWebView = null;
        if (_webview != null) { Saucer.saucer_webview_free(_webview); _webview = null; }
        if (_window != null) { Saucer.saucer_window_free(_window); _window = null; }
        if (_selfHandle != null) { NativeCallbackHelper.Free(_selfHandle); _selfHandle = null; }
        if (_app != null) { Saucer.saucer_application_free(_app); _app = null; }
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
