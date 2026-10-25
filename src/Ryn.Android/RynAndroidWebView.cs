using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Android.Webkit;
using Ryn.Core;

namespace Ryn.Android;

public class RynAndroidWebView : IRynWebView
{
    private readonly WebView _webView;
    private readonly List<string> _injectedScripts = [];

    public event EventHandler<FileDropEventArgs>? FileDrop;

    public RynAndroidWebView(WebView webView)
    {
        _webView = webView;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        _webView.Post(() => _webView.LoadUrl(url.ToString()));
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default)
    {
        _webView.Post(() =>
            _webView.LoadDataWithBaseURL("https://appassets.androidplatform.net/", html, "text/html", "UTF-8", null));
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _webView.Post(() =>
        {
            try
            {
                _webView.EvaluateJavascript(script, new JavascriptCallback(tcs));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return new ValueTask<string>(tcs.Task);
    }

    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        _injectedScripts.Add(script);
        _webView.Post(() => _webView.EvaluateJavascript(script, null));
        return ValueTask.CompletedTask;
    }

    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler)
    {
        // Custom schemes can be registered with RynWebViewClient
    }

    public void EmitEvent(string eventName, string jsonData)
    {
        var escapedEvent = eventName.Replace("'", "\\'");
        var script = $"if (window.__ryn && window.__ryn._emit) {{ window.__ryn._emit('{escapedEvent}', {jsonData}); }}";
        _webView.Post(() => _webView.EvaluateJavascript(script, null));
    }

    public void EmitEvent<T>(string eventName, T payload, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        EmitEvent(eventName, json);
    }

    private sealed class JavascriptCallback : Java.Lang.Object, IValueCallback
    {
        private readonly TaskCompletionSource<string> _tcs;

        public JavascriptCallback(TaskCompletionSource<string> tcs)
        {
            _tcs = tcs;
        }

        public void OnReceiveValue(Java.Lang.Object? value)
        {
            var str = value?.ToString() ?? "null";
            // Strip enclosing quotes from string results returned by EvaluateJavascript
            if (str.Length >= 2 && str.StartsWith('"') && str.EndsWith('"'))
            {
                str = str[1..^1];
            }
            _tcs.TrySetResult(str);
        }
    }
}
