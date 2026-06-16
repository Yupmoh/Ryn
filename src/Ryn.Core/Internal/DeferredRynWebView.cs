using System.Text.Json.Serialization.Metadata;

namespace Ryn.Core.Internal;

/// <summary>
/// An <see cref="IRynWebView"/> that can be injected before the native webview exists. Members forward to
/// the real webview once available; <see cref="FileDrop"/> subscriptions made early are attached when the
/// webview becomes ready. See <see cref="DeferredRynWindow"/>.
/// </summary>
/// <remarks>
/// The webview is created later than the window: <c>accessor.Window</c> is assigned (and the accessor's
/// <see cref="RynWindowAccessor.OnReady"/> queue drains) <em>before</em> the native event loop starts and
/// runs <c>RynWindow.InitializeNative</c>, which is what creates <c>RynWindow.WebView</c>. So a FileDrop
/// subscription cannot simply queue <c>w.WebView.FileDrop += h</c> through <c>OnReady</c> — at drain time
/// <c>w.WebView</c> still throws "Window not initialized" and that crashes startup (ARC-08). Instead we
/// hold the pending subscribe/unsubscribe handlers here and flush them onto the live webview the first time
/// one is actually observed (any forwarded member access, plus an eager attempt from the OnReady drain).
/// A pre-ready unsubscribe cancels the matching still-pending subscribe rather than leaking it (PAP-15).
/// </remarks>
internal sealed class DeferredRynWebView : IRynWebView
{
    private readonly RynWindowAccessor _accessor;

    // Pending FileDrop handlers queued while the native webview does not yet exist. Reconciled as a multiset:
    // a pre-ready remove cancels one matching pending add (PAP-15) so subscribe-then-unsubscribe leaves nothing
    // queued. Guarded by _gate because, although the startup sequence that fills it is single-threaded, an app
    // may subscribe/unsubscribe from another thread before the first flush.
    private readonly List<EventHandler<FileDropEventArgs>> _pendingFileDrop = [];
    private readonly object _gate = new();
    private bool _flushed;

    public DeferredRynWebView(RynWindowAccessor accessor)
    {
        _accessor = accessor;
        // Eagerly attempt a flush once the window is set. The webview is usually not ready yet at that point
        // (it is created later, inside the event loop), so this is typically a no-op; the real flush happens
        // on the first forwarded member access below. Registering here covers apps that subscribe to FileDrop
        // but never touch any other webview member. The OnReady token is discarded: FileDrop's own pre-ready
        // multiset (see below) cancels a subscribe-then-unsubscribe, so there is no queued attach to cancel here.
        _ = accessor.OnReady(_ => TryFlush());
    }

    private IRynWebView Live
    {
        get
        {
            // Touching a live member is also our reliable "webview exists now" signal, so flush any handlers
            // that were queued before the native webview was created.
            if (!Volatile.Read(ref _flushed)) TryFlush();
            return _accessor.Window?.WebView
                ?? throw new InvalidOperationException(
                    "The webview is not available yet. IRynWebView can be injected anywhere, but its members are only usable after RunAsync has started.");
        }
    }

    /// <summary>
    /// Applies any handlers queued before the native webview existed, exactly once. No-op while the webview is
    /// still not created (the accessor's window may be set well before <c>InitializeNative</c> builds the webview).
    /// </summary>
    private void TryFlush()
    {
        lock (_gate)
        {
            if (_flushed) return;
            if (_accessor.Window?.WebView is not { } webView) return;

            foreach (var handler in _pendingFileDrop)
                webView.FileDrop += handler;
            _pendingFileDrop.Clear();
            _flushed = true;
        }
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => Live.NavigateAsync(url, cancellationToken);
    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default) => Live.NavigateToStringAsync(html, cancellationToken);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) => Live.EvaluateJavaScriptAsync(script, cancellationToken);
    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default) => Live.InjectScriptAsync(script, cancellationToken);
    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler) => Live.RegisterCustomScheme(scheme, handler);
    public void EmitEvent(string eventName, string jsonData) => Live.EmitEvent(eventName, jsonData);
    public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo) => Live.EmitEvent(eventName, payload, typeInfo);

    public event EventHandler<FileDropEventArgs>? FileDrop
    {
        add
        {
            if (value is null) return;
            lock (_gate)
            {
                // Once the webview has come up (flush has run), attach straight through to the live webview —
                // this is the normal post-ready subscription path. Only queue while it does not yet exist.
                if (_flushed)
                {
                    if (_accessor.Window?.WebView is { } webView)
                        webView.FileDrop += value;
                }
                else
                {
                    _pendingFileDrop.Add(value);
                }
            }
        }
        remove
        {
            if (value is null) return;
            lock (_gate)
            {
                // A pre-ready unsubscribe must cancel the matching still-pending subscribe (PAP-15); otherwise
                // the queued add would attach a handler the caller already removed and leak it permanently.
                if (!_flushed && _accessor.Window?.WebView is null)
                {
                    _pendingFileDrop.Remove(value);
                    return;
                }

                // Webview is (or has been) live: ensure any queue is drained, then detach from the real webview.
                if (!_flushed) TryFlush();
                if (_accessor.Window?.WebView is { } webView)
                    webView.FileDrop -= value;
                else
                    _pendingFileDrop.Remove(value);
            }
        }
    }
}
