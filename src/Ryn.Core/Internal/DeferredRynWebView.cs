using System.Text.Json.Serialization.Metadata;

namespace Ryn.Core.Internal;

/// <summary>
/// An <see cref="IRynWebView"/> that can be injected before the native webview exists. Members forward to
/// the real webview once available; <see cref="FileDrop"/> subscriptions made early are attached when the
/// window becomes ready. See <see cref="DeferredRynWindow"/>.
/// </summary>
internal sealed class DeferredRynWebView(RynWindowAccessor accessor) : IRynWebView
{
    private IRynWebView Live => accessor.Window?.WebView
        ?? throw new InvalidOperationException(
            "The webview is not available yet. IRynWebView can be injected anywhere, but its members are only usable after RunAsync has started.");

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => Live.NavigateAsync(url, cancellationToken);
    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default) => Live.NavigateToStringAsync(html, cancellationToken);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) => Live.EvaluateJavaScriptAsync(script, cancellationToken);
    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default) => Live.InjectScriptAsync(script, cancellationToken);
    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler) => Live.RegisterCustomScheme(scheme, handler);
    public void EmitEvent(string eventName, string jsonData) => Live.EmitEvent(eventName, jsonData);
    public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo) => Live.EmitEvent(eventName, payload, typeInfo);

    public event EventHandler<FileDropEventArgs>? FileDrop
    {
        add { var h = value; accessor.OnReady(w => w.WebView.FileDrop += h); }
        remove { if (accessor.Window?.WebView is { } v) v.FileDrop -= value; }
    }
}
