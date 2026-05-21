using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Core;

public sealed class RynWebView : IRynWebView, IDisposable
{
    private const string IpcScheme = "ryn-ipc";

    private static ReadOnlySpan<byte> BridgeScript =>
        """
        (function(){
          window.__ryn = window.__ryn || {};
          window.__ryn.eval = function(id, encoded) {
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
            x.open('POST', 'ryn-ipc://eval/' + id + '/' + ok, true);
            x.send(data);
          }
        })();
        """u8;

    private nint _webview;
    private nint _selfHandle;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pendingEvals = new();
    private long _nextEvalId;

    private readonly ConcurrentDictionary<string, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>>> _schemeHandlers = new();

    private bool _disposed;

    internal unsafe RynWebView(saucer_webview* webview)
    {
        _webview = (nint)webview;
        _selfHandle = (nint)NativeCallbackHelper.Alloc(this);

        RegisterIpcScheme();
        InjectBridgeScript();
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

        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(html, buf);
        Saucer.saucer_webview_set_html((saucer_webview*)_webview, str.Pointer);
        str.Dispose();

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
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
        var evalCall = $"window.__ryn.eval({id},'{base64}')";

        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(evalCall, buf);
        Saucer.saucer_webview_execute((saucer_webview*)_webview, str.Pointer);
        str.Dispose();

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

    private unsafe void RegisterIpcScheme()
    {
        Span<byte> buf = stackalloc byte[32];
        var str = Utf8String.Create(IpcScheme, buf);

        Saucer.saucer_webview_handle_scheme(
            (saucer_webview*)_webview,
            str.Pointer,
            (delegate* unmanaged[Cdecl]<saucer_scheme_request*, saucer_scheme_executor*, void*, void>)&OnIpcSchemeRequest,
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
    private static unsafe void OnIpcSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<RynWebView>(userdata);
        self.HandleIpcRequest(request, executor);
    }

    private unsafe void HandleIpcRequest(saucer_scheme_request* request, saucer_scheme_executor* executor)
    {
        var url = Saucer.saucer_scheme_request_url(request);
        var path = SaucerStringReader.ReadUrlPath(url);
        Saucer.saucer_url_free(url);

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "eval"
            && long.TryParse(parts[1], out var evalId)
            && int.TryParse(parts[2], out var ok))
        {
            var body = ReadRequestBody(request);

            if (_pendingEvals.TryRemove(evalId, out var tcs))
            {
                if (ok == 1)
                    tcs.TrySetResult(body);
                else
                    tcs.TrySetException(new JavaScriptException(body));
            }
        }

        AcceptEmptyResponse(executor);
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
        Saucer.saucer_scheme_executor_accept(executor, response);
        mime.Dispose();
    }

    private static unsafe void AcceptEmptyResponse(saucer_scheme_executor* executor)
    {
        var emptyStash = Saucer.saucer_stash_new_empty();
        Span<byte> mimeBuf = stackalloc byte[16];
        var mime = Utf8String.Create("text/plain", mimeBuf);
        var response = Saucer.saucer_scheme_response_new(emptyStash, mime.Pointer);
        Saucer.saucer_scheme_executor_accept(executor, response);
        mime.Dispose();
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
    }
}

public sealed class JavaScriptException : Exception
{
    public JavaScriptException() { }
    public JavaScriptException(string message) : base(message) { }
    public JavaScriptException(string message, Exception innerException) : base(message, innerException) { }
}
