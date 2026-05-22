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

    private CommandDispatchHandler? _commandHandler;

    private string _cachedTitle;
    private int _cachedWidth;
    private int _cachedHeight;
    private bool _cachedResizable;
    private bool _disposed;

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
            if (_window != null)
            {
                Span<byte> buf = stackalloc byte[256];
                var str = Utf8String.Create(value, buf);
                Saucer.saucer_window_set_title(_window, str.Pointer);
                str.Dispose();
            }
        }
    }

    public int Width
    {
        get => _cachedWidth;
        set
        {
            _cachedWidth = value;
            if (_window != null)
                Saucer.saucer_window_set_size(_window, value, _cachedHeight);
        }
    }

    public int Height
    {
        get => _cachedHeight;
        set
        {
            _cachedHeight = value;
            if (_window != null)
                Saucer.saucer_window_set_size(_window, _cachedWidth, value);
        }
    }

    public bool Resizable
    {
        get => _cachedResizable;
        set
        {
            _cachedResizable = value;
            if (_window != null)
                Saucer.saucer_window_set_resizable(_window, (byte)(value ? 1 : 0));
        }
    }

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
    {
        if (_window != null)
            Saucer.saucer_window_show(_window);
        return ValueTask.CompletedTask;
    }

    public ValueTask HideAsync(CancellationToken cancellationToken = default)
    {
        if (_window != null)
            Saucer.saucer_window_hide(_window);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_window != null)
            Saucer.saucer_window_close(_window);
        return ValueTask.CompletedTask;
    }

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => _closeTcs.TrySetCanceled(cancellationToken));
        }
        return new ValueTask(_closeTcs.Task);
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) =>
        _rynWebView?.NavigateAsync(url, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _rynWebView?.EvaluateJavaScriptAsync(script, cancellationToken)
            ?? new ValueTask<string>(string.Empty);

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

        if (_app == null)
            throw new InvalidOperationException($"Failed to create saucer application (error code: {error})");

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (_app != null)
                    Saucer.saucer_application_quit(_app);
            });
        }

        _selfHandle = NativeCallbackHelper.Alloc(this);

        var exitCode = Saucer.saucer_application_run(
            _app,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnReady,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnFinish,
            _selfHandle);
        _ = exitCode;

        // After run returns, clean up native resources
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
        if (_window == null)
            throw new InvalidOperationException($"Failed to create saucer window (error code: {error})");

        // Register ryn:// scheme before webview creation (used for both content and IPC)
        Span<byte> schemeBuf = stackalloc byte[32];
        var schemeStr = Utf8String.Create("ryn", schemeBuf);
        Saucer.saucer_webview_register_scheme(schemeStr.Pointer);
        schemeStr.Dispose();

        var webviewOpts = Saucer.saucer_webview_options_new(_window);
        _webview = Saucer.saucer_webview_new(webviewOpts, &error);
        Saucer.saucer_webview_options_free(webviewOpts);

        if (_webview == null)
            throw new InvalidOperationException($"Failed to create saucer webview (error code: {error})");

        // Apply options
        ApplyWindowOptions();

        // Create managed webview wrapper
        _rynWebView = new RynWebView(_webview, _app);

        // Wire command dispatcher if configured
        if (_commandHandler is not null)
            _rynWebView.SetCommandHandler(_commandHandler);

        // Subscribe to window events
        SubscribeWindowEvents();

        // Navigate to initial content
        if (_options.Url != null)
        {
            Span<byte> urlBuf = stackalloc byte[256];
            var urlStr = Utf8String.Create(_options.Url.AbsoluteUri, urlBuf);
            Saucer.saucer_webview_set_url_str(_webview, urlStr.Pointer);
            urlStr.Dispose();
        }
        else if (_options.Html != null)
        {
            // Serve HTML via the ryn:// scheme handler (same origin as IPC, no CORS issues)
            _rynWebView.SetHtmlContent(_options.Html);
            _rynWebView.NavigateToAppScheme();
        }

        // Notify that native resources are ready
        OnNativeReady?.Invoke((nint)_app);

        // Show the window
        Saucer.saucer_window_show(_window);
    }

    private void ApplyWindowOptions()
    {
        // Title
        Span<byte> buf = stackalloc byte[256];
        var titleStr = Utf8String.Create(_options.Title, buf);
        Saucer.saucer_window_set_title(_window, titleStr.Pointer);
        titleStr.Dispose();

        // Size
        Saucer.saucer_window_set_size(_window, _options.Width, _options.Height);

        // Resizable
        Saucer.saucer_window_set_resizable(_window, (byte)(_options.Resizable ? 1 : 0));

        // Decorations (frameless)
        if (_options.Frameless)
        {
            Saucer.saucer_window_set_decorations(_window, saucer_window_decoration.SAUCER_WINDOW_DECORATION_NONE);
        }

        // Dev tools
        if (_options.DevTools)
        {
            Saucer.saucer_webview_set_dev_tools(_webview, 1);
            Saucer.saucer_webview_set_context_menu(_webview, 1);
        }
    }

    private void SubscribeWindowEvents()
    {
        // CLOSE event — allow close
        Saucer.saucer_window_on(
            _window,
            saucer_window_event.SAUCER_WINDOW_EVENT_CLOSE,
            (void*)(delegate* unmanaged[Cdecl]<saucer_window*, void*, saucer_policy>)&OnWindowClose,
            1,
            _selfHandle);

        // CLOSED event — signal TCS and quit
        Saucer.saucer_window_on(
            _window,
            saucer_window_event.SAUCER_WINDOW_EVENT_CLOSED,
            (void*)(delegate* unmanaged[Cdecl]<saucer_window*, void*, void>)&OnWindowClosed,
            1,
            _selfHandle);

        // RESIZE event — update cached size
        Saucer.saucer_window_on(
            _window,
            saucer_window_event.SAUCER_WINDOW_EVENT_RESIZE,
            (void*)(delegate* unmanaged[Cdecl]<saucer_window*, int, int, void*, void>)&OnWindowResize,
            1,
            _selfHandle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static saucer_policy OnWindowClose(saucer_window* window, void* userdata)
    {
        return saucer_policy.SAUCER_POLICY_ALLOW;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowClosed(saucer_window* window, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self._closeTcs.TrySetResult();
        Saucer.saucer_application_quit(self._app);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowResize(saucer_window* window, int w, int h, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWindow>(userdata);
        self._cachedWidth = w;
        self._cachedHeight = h;
    }

    private void DisposeNative()
    {
        _rynWebView?.Dispose();
        _rynWebView = null;

        if (_webview != null)
        {
            Saucer.saucer_webview_free(_webview);
            _webview = null;
        }

        if (_window != null)
        {
            Saucer.saucer_window_free(_window);
            _window = null;
        }

        if (_selfHandle != null)
        {
            NativeCallbackHelper.Free(_selfHandle);
            _selfHandle = null;
        }

        if (_app != null)
        {
            Saucer.saucer_application_free(_app);
            _app = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rynWebView?.Dispose();
        _closeTcs.TrySetCanceled();
    }
}
