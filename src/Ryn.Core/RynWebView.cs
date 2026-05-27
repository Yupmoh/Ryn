using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Core;

public sealed class RynWebView : IRynWebView, IDisposable
{
    private const string AppScheme = "ryn";

    private static ReadOnlySpan<byte> BridgeScript =>
        """
        (function(){
          var ryn = window.__ryn = window.__ryn || {};
          var nextId = 1;
          var pending = {};
          var listeners = {};

          ryn.invoke = function(command, args) {
            return new Promise(function(resolve, reject) {
              var id = nextId++;
              pending[id] = { resolve: resolve, reject: reject };
              var body = args ? JSON.stringify(args) : '{}';
              var x = new XMLHttpRequest();
              x.open('POST', '/ipc/cmd/' + id + '/' + encodeURIComponent(command), true);
              x.send(body);
            });
          };

          ryn._resolve = function(id, ok, data) {
            var p = pending[id];
            if (!p) return;
            delete pending[id];
            if (ok) {
              try { p.resolve(JSON.parse(data)); } catch(e) { p.resolve(data); }
            } else {
              p.reject(new Error(data));
            }
          };

          ryn.on = function(event, callback) {
            if (!listeners[event]) listeners[event] = [];
            listeners[event].push(callback);
          };

          ryn.off = function(event, callback) {
            var list = listeners[event];
            if (!list) return;
            var idx = list.indexOf(callback);
            if (idx >= 0) list.splice(idx, 1);
          };

          ryn._emit = function(event, data) {
            var list = listeners[event];
            if (!list) return;
            for (var i = 0; i < list.length; i++) {
              try { list[i](data); } catch(e) { console.error('Ryn event error:', e); }
            }
          };

          ryn.eval = function(id, encoded) {
            try {
              var script = atob(encoded);
              var result = eval(script);
              Promise.resolve(result).then(
                function(v) { __ryn_send(id, 1, String(v)); },
                function(e) { __ryn_send(id, 0, String(e)); }
              );
            } catch(e) { __ryn_send(id, 0, String(e)); }
          };

          function __ryn_send(id, ok, data) {
            var x = new XMLHttpRequest();
            x.open('POST', '/ipc/eval/' + id + '/' + ok, true);
            x.send(data);
          }
        })();
        """u8;

    private nint _webview;
    private nint _app;
    private nint _selfHandle;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pendingEvals = new();
    private long _nextEvalId;

    private readonly ConcurrentDictionary<string, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>>> _schemeHandlers = new();

    private CommandDispatchHandler? _commandHandler;

    // HTML content to serve from ryn://app/
    private string? _htmlContent;

    private bool _disposed;

    internal unsafe RynWebView(saucer_webview* webview, saucer_application* app)
    {
        _webview = (nint)webview;
        _app = (nint)app;
        _selfHandle = (nint)NativeCallbackHelper.Alloc(this);

        RegisterAppScheme();
        InjectBridgeScript();
    }

    internal void SetCommandHandler(CommandDispatchHandler handler) => _commandHandler = handler;

    internal void SetHtmlContent(string html) => _htmlContent = html;

    internal unsafe void NavigateToAppScheme()
    {
        Span<byte> buf = stackalloc byte[64];
        var str = Utf8String.Create("ryn://app/index.html", buf);
        Saucer.saucer_webview_set_url_str((saucer_webview*)_webview, str.Pointer);
        str.Dispose();
    }

    public unsafe ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(url);

        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(url.AbsoluteUri, buf);
        Saucer.saucer_webview_set_url_str((saucer_webview*)_webview, str.Pointer);
        str.Dispose();

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Serve via app scheme to avoid null-origin CORS issues
        _htmlContent = html;
        NavigateToAppScheme();

        return ValueTask.CompletedTask;
    }

    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextEvalId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (_pendingEvals.TryRemove(id, out var removed))
                    removed.TrySetCanceled(cancellationToken);
            });
        }

        _pendingEvals[id] = tcs;

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        ExecuteOnUiThread($"window.__ryn.eval({id},'{base64}')");

        return new ValueTask<string>(tcs.Task);
    }

    public unsafe ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(script, buf);
        Saucer.saucer_webview_inject((saucer_webview*)_webview, str.Pointer, saucer_script_time.SAUCER_SCRIPT_TIME_READY, 0, 1);
        str.Dispose();

        return ValueTask.CompletedTask;
    }

    public unsafe void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _schemeHandlers[scheme] = handler;

        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(scheme, buf);
        Saucer.saucer_webview_handle_scheme(
            (saucer_webview*)_webview,
            str.Pointer,
            (delegate* unmanaged[Cdecl]<saucer_scheme_request*, saucer_scheme_executor*, void*, void>)&OnSchemeRequest,
            (void*)_selfHandle);
        str.Dispose();
    }

    public void EmitEvent(string eventName, string jsonData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventName);
        var escapedEvent = EscapeForJs(eventName);
        ExecuteOnUiThread($"window.__ryn._emit('{escapedEvent}',{jsonData})");
    }

    private unsafe void RegisterAppScheme()
    {
        Span<byte> buf = stackalloc byte[32];
        var str = Utf8String.Create(AppScheme, buf);

        Saucer.saucer_webview_handle_scheme(
            (saucer_webview*)_webview,
            str.Pointer,
            (delegate* unmanaged[Cdecl]<saucer_scheme_request*, saucer_scheme_executor*, void*, void>)&OnAppSchemeRequest,
            (void*)_selfHandle);

        str.Dispose();
    }

    private unsafe void InjectBridgeScript()
    {
        fixed (byte* ptr = BridgeScript)
        {
            Saucer.saucer_webview_inject(
                (saucer_webview*)_webview,
                (sbyte*)ptr,
                saucer_script_time.SAUCER_SCRIPT_TIME_CREATION,
                0,
                0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAppSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWebView>(userdata);
        self.HandleAppSchemeRequest(request, executor);
    }

    private unsafe void HandleAppSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor)
    {
        var url = Saucer.saucer_scheme_request_url(request);
        var path = SaucerStringReader.ReadUrlPath(url);
        Saucer.saucer_url_free(url);

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /ipc/eval/{id}/{ok} — JS eval response
        if (parts.Length >= 4 && parts[0] == "ipc" && parts[1] == "eval"
            && long.TryParse(parts[2], out var evalId)
            && int.TryParse(parts[3], out var ok))
        {
            var body = ReadRequestBody(request);

            if (_pendingEvals.TryRemove(evalId, out var tcs))
            {
                if (ok == 1)
                    tcs.TrySetResult(body);
                else
                    tcs.TrySetException(new JavaScriptException(body));
            }

            AcceptEmptyResponse(executor);
            return;
        }

        // /ipc/cmd/{id}/{command} — IPC command invocation
        if (parts.Length >= 4 && parts[0] == "ipc" && parts[1] == "cmd"
            && long.TryParse(parts[2], out var cmdId))
        {
            var command = Uri.UnescapeDataString(parts[3]);
            var body = ReadRequestBody(request);
            var args = Encoding.UTF8.GetBytes(body);

            AcceptEmptyResponse(executor);
            _ = DispatchCommandAsync(cmdId, command, args);
            return;
        }

        // Serve HTML content for /index.html or /
        if (_htmlContent is not null && (path is "/" or "/index.html" or ""))
        {
            var htmlBytes = Encoding.UTF8.GetBytes(_htmlContent);
            fixed (byte* ptr = htmlBytes)
            {
                var stash = Saucer.saucer_stash_new_from(ptr, (nuint)htmlBytes.Length);
                Span<byte> mimeBuf = stackalloc byte[16];
                var mime = Utf8String.Create("text/html", mimeBuf);
                var response = Saucer.saucer_scheme_response_new(stash, mime.Pointer);
                Saucer.saucer_scheme_executor_accept(executor, response);
                mime.Dispose();
            }
            return;
        }

        // 404 for everything else
        Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_NOT_FOUND);
    }

    private async Task DispatchCommandAsync(long id, string command, byte[] args)
    {
        if (_commandHandler is null) return;

        try
        {
            var result = await _commandHandler(command, args, CancellationToken.None)
                .ConfigureAwait(false);
            var escaped = EscapeForJs(result);
            ExecuteOnUiThread($"window.__ryn._resolve({id},true,'{escaped}')");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            var escaped = EscapeForJs(message);
            ExecuteOnUiThread($"window.__ryn._resolve({id},false,'{escaped}')");
        }
    }

    private unsafe void ExecuteOnUiThread(string js)
    {
        if (_app == 0 || _webview == 0) return;

        var webview = _webview;
        var app = _app;

        var callbackData = NativeCallbackHelper.Alloc((Action)(() =>
        {
            Span<byte> buf = stackalloc byte[256];
            var str = Utf8String.Create(js, buf);
            Saucer.saucer_webview_execute((saucer_webview*)webview, str.Pointer);
            str.Dispose();
        }));

        Saucer.saucer_application_post(
            (saucer_application*)app,
            (delegate* unmanaged[Cdecl]<void*, void>)&ExecutePostedAction,
            callbackData);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ExecutePostedAction(void* userdata)
    {
        var action = NativeCallbackHelper.Resolve<Action>(userdata);
        NativeCallbackHelper.Free(userdata);
        action();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWebView>(userdata);
        self.HandleUserSchemeRequest(request, executor);
    }

    private unsafe void HandleUserSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor)
    {
        var url = Saucer.saucer_scheme_request_url(request);
        var urlString = SaucerStringReader.ReadUrlString(url);
        var scheme = SaucerStringReader.ReadUrlScheme(url);
        Saucer.saucer_url_free(url);

        if (!_schemeHandlers.TryGetValue(scheme, out var handler))
        {
            Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_NOT_FOUND);
            return;
        }

        var method = SaucerStringReader.ReadRequestMethod(request);
        var body = ReadRequestBody(request);

        var rynRequest = new RynSchemeRequest(
            new Uri(urlString),
            method,
            Encoding.UTF8.GetBytes(body));

        var execCopy = (nint)Saucer.saucer_scheme_executor_copy(executor);
        _ = DispatchSchemeHandlerAsync(handler, rynRequest, execCopy);
    }

    private static async Task DispatchSchemeHandlerAsync(
        Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler,
        RynSchemeRequest request,
        nint executorHandle)
    {
        try
        {
            var rynResponse = await handler(request).ConfigureAwait(false);
            unsafe { AcceptSchemeResponse((saucer_scheme_executor*)executorHandle, rynResponse); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            unsafe { Saucer.saucer_scheme_executor_reject((saucer_scheme_executor*)executorHandle, saucer_scheme_error.SAUCER_SCHEME_ERROR_FAILED); }
        }
        finally
        {
            unsafe { Saucer.saucer_scheme_executor_free((saucer_scheme_executor*)executorHandle); }
        }
    }

    private static unsafe void AcceptSchemeResponse(saucer_scheme_executor* executor, RynSchemeResponse rynResponse)
    {
        saucer_stash* stash;
        if (rynResponse.Body.Length > 0)
        {
            fixed (byte* bodyPtr = rynResponse.Body.Span)
            {
                stash = Saucer.saucer_stash_new_from(bodyPtr, (nuint)rynResponse.Body.Length);
            }
        }
        else
        {
            stash = Saucer.saucer_stash_new_empty();
        }

        Span<byte> mimeBuf = stackalloc byte[256];
        var mime = Utf8String.Create(rynResponse.ContentType, mimeBuf);
        var response = Saucer.saucer_scheme_response_new(stash, mime.Pointer);
        Saucer.saucer_scheme_response_set_status(response, rynResponse.StatusCode);
        AppendCorsHeaders(response);
        Saucer.saucer_scheme_executor_accept(executor, response);
        mime.Dispose();
    }

    private static unsafe void AcceptEmptyResponse(saucer_scheme_executor* executor)
    {
        var emptyStash = Saucer.saucer_stash_new_empty();
        Span<byte> mimeBuf = stackalloc byte[16];
        var mime = Utf8String.Create("text/plain", mimeBuf);
        var response = Saucer.saucer_scheme_response_new(emptyStash, mime.Pointer);
        AppendCorsHeaders(response);
        Saucer.saucer_scheme_executor_accept(executor, response);
        mime.Dispose();
    }

    private static unsafe void AppendCorsHeaders(saucer_scheme_response* response)
    {
        Span<byte> hdrBuf = stackalloc byte[64];
        Span<byte> valBuf = stackalloc byte[8];

        var hdr = Utf8String.Create("Access-Control-Allow-Origin", hdrBuf);
        var val = Utf8String.Create("*", valBuf);
        Saucer.saucer_scheme_response_append_header(response, hdr.Pointer, val.Pointer);
        hdr.Dispose();
        val.Dispose();
    }

    private static unsafe string ReadRequestBody(saucer_scheme_request* request)
    {
        var stash = Saucer.saucer_scheme_request_content(request);
        if (stash == null) return string.Empty;

        var size = Saucer.saucer_stash_size(stash);
        if (size == 0) return string.Empty;

        var data = Saucer.saucer_stash_data(stash);
        return Encoding.UTF8.GetString(data, (int)size);
    }

    private static string EscapeForJs(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal);

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _pendingEvals)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingEvals.Clear();

        if (_selfHandle != 0)
        {
            NativeCallbackHelper.Free(_selfHandle);
            _selfHandle = 0;
        }

        _webview = 0;
        _app = 0;
    }
}

public sealed class JavaScriptException : Exception
{
    public JavaScriptException() { }
    public JavaScriptException(string message) : base(message) { }
    public JavaScriptException(string message, Exception innerException) : base(message, innerException) { }
}
