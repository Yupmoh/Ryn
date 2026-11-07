using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Foundation;
using Ryn.Core;
using WebKit;

namespace Ryn.iOS;

public class RynIosWebView : IRynWebView
{
    private readonly WKWebView _webView;
    private readonly List<string> _injectedScripts = [];

    public event EventHandler<FileDropEventArgs>? FileDrop;

    public RynIosWebView(WKWebView webView)
    {
        _webView = webView;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        _webView.InvokeOnMainThread(() =>
        {
            _webView.LoadRequest(new NSUrlRequest(new NSUrl(url.ToString())));
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        _webView.InvokeOnMainThread(() =>
        {
            _webView.LoadHtmlString(html, new NSUrl("ryn://localhost/"));
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _webView.InvokeOnMainThread(async () =>
        {
            try
            {
                var result = await _webView.EvaluateJavaScriptAsync(script).ConfigureAwait(false);
                var str = result?.ToString() ?? "null";
                tcs.TrySetResult(str);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        _injectedScripts.Add(script);
        _webView.InvokeOnMainThread(() =>
        {
            _ = _webView.EvaluateJavaScriptAsync(script);
        });
        return ValueTask.CompletedTask;
    }

    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
    {
    }

    public void EmitEvent(string eventName, string jsonData)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(jsonData);
        var escapedEvent = eventName.Replace("'", "\\'", StringComparison.Ordinal);
        var script = $"if (window.__ryn && window.__ryn._emit) {{ window.__ryn._emit('{escapedEvent}', {jsonData}); }}";
        _webView.InvokeOnMainThread(() =>
        {
            _ = _webView.EvaluateJavaScriptAsync(script);
        });
    }

    public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        EmitEvent(eventName, json);
    }
}
