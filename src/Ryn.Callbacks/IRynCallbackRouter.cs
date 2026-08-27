using Ryn.Core;

namespace Ryn.Callbacks;

/// <summary>Routes framework callbacks directly to generated application handlers.</summary>
public interface IRynCallbackRouter
{
    /// <summary>Invokes this router's handlers before a webview navigation.</summary>
    public NavigationDecision OnWebViewNavigating(WebViewNavigatingContext context, IServiceProvider services);

    /// <summary>Invokes this router's handlers after a webview navigation.</summary>
    public void OnWebViewNavigated(WebViewNavigatedContext context, IServiceProvider services);
}
