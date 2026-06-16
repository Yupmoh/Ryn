using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Ryn.Core.Internal;
using Ryn.Interop;

namespace Ryn.Core;

public sealed class RynWebView : IRynWebView, Internal.ILocalServerHost, IDisposable
{
    private const string AppScheme = "ryn";

    internal static string GetBridgeScriptText() => BuildBridgeScript("TEST_TOKEN");
    internal static string GetConsoleForwardScriptText() => Encoding.UTF8.GetString(ConsoleForwardScript);

    /// <summary>
    /// Per-launch high-entropy token embedded in the JS bridge and required on every IPC request handled
    /// by the local web server, so another local process or a cross-origin page cannot drive IPC.
    /// </summary>
    private readonly string _ipcToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    string Internal.ILocalServerHost.IpcToken => _ipcToken;

    /// <summary>
    /// Builds the JS bridge. Command results are returned inline on the IPC response body (the XHR
    /// resolves from <c>onload</c>) rather than via a second <c>window.__ryn._resolve</c> eval hop —
    /// this removes a per-call UI-thread round trip and an extra escape pass.
    /// </summary>
    /// <remarks>
    /// The route prefixes (<c>/ipc/cmd/</c>, <c>/ipc/eval/</c>) and the token header (<c>X-Ryn-Token</c>)
    /// below are the page-side end of the IPC wire contract. Their canonical source is
    /// <see cref="Internal.IpcProtocol"/>; they cannot be interpolated into this raw-string template without
    /// breaking its <c>{{ }}</c> escaping, so this copy is kept in sync by hand. If a value in
    /// <see cref="Internal.IpcProtocol"/> changes, update the literals here (and the host-side parser) to match.
    /// </remarks>
    private static string BuildBridgeScript(string token) =>
        $$"""
        (function(){
          var ryn = window.__ryn = window.__ryn || {};
          var token = "{{token}}";
          var nextId = 1;
          var listeners = {};
          // IPC base URL. Empty = relative (same-origin scheme/local-server). Overridden to an absolute
          // loopback URL in dev-server (Vite) mode so window.__ryn.invoke reaches the Ryn backend.
          if (typeof ryn._ipcBase !== 'string') ryn._ipcBase = "";

          ryn.invoke = function(command, args) {
            return new Promise(function(resolve, reject) {
              var id = nextId++;
              var body = args ? JSON.stringify(args) : '{}';
              var x = new XMLHttpRequest();
              // Route prefix + header literals: canonical source is Ryn.Core.Internal.IpcProtocol (kept in sync by hand).
              x.open('POST', ryn._ipcBase + '/ipc/cmd/' + id + '/' + encodeURIComponent(command), true);
              x.setRequestHeader('Content-Type', 'application/json');
              if (token) x.setRequestHeader('X-Ryn-Token', token);
              var timer = setTimeout(function() {
                try { x.abort(); } catch (e) {}
                reject(new Error('IPC timeout: ' + command));
              }, 30000);
              x.onload = function() {
                clearTimeout(timer);
                if (x.status >= 200 && x.status < 300) {
                  if (x.responseText === '') { resolve(undefined); return; }
                  try { resolve(JSON.parse(x.responseText)); } catch (e) { resolve(x.responseText); }
                } else {
                  reject(new Error(x.responseText || ('IPC error ' + x.status + ': ' + command)));
                }
              };
              x.onerror = function() { clearTimeout(timer); reject(new Error('IPC network error: ' + command)); };
              x.send(body);
            });
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

          // Decodes a base64 string of UTF-8 bytes back to a JS string. atob() yields one latin-1 code
          // unit per byte, so multi-byte UTF-8 characters must be reassembled via TextDecoder — using
          // atob() alone corrupts every non-ASCII script (e.g. 'héllo' would mis-measure its length).
          function __ryn_b64utf8(b64) {
            var bin = atob(b64);
            var bytes = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
            return new TextDecoder('utf-8').decode(bytes);
          }

          // The result is returned as JSON (JSON.stringify) so the host can JSON.parse it: objects survive
          // round-trips, null/undefined are distinguishable, and there is no String() coercion. undefined
          // has no JSON form, so it is sent as the literal "null".
          function __ryn_result(v) {
            try { var s = JSON.stringify(v); return s === undefined ? 'null' : s; }
            catch (e) { return JSON.stringify(String(v)); }
          }

          ryn.eval = function(id, nonce, encoded) {
            try {
              var script = __ryn_b64utf8(encoded);
              var result = eval(script);
              Promise.resolve(result).then(
                function(v) { __ryn_send(id, nonce, 1, __ryn_result(v)); },
                function(e) { __ryn_send(id, nonce, 0, JSON.stringify(String(e))); }
              );
            } catch (e) { __ryn_send(id, nonce, 0, JSON.stringify(String(e))); }
          };

          // Path is /ipc/eval/{id}/{ok}/{nonce}: {ok} stays the third segment so the local-server transport
          // (which reads only id+ok) is unaffected, while the ryn:// scheme path additionally checks {nonce}.
          function __ryn_send(id, nonce, ok, data) {
            var x = new XMLHttpRequest();
            x.open('POST', ryn._ipcBase + '/ipc/eval/' + id + '/' + ok + '/' + encodeURIComponent(nonce), true);
            if (token) x.setRequestHeader('X-Ryn-Token', token);
            x.send(data);
          }
        })();
        """;

    private static ReadOnlySpan<byte> ConsoleForwardScript =>
        """
        (function(){
          // Only operate in the top frame. Never patch console / forward to IPC inside a cross-origin
          // iframe (e.g. a captcha widget), where the relative IPC POST would hit the wrong origin.
          if (window.top !== window.self) return;
          var c = window.console;
          var orig = { log: c.log, warn: c.warn, error: c.error, info: c.info };
          function fmt(args) {
            var parts = [];
            for (var i = 0; i < args.length; i++) {
              var a = args[i];
              if (a === null) { parts.push('null'); }
              else if (a === undefined) { parts.push('undefined'); }
              else if (typeof a === 'object') {
                try { parts.push(JSON.stringify(a)); } catch(e) { parts.push(String(a)); }
              } else { parts.push(String(a)); }
            }
            return parts.join(' ');
          }
          ['log','warn','error','info'].forEach(function(level) {
            c[level] = function() {
              orig[level].apply(c, arguments);
              try { window.__ryn.invoke('__ryn.console', { level: level, message: fmt(arguments) }); } catch(e) {}
            };
          });
        })();
        """u8;

    private static ReadOnlySpan<byte> FileDropScript =>
        """
        (function(){
          document.addEventListener('dragover', function(e) { e.preventDefault(); });
          document.addEventListener('drop', function(e) {
            e.preventDefault();
            var files = [];
            for (var i = 0; i < e.dataTransfer.files.length; i++) {
              files.push(e.dataTransfer.files[i].name);
            }
            if (files.length === 0) return;
            var data = { files: files, x: e.clientX, y: e.clientY };
            window.__ryn._emit('fileDrop', data);
            window.__ryn.invoke('__ryn.fileDrop', data);
          });
        })();
        """u8;

    private nint _webview;
    private nint _app;
    private nint _selfHandle;

    /// <summary>
    /// Pending host-initiated JS evals, keyed by id. Each carries a per-eval random nonce that the eval
    /// response must present (constant-time compare) before the result is accepted — so page script that
    /// has IPC reach cannot spoof an in-flight eval result by guessing the sequential id (IPC-04).
    /// </summary>
    private readonly ConcurrentDictionary<long, PendingEval> _pendingEvals = new();
    private long _nextEvalId;

    /// <summary>A pending host-initiated eval: the completion source plus the nonce gating its response.</summary>
    private sealed record PendingEval(TaskCompletionSource<string> Completion, string Nonce);

    /// <summary>
    /// Default ceiling on a host-initiated JS eval. Without it a navigation or a page that never answers
    /// would leave the awaiting <see cref="TaskCompletionSource{TResult}"/> hung forever (ARC-10). A caller
    /// can still pass a <see cref="CancellationToken"/> for a tighter bound.
    /// </summary>
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Outstanding GCHandles for actions posted to saucer's UI thread via <see cref="ExecuteOnUiThread"/>.
    /// Normally each is freed by <see cref="ExecutePostedAction"/> when saucer runs it, but that assumes
    /// saucer always drains posted callbacks. Tracking them lets <see cref="Dispose"/> reclaim any that were
    /// never executed (e.g. the app quit with callbacks still queued) instead of leaking them.
    /// </summary>
    private readonly ConcurrentDictionary<nint, byte> _postedCallbacks = new();

    private readonly ConcurrentDictionary<string, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>>> _schemeHandlers = new();

    private CommandDispatchHandler? _commandHandler;

    /// <inheritdoc />
    public event EventHandler<FileDropEventArgs>? FileDrop;

    // HTML content to serve from ryn://app/
    private string? _htmlContent;
    private string? _contentDirectory;
    private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase) { IpcProtocol.AppOrigin };

    /// <summary>
    /// Schemes the app declared up front (RynOptions.CustomSchemes), which the host registered with the
    /// engine via <c>saucer_webview_register_scheme</c> before the webview was created. <see cref="RegisterCustomScheme"/>
    /// only attaches handlers for these — saucer silently no-ops <c>handle_scheme</c> for an unregistered
    /// scheme, so attaching to an undeclared scheme would be a dead handler (ARC-02).
    /// </summary>
    private readonly HashSet<string> _declaredSchemes = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _disposed;

    /// <summary>
    /// Count of in-flight async scheme/command responses that hold a copied <c>saucer_scheme_executor</c>
    /// (INT-03). Disposal waits for these to drain before freeing <see cref="_selfHandle"/>, and each
    /// responder skips its native accept/free once <see cref="_disposed"/> is set, so a window closing or a
    /// dispose racing an in-flight command can never call accept/free on an executor whose webview is gone.
    /// </summary>
    private int _inFlightResponses;

    internal unsafe RynWebView(saucer_webview* webview, saucer_application* app)
    {
        _webview = (nint)webview;
        _app = (nint)app;
        _selfHandle = (nint)NativeCallbackHelper.Alloc(this);

        RegisterAppScheme();
        InjectBridgeScript();
    }

    internal void SetCommandHandler(CommandDispatchHandler handler) => _commandHandler = handler;

    Task<(bool Ok, string Data)> Internal.ILocalServerHost.DispatchCommandFromServerAsync(string command, string body)
        => ExecuteCommandAsync(command, Encoding.UTF8.GetBytes(body));

    void Internal.ILocalServerHost.HandleEvalFromServer(long evalId, int ok, string body)
    {
        // Local-server transport: already gated by the loopback + per-launch token + same-origin checks in
        // LocalWebServer.IsAuthorized, and the interface does not carry the eval nonce, so resolve by id here.
        if (_pendingEvals.TryRemove(evalId, out var pending))
            CompletePendingEval(pending, ok, body);
    }

    private static void CompletePendingEval(PendingEval pending, int ok, string body)
    {
        if (ok == 1)
            pending.Completion.TrySetResult(body);
        else
            pending.Completion.TrySetException(new JavaScriptException(body));
    }

    internal void SetAllowedOrigins(List<string> origins)
    {
        foreach (var origin in origins)
            _allowedOrigins.Add(origin);
    }

    /// <summary>
    /// Records the custom schemes the host registered with the engine (from <c>RynOptions.CustomSchemes</c>)
    /// before the webview was created. Only these may later be wired up via <see cref="RegisterCustomScheme"/>.
    /// The reserved <c>ryn</c> scheme is never accepted here — it backs the built-in IPC/content transport.
    /// </summary>
    internal void SetDeclaredSchemes(IEnumerable<string> schemes)
    {
        foreach (var scheme in schemes)
        {
            if (!string.Equals(scheme, AppScheme, StringComparison.OrdinalIgnoreCase))
                _declaredSchemes.Add(scheme);
        }
    }

    internal void SetHtmlContent(string html) => _htmlContent = html;

    internal void SetContentDirectory(string path) => _contentDirectory = Path.GetFullPath(path);

    internal unsafe void NavigateToAppScheme()
    {
        Span<byte> buf = stackalloc byte[64];
        var str = Utf8String.Create($"{IpcProtocol.AppOrigin}/index.html", buf);
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

    /// <summary>
    /// Evaluates a JavaScript expression in the page and returns its result. The script is transported as
    /// base64 of its UTF-8 bytes and decoded page-side with <c>TextDecoder</c>, so non-ASCII scripts are not
    /// corrupted. The result is returned as a JSON document (the page <c>JSON.stringify</c>s it): a string
    /// result comes back as a quoted JSON string, objects/arrays/numbers/booleans as their JSON form, and
    /// <c>undefined</c> as <c>"null"</c>. Callers that want the raw value should <c>JSON.parse</c> it. The
    /// eval is bounded by a default 30s timeout (and any supplied <paramref name="cancellationToken"/>).
    /// </summary>
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(script);

        var id = Interlocked.Increment(ref _nextEvalId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        _pendingEvals[id] = new PendingEval(tcs, nonce);

        // Bound the wait so a navigation (which abandons the in-flight eval) or an unresponsive page cannot
        // hang the awaiter forever. The caller's token, if any, is linked in so either can cancel. Ownership of
        // the CTS is transferred to the completion continuation below, which disposes it once the eval settles.
#pragma warning disable CA2000 // Disposed in the tcs.Task continuation; the eval always settles (result/fault/cancel/timeout).
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#pragma warning restore CA2000
        timeoutCts.CancelAfter(EvalTimeout);
        var registration = timeoutCts.Token.Register(() =>
        {
            if (_pendingEvals.TryRemove(id, out var removed))
            {
                if (cancellationToken.IsCancellationRequested)
                    removed.Completion.TrySetCanceled(cancellationToken);
                else
                    removed.Completion.TrySetException(new TimeoutException($"JavaScript eval timed out after {EvalTimeout.TotalSeconds:0}s."));
            }
        });
        // Release the registration + linked source once the eval settles (resolved, faulted, or cancelled),
        // so a long-lived token doesn't accumulate one registration per call.
        _ = tcs.Task.ContinueWith(
            static (_, state) =>
            {
                var (reg, cts) = ((CancellationTokenRegistration, CancellationTokenSource))state!;
                reg.Dispose();
                cts.Dispose();
            },
            (registration, timeoutCts), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        ExecuteOnUiThread($"window.__ryn.eval({id},'{nonce}','{base64}')");

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

    /// <summary>
    /// Attaches a handler for a custom URL scheme. The scheme must have been declared up front via
    /// <c>RynOptions.CustomSchemes</c> (registered with the engine before the webview was created) — saucer
    /// silently ignores <c>handle_scheme</c> for a scheme it was never told about pre-creation, so attaching
    /// to an undeclared scheme would install a handler that never fires (ARC-02). The reserved <c>ryn</c>
    /// scheme is rejected because it backs the built-in IPC and content transport.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="scheme"/> is <c>ryn</c> or was not declared in <c>RynOptions.CustomSchemes</c>.
    /// </exception>
    public unsafe void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(handler);

        if (string.Equals(scheme, AppScheme, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The 'ryn' scheme is reserved for Ryn's IPC and content transport and cannot be re-registered.",
                nameof(scheme));

        if (!_declaredSchemes.Contains(scheme))
            throw new ArgumentException(
                $"Scheme '{scheme}' was not declared. Add it to RynOptions.CustomSchemes so it is registered " +
                "with the engine before the webview is created; handlers cannot be attached to undeclared schemes.",
                nameof(scheme));

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
        ArgumentNullException.ThrowIfNull(jsonData);

        // Validate + canonicalize the payload so a caller-supplied string cannot inject script into the
        // emit call. Anything that isn't well-formed JSON is rejected rather than spliced in raw.
        string canonical;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonData);
            canonical = doc.RootElement.GetRawText();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException(
                "jsonData must be a valid JSON value. Use EmitEvent<T>(name, payload, typeInfo) to emit typed data safely.",
                nameof(jsonData), ex);
        }

        var escapedEvent = EscapeForJs(eventName);
        ExecuteOnUiThread($"window.__ryn._emit('{escapedEvent}',{canonical})");
    }

    /// <summary>
    /// Emits a strongly-typed event payload, serialized through a source-generated
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> (AOT-safe, injection-safe).
    /// Prefer this over the string overload.
    /// </summary>
    public void EmitEvent<T>(string eventName, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var json = System.Text.Json.JsonSerializer.Serialize(payload, typeInfo);
        var escapedEvent = EscapeForJs(eventName);
        ExecuteOnUiThread($"window.__ryn._emit('{escapedEvent}',{json})");
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
        var script = BuildBridgeScript(_ipcToken);
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(script, buf); // falls back to a pooled buffer for the full script
        Saucer.saucer_webview_inject(
            (saucer_webview*)_webview,
            str.Pointer,
            saucer_script_time.SAUCER_SCRIPT_TIME_CREATION,
            1, // no_frames: main frame only — never inject the Ryn bridge into cross-origin iframes (e.g. captcha widgets)
            0);
        str.Dispose();
    }

    /// <summary>
    /// Points the JS bridge's IPC calls at an absolute loopback URL (the Ryn IPC server) instead of the
    /// current page origin. Used in dev-server (e.g. Vite) mode so <c>window.__ryn.invoke</c> works when the
    /// UI is served by a separate dev server. Injected at document-creation time, after the bridge.
    /// </summary>
    internal unsafe void SetIpcBaseOverride(string absoluteBaseUrl)
    {
        var js = $"(function(){{window.__ryn=window.__ryn||{{}};window.__ryn._ipcBase=\"{System.Text.Json.JsonEncodedText.Encode(absoluteBaseUrl)}\";}})();";
        InjectAtCreation(js);
    }

    /// <summary>Surfaces a developer-facing warning in the page console (visible in DevTools).</summary>
    internal unsafe void WarnInPageConsole(string message)
    {
        var js = $"console.warn(\"{System.Text.Json.JsonEncodedText.Encode(message)}\");";
        InjectAtCreation(js);
    }

    private unsafe void InjectAtCreation(string js)
    {
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(js, buf);
        Saucer.saucer_webview_inject(
            (saucer_webview*)_webview,
            str.Pointer,
            saucer_script_time.SAUCER_SCRIPT_TIME_CREATION,
            1, // no_frames: main frame only — never inject the Ryn bridge into cross-origin iframes (e.g. captcha widgets)
            0);
        str.Dispose();
    }

    internal unsafe void InjectConsoleForwardScript()
    {
        fixed (byte* ptr = ConsoleForwardScript)
        {
            Saucer.saucer_webview_inject(
                (saucer_webview*)_webview,
                (sbyte*)ptr,
                saucer_script_time.SAUCER_SCRIPT_TIME_CREATION,
                1, // no_frames: main frame only — keep framework scripts out of cross-origin iframes
                0);
        }
    }

    internal unsafe void InjectFileDropScript()
    {
        fixed (byte* ptr = FileDropScript)
        {
            Saucer.saucer_webview_inject(
                (saucer_webview*)_webview,
                (sbyte*)ptr,
                saucer_script_time.SAUCER_SCRIPT_TIME_CREATION,
                1, // no_frames: main frame only — keep framework scripts out of cross-origin iframes
                0);
        }
    }

    internal void RaiseFileDrop(FileDropEventArgs args) => FileDrop?.Invoke(this, args);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAppSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor, void* userdata)
    {
        // Route through NativeGuard so no managed exception can cross the native boundary (Contract A). The
        // native pointers are carried as nint because a lambda display class cannot hold pointer-typed fields.
        var req = (nint)request;
        var exec = (nint)executor;
        var data = (nint)userdata;
        NativeGuard.Invoke(nameof(OnAppSchemeRequest), () =>
        {
            var self = NativeCallbackHelper.Resolve<RynWebView>(data);
            self.HandleAppSchemeRequest((saucer_scheme_request*)req, (saucer_scheme_executor*)exec);
        });
    }

    private unsafe void HandleAppSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor)
    {
        var url = Saucer.saucer_scheme_request_url(request);
        var path = SaucerStringReader.ReadUrlPath(url);
        Saucer.saucer_url_free(url);

        // Read the headers once and pull out everything the guards need (origin + token).
        var headers = SaucerStringReader.ReadRequestHeaders(request);
        var requestOrigin = ParseOriginHeader(headers);
        var matchedOrigin = ResolveAllowedOrigin(requestOrigin);

        var method = SaucerStringReader.ReadRequestMethod(request);
        var isIpc = path.StartsWith(IpcProtocol.IpcPrefix, StringComparison.Ordinal);

        // CORS preflight — reject if origin is explicitly disallowed
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (matchedOrigin is null && requestOrigin is not null)
            {
                Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_FAILED);
                return;
            }
            AcceptCorsPreflightResponse(executor, matchedOrigin);
            return;
        }

        // IPC endpoints (cmd/eval) are privileged: enforce the per-launch token, require POST, and treat a
        // missing Origin as denied (IPC-01). The bridge always sends the token + POST, so legitimate calls
        // are unaffected; a no-Origin GET (e.g. an <img>/iframe probe) and any wrong/absent token are
        // rejected instead of being mapped onto the app origin. The token check is constant-time.
        if (isIpc)
        {
            var presentedToken = ParseHeaderValue(headers, IpcProtocol.TokenHeader);
            var authorized =
                requestOrigin is not null              // null Origin is denied for /ipc/
                && matchedOrigin is not null           // origin must be on the allowlist
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                && TokenMatches(presentedToken);
            if (!authorized)
            {
                Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_FAILED);
                return;
            }
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /ipc/eval/{id}/{ok}/{nonce} — JS eval response. The per-eval nonce (IPC-04) must match the one
        // issued for this id before the pending eval is resolved, so page script with IPC reach cannot
        // spoof a host-initiated eval result by guessing the sequential id.
        if (parts.Length >= 5 && parts[0] == "ipc" && parts[1] == "eval"
            && long.TryParse(parts[2], out var evalId)
            && int.TryParse(parts[3], out var ok))
        {
            var nonce = Uri.UnescapeDataString(parts[4]);
            var body = ReadRequestBody(request);

            if (_pendingEvals.TryGetValue(evalId, out var pending)
                && NonceMatches(pending.Nonce, nonce)
                && _pendingEvals.TryRemove(evalId, out pending))
            {
                CompletePendingEval(pending, ok, body);
            }

            AcceptEmptyResponse(executor, matchedOrigin);
            return;
        }

        // /ipc/cmd/{id}/{command} — IPC command invocation
        if (parts.Length >= 4 && parts[0] == "ipc" && parts[1] == "cmd"
            && long.TryParse(parts[2], out var cmdId))
        {
            var command = Uri.UnescapeDataString(parts[3]);
            var body = ReadRequestBody(request);
            var args = Encoding.UTF8.GetBytes(body);

            // Keep the executor alive across the async dispatch and deliver the result on the response body
            // itself (no second window.__ryn._resolve eval hop). The copy is refcounted (INT-03) so Dispose
            // waits for the response to drain before freeing native handles; if we're already disposing,
            // TryBeginNativeResponse rejects and returns false.
            if (TryBeginNativeResponse(executor, out var execCopy))
                _ = RespondToCommandAsync(command, args, execCopy, matchedOrigin);
            return;
        }

        // Serve static files from content directory
        if (_contentDirectory is not null)
        {
            var relativePath = (path is "/" or "") ? "index.html" : path.TrimStart('/');
            var filePath = Path.GetFullPath(Path.Combine(_contentDirectory, relativePath));

            // One canonical containment rule for the whole framework (PAP-23): exact-root OR
            // child-with-trailing-separator. Replaces a bare StartsWith(base + sep), which had no exact-root
            // branch and could be fooled by a sibling-prefix path (e.g. /content-evil vs /content).
            if (RynPath.IsContainedIn(filePath, _contentDirectory, RynPath.HostComparison) && File.Exists(filePath))
            {
                ServeFile(executor, filePath);
                return;
            }
        }

        // Serve inline HTML content for /index.html or /
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
                // accept() copies the response; we still own (and must free) both native objects.
                Saucer.saucer_scheme_response_free(response);
                Saucer.saucer_stash_free(stash);
                mime.Dispose();
            }
            return;
        }

        // 404 for everything else
        Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_NOT_FOUND);
    }

    // TODO(ARC-21, roadmap): this reads the whole file synchronously on saucer's UI thread and has no
    // Range/206 support, so large assets block the UI and media scrubbing (mp4/webm/mp3) over ryn:// does not
    // work. Move to the copied-executor async pattern used for commands (saucer_scheme_executor_copy + async
    // accept, refcounted via TryBeginNativeResponse/EndNativeResponse) and add Range handling. Deferred: it is
    // gated on the INT-03 executor-lifetime work landing first. Tracked in PLAN.md's performance backlog.
    private static unsafe void ServeFile(saucer_scheme_executor* executor, string filePath)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        var mimeType = GetMimeType(Path.GetExtension(filePath));

        fixed (byte* ptr = fileBytes)
        {
            var stash = Saucer.saucer_stash_new_from(ptr, (nuint)fileBytes.Length);
            Span<byte> mimeBuf = stackalloc byte[128];
            var mime = Utf8String.Create(mimeType, mimeBuf);
            var response = Saucer.saucer_scheme_response_new(stash, mime.Pointer);
            Saucer.saucer_scheme_executor_accept(executor, response);
            Saucer.saucer_scheme_response_free(response);
            Saucer.saucer_stash_free(stash);
            mime.Dispose();
        }
    }

    private static string GetMimeType(string extension)
    {
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)) return "text/html";
        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)) return "text/css";
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)) return "application/javascript";
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)) return "image/x-icon";
        if (extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)) return "font/woff";
        if (extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)) return "font/woff2";
        if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)) return "font/ttf";
        if (extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)) return "font/otf";
        if (extension.Equals(".wasm", StringComparison.OrdinalIgnoreCase)) return "application/wasm";
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) return "text/plain";
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return "application/xml";
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "application/pdf";
        return "application/octet-stream";
    }

    /// <summary>
    /// Executes an IPC command and returns its outcome. <c>data</c> is the raw JSON result on success, or
    /// the error message on failure. Both transports deliver this on the response body. The <c>Task.Run</c>
    /// hop intentionally moves command execution off saucer's native UI thread.
    /// </summary>
    internal async Task<(bool Ok, string Data)> ExecuteCommandAsync(string command, byte[] args)
    {
        if (command == "__ryn.fileDrop")
            return HandleFileDropInline(args);

        if (_commandHandler is null)
            return (true, "null");

        try
        {
            var result = await Task.Run(async () => await _commandHandler(command, args, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
            return (true, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not leak raw exception text to web content (IPC-03): an absolute path or plugin internal in
            // the message would be readable by hostile/XSS page script via the rejected invoke promise. The
            // full exception still reaches the host via the dispatcher's IIpcObserver.OnCommandFailed hook
            // (logged server-side). Keep the detailed message only in Debug builds to aid development.
#if DEBUG
            return (false, ex.InnerException?.Message ?? ex.Message);
#else
            return (false, "Command failed");
#endif
        }
    }

    /// <summary>
    /// Begins a refcounted, async native response: copies the executor so it survives the off-thread hop and
    /// records the in-flight op so <see cref="Dispose"/> drains it before freeing native handles (INT-03). If
    /// disposal already began, the request is rejected synchronously and <c>false</c> is returned so the
    /// caller skips the async path entirely.
    /// </summary>
    private unsafe bool TryBeginNativeResponse(saucer_scheme_executor* executor, out nint executorCopy)
    {
        // Publish the increment before re-checking _disposed; Dispose sets _disposed then reads the count, so
        // the two orderings can't both miss each other (either Dispose sees our increment and waits, or we see
        // _disposed and back out).
        Interlocked.Increment(ref _inFlightResponses);
        if (_disposed)
        {
            Interlocked.Decrement(ref _inFlightResponses);
            executorCopy = 0;
            Saucer.saucer_scheme_executor_reject(executor, saucer_scheme_error.SAUCER_SCHEME_ERROR_FAILED);
            return false;
        }

        executorCopy = (nint)Saucer.saucer_scheme_executor_copy(executor);
        return true;
    }

    private void EndNativeResponse() => Interlocked.Decrement(ref _inFlightResponses);

    private async Task RespondToCommandAsync(string command, byte[] args, nint executorHandle, string? origin)
    {
        try
        {
            var (ok, data) = await ExecuteCommandAsync(command, args).ConfigureAwait(false);
            // Skip the native write+free entirely if disposal began while the command ran: the webview that
            // backs this executor copy may already be freed, so accept/free would be a use-after-free (INT-03).
            if (!_disposed)
            {
                try
                {
                    unsafe { WriteCommandResponse((saucer_scheme_executor*)executorHandle, ok, data, origin); }
                }
                finally
                {
                    unsafe { Saucer.saucer_scheme_executor_free((saucer_scheme_executor*)executorHandle); }
                }
            }
        }
        finally
        {
            EndNativeResponse();
        }
    }

    private (bool Ok, string Data) HandleFileDropInline(byte[] args)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(args);
            var root = doc.RootElement;
            var files = new List<string>();
            if (root.TryGetProperty("files", out var filesEl))
            {
                foreach (var f in filesEl.EnumerateArray())
                    if (f.GetString() is { } name) files.Add(name);
            }
            var x = root.TryGetProperty("x", out var xEl) ? xEl.GetInt32() : 0;
            var y = root.TryGetProperty("y", out var yEl) ? yEl.GetInt32() : 0;
            RaiseFileDrop(new FileDropEventArgs { FileNames = files, X = x, Y = y });
            return (true, "null");
        }
        catch (System.Text.Json.JsonException)
        {
            return (false, "Invalid file drop data");
        }
    }

    private static unsafe void WriteCommandResponse(saucer_scheme_executor* executor, bool ok, string data, string? origin)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(data);
        saucer_stash* stash;
        if (bodyBytes.Length > 0)
        {
            fixed (byte* bodyPtr = bodyBytes)
                stash = Saucer.saucer_stash_new_from(bodyPtr, (nuint)bodyBytes.Length);
        }
        else
        {
            stash = Saucer.saucer_stash_new_empty();
        }

        Span<byte> mimeBuf = stackalloc byte[32];
        var mime = Utf8String.Create(ok ? "application/json" : "text/plain", mimeBuf);
        var response = Saucer.saucer_scheme_response_new(stash, mime.Pointer);
        Saucer.saucer_scheme_response_set_status(response, ok ? 200 : 500);
        AppendHeader(response, "Access-Control-Allow-Origin", origin ?? IpcProtocol.AppOrigin);
        AppendHeader(response, "Vary", "Origin");
        AppendHeader(response, "X-Content-Type-Options", "nosniff");
        Saucer.saucer_scheme_executor_accept(executor, response);
        Saucer.saucer_scheme_response_free(response);
        Saucer.saucer_stash_free(stash);
        mime.Dispose();
    }

    private unsafe void ExecuteOnUiThread(string js)
    {
        if (_disposed || _app == 0 || _webview == 0) return;

        var webview = _webview;
        var app = _app;

        var payload = new PostedAction(this, () =>
        {
            Span<byte> buf = stackalloc byte[256];
            var str = Utf8String.Create(js, buf);
            Saucer.saucer_webview_execute((saucer_webview*)webview, str.Pointer);
            str.Dispose();
        });
        var callbackData = (nint)NativeCallbackHelper.Alloc(payload);
        _postedCallbacks[callbackData] = 0;

        Saucer.saucer_application_post(
            (saucer_application*)app,
            (delegate* unmanaged[Cdecl]<void*, void>)&ExecutePostedAction,
            (void*)callbackData);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ExecutePostedAction(void* userdata)
    {
        var handle = (nint)userdata;
        // Route through NativeGuard (Contract A): a throw from the posted action (e.g. the JS-execute body)
        // must not unwind into saucer's native run loop and kill the process.
        NativeGuard.Invoke(nameof(ExecutePostedAction), () =>
        {
            // Safe to resolve: saucer only invokes posted callbacks while its run loop is alive, and Dispose
            // (which reclaims leftovers) runs only after that loop has stopped — the two never overlap.
            var payload = NativeCallbackHelper.Resolve<PostedAction>(handle);

            // Claim the handle so a Dispose racing at shutdown can't also free it; whoever removes it frees it.
            if (!payload.Owner._postedCallbacks.TryRemove(handle, out _))
                return;

            try { payload.Run(); }
            finally { NativeCallbackHelper.Free(handle); }
        });
    }

    /// <summary>Pairs an action posted to the UI thread with its owning webview, so the static native
    /// callback can deregister the GCHandle before freeing it.</summary>
    private sealed record PostedAction(RynWebView Owner, Action Run);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSchemeRequest(saucer_scheme_request* request, saucer_scheme_executor* executor, void* userdata)
    {
        var req = (nint)request;
        var exec = (nint)executor;
        var data = (nint)userdata;
        NativeGuard.Invoke(nameof(OnSchemeRequest), () =>
        {
            var self = NativeCallbackHelper.Resolve<RynWebView>(data);
            self.HandleUserSchemeRequest((saucer_scheme_request*)req, (saucer_scheme_executor*)exec);
        });
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
        var headers = SaucerStringReader.ReadRequestHeaders(request);
        var body = ReadRequestBody(request);

        // Scope the response ACAO to the request's allowed origin instead of "*" (IPC-05): a same-origin or
        // allowlisted origin gets reflected; anything else gets no ACAO header (engine default = same-origin).
        var matchedOrigin = ResolveAllowedOrigin(ParseOriginHeader(headers));

        var rynRequest = new RynSchemeRequest(
            new Uri(urlString),
            method,
            Encoding.UTF8.GetBytes(body));

        // Refcounted executor copy (INT-03): if disposal already began, reject and skip the async hop.
        if (TryBeginNativeResponse(executor, out var execCopy))
            _ = DispatchSchemeHandlerAsync(handler, rynRequest, execCopy, matchedOrigin);
    }

    private async Task DispatchSchemeHandlerAsync(
        Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler,
        RynSchemeRequest request,
        nint executorHandle,
        string? origin)
    {
        try
        {
            RynSchemeResponse rynResponse;
            try
            {
                rynResponse = await handler(request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_disposed)
                    unsafe { Saucer.saucer_scheme_executor_reject((saucer_scheme_executor*)executorHandle, saucer_scheme_error.SAUCER_SCHEME_ERROR_FAILED); }
                return;
            }

            // Skip the native accept entirely once disposal began — the backing webview may be gone (INT-03).
            if (!_disposed)
                unsafe { AcceptSchemeResponse((saucer_scheme_executor*)executorHandle, rynResponse, origin); }
        }
        finally
        {
            // The free pairs with saucer_scheme_executor_copy and is itself skipped once disposed, since the
            // copy's backing webview may already be gone; the leaked copy is harmless at process teardown.
            if (!_disposed)
                unsafe { Saucer.saucer_scheme_executor_free((saucer_scheme_executor*)executorHandle); }
            EndNativeResponse();
        }
    }

    private static unsafe void AcceptSchemeResponse(saucer_scheme_executor* executor, RynSchemeResponse rynResponse, string? origin)
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
        // Reflect ACAO only for an allowlisted/same-origin request (IPC-05); omit it otherwise so custom-scheme
        // data is not blanket-readable cross-origin. A null origin means same-origin/no CORS — no header needed.
        if (origin is not null)
        {
            AppendHeader(response, "Access-Control-Allow-Origin", origin);
            AppendHeader(response, "Vary", "Origin");
        }
        Saucer.saucer_scheme_executor_accept(executor, response);
        Saucer.saucer_scheme_response_free(response);
        Saucer.saucer_stash_free(stash);
        mime.Dispose();
    }

    private unsafe void AcceptEmptyResponse(saucer_scheme_executor* executor, string? origin = null)
    {
        var emptyStash = Saucer.saucer_stash_new_empty();
        Span<byte> mimeBuf = stackalloc byte[16];
        var mime = Utf8String.Create("text/plain", mimeBuf);
        var response = Saucer.saucer_scheme_response_new(emptyStash, mime.Pointer);
        AppendCorsHeaders(response, origin);
        Saucer.saucer_scheme_executor_accept(executor, response);
        Saucer.saucer_scheme_response_free(response);
        Saucer.saucer_stash_free(emptyStash);
        mime.Dispose();
    }

    private unsafe void AcceptCorsPreflightResponse(saucer_scheme_executor* executor, string? matchedOrigin)
    {
        var emptyStash = Saucer.saucer_stash_new_empty();
        Span<byte> mimeBuf = stackalloc byte[16];
        var mime = Utf8String.Create("text/plain", mimeBuf);
        var response = Saucer.saucer_scheme_response_new(emptyStash, mime.Pointer);
        AppendCorsHeaders(response, matchedOrigin);
        AppendHeader(response, "Access-Control-Allow-Methods", "POST, OPTIONS");
        AppendHeader(response, "Access-Control-Allow-Headers", "Content-Type, X-Ryn-Token");
        Saucer.saucer_scheme_executor_accept(executor, response);
        Saucer.saucer_scheme_response_free(response);
        Saucer.saucer_stash_free(emptyStash);
        mime.Dispose();
    }

    private static unsafe void AppendCorsHeaders(saucer_scheme_response* response, string? origin)
    {
        AppendHeader(response, "Access-Control-Allow-Origin", origin ?? IpcProtocol.AppOrigin);
        AppendHeader(response, "Vary", "Origin");
    }

    /// <summary>
    /// Extracts the request Origin from the raw header blob, normalizing an absent/empty/"null" Origin to
    /// <c>null</c>. A null result means "no usable origin" — for /ipc/ that is treated as <em>denied</em>.
    /// </summary>
    private static string? ParseOriginHeader(string headers)
    {
        var value = ParseHeaderValue(headers, "Origin");
        if (value is null || value.Length == 0 || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        return value;
    }

    /// <summary>Reads a single header value out of saucer's newline-separated <c>Name: value</c> header blob.</summary>
    private static string? ParseHeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim().TrimEnd('\r');
        }
        return null;
    }

    /// <summary>Constant-time comparison of a presented IPC token against the per-launch token (IPC-01).</summary>
    private bool TokenMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_ipcToken));
    }

    /// <summary>Constant-time comparison of a presented eval nonce against the one issued for that id (IPC-04).</summary>
    private static bool NonceMatches(string expected, string presented)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    private string? ResolveAllowedOrigin(string? requestOrigin)
    {
        if (requestOrigin is null)
            return IpcProtocol.AppOrigin;
        return _allowedOrigins.Contains(requestOrigin) ? requestOrigin : null;
    }

    private static unsafe void AppendHeader(saucer_scheme_response* response, string name, string value)
    {
        Span<byte> hdrBuf = stackalloc byte[64];
        Span<byte> valBuf = stackalloc byte[64];
        var hdr = Utf8String.Create(name, hdrBuf);
        var val = Utf8String.Create(value, valBuf);
        Saucer.saucer_scheme_response_append_header(response, hdr.Pointer, val.Pointer);
        hdr.Dispose();
        val.Dispose();
    }

    private static unsafe string ReadRequestBody(saucer_scheme_request* request)
    {
        var stash = Saucer.saucer_scheme_request_content(request);
        if (stash == null) return string.Empty;

        // saucer_scheme_request_content allocates a new stash that we own and must free.
        try
        {
            var size = Saucer.saucer_stash_size(stash);
            if (size == 0) return string.Empty;

            var data = Saucer.saucer_stash_data(stash);
            return Encoding.UTF8.GetString(data, (int)size);
        }
        finally
        {
            Saucer.saucer_stash_free(stash);
        }
    }

    internal static string EscapeForJs(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal)
             .Replace("`", "\\`", StringComparison.Ordinal)
             .Replace("$", "\\$", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\0", "\\0", StringComparison.Ordinal)
             .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
             .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    public unsafe void Dispose()
    {
        if (_disposed) return;
        // Set first: in-flight responders observe this and skip their native accept/free (INT-03), and
        // TryBeginNativeResponse will reject any newly-arriving request instead of copying its executor.
        _disposed = true;

        foreach (var kvp in _pendingEvals)
        {
            kvp.Value.Completion.TrySetCanceled();
        }
        _pendingEvals.Clear();

        // Drain any in-flight async scheme/command responses before freeing _selfHandle — they resolve
        // GCHandle userdata and we must not free it out from under them (INT-03). Each is already past the
        // _disposed re-check so it will skip native accept/free; we just wait for the managed task to unwind.
        // The run loop has already stopped here, so these are short tail-end completions, not new work.
        var spin = new SpinWait();
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref _inFlightResponses) > 0 && Environment.TickCount64 < deadline)
            spin.SpinOnce();

        // The saucer run loop has stopped by the time Dispose runs (RynWindow.DisposeNative is invoked after
        // saucer_application_run returns), so no posted callback can still fire. Reclaim any GCHandles saucer
        // never executed — ExecutePostedAction is what normally frees them, so without this they would leak.
        foreach (var handle in _postedCallbacks.Keys)
        {
            if (_postedCallbacks.TryRemove(handle, out _))
                NativeCallbackHelper.Free(handle);
        }

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
