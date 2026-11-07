using Android.Content.Res;
using Android.Graphics;
using Android.Webkit;
using Ryn.Android.Internal;

namespace Ryn.Android;

public class RynWebViewClient : WebViewClient
{
    private readonly AssetManager _assets;
    private readonly Func<string> _bridgeScriptFactory;

    public RynWebViewClient(AssetManager assets, Func<string> bridgeScriptFactory)
    {
        _assets = assets;
        _bridgeScriptFactory = bridgeScriptFactory;
    }

    public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
    {
        if (request?.Url is null)
            return base.ShouldInterceptRequest(view, request);

        var scheme = request.Url.Scheme?.ToLowerInvariant();
        var host = request.Url.Host?.ToLowerInvariant();

        // Match custom scheme ryn://app, asset domain, or local requests
        if (scheme == "ryn" || host == "appassets.androidplatform.net" || host == "localhost")
        {
            var path = request.Url.Path ?? "";
            var relativePath = (string.IsNullOrEmpty(path) || path == "/") ? "index.html" : path.TrimStart('/');
            var cleanPath = relativePath.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase)
                ? relativePath[8..]
                : relativePath;

            // Try opening from wwwroot/ first, then direct relative path
            try
            {
                var stream = _assets.Open("wwwroot/" + cleanPath);
                var mimeType = MimeTypeHelper.GetMimeType(cleanPath);
                return new WebResourceResponse(mimeType, "UTF-8", stream);
            }
            catch (Java.IO.IOException)
            {
                try
                {
                    var stream = _assets.Open(cleanPath);
                    var mimeType = MimeTypeHelper.GetMimeType(cleanPath);
                    return new WebResourceResponse(mimeType, "UTF-8", stream);
                }
                catch (Java.IO.IOException)
                {
                    // Not found in APK assets
                }
            }
        }

        return base.ShouldInterceptRequest(view, request);
    }

    public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
    {
        base.OnPageStarted(view, url, favicon);
        InjectBridge(view);
    }

    public override void OnPageFinished(WebView? view, string? url)
    {
        base.OnPageFinished(view, url);
        InjectBridge(view);
    }

    private void InjectBridge(WebView? view)
    {
        if (view is null) return;
        var script = _bridgeScriptFactory();
        view.EvaluateJavascript(script, null);
    }
}
