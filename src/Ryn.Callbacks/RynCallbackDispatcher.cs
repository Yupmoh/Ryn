using Ryn.Core;

namespace Ryn.Callbacks;

/// <summary>Dispatches framework callbacks to source-generated routers in registration order.</summary>
public sealed class RynCallbackDispatcher
{
    private readonly IRynCallbackRouter[] _routers;
    private readonly IServiceProvider _services;

    public RynCallbackDispatcher(IEnumerable<IRynCallbackRouter> routers, IServiceProvider services)
    {
        _routers = routers.ToArray();
        _services = services;
    }

    /// <summary>Invokes navigating callbacks until one blocks the navigation.</summary>
    public NavigationDecision DispatchWebViewNavigating(WebViewNavigatingContext context)
    {
        for (var i = 0; i < _routers.Length; i++)
        {
            if (_routers[i].OnWebViewNavigating(context, _services) == NavigationDecision.Block)
                return NavigationDecision.Block;
        }

        return NavigationDecision.Allow;
    }

    /// <summary>Invokes all navigated callbacks.</summary>
    public void DispatchWebViewNavigated(WebViewNavigatedContext context)
    {
        for (var i = 0; i < _routers.Length; i++)
            _routers[i].OnWebViewNavigated(context, _services);
    }
}
