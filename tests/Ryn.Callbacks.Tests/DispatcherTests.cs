using FluentAssertions;
using Ryn.Core;
using Xunit;

namespace Ryn.Callbacks.Tests;

public sealed class DispatcherTests
{
    private static readonly WebViewNavigatingContext NavigatingContext = new(
        new Uri("https://example.test/next"),
        IsNewWindow: false,
        IsRedirect: false,
        IsUserInitiated: true);

    private static readonly WebViewNavigatedContext NavigatedContext = new(
        new Uri("https://example.test/next"));

    private static readonly IServiceProvider EmptyServices = new EmptyServiceProvider();

    [Fact]
    public void DispatchWebViewNavigating_NoRouters_AllowsNavigation()
    {
        var dispatcher = CreateDispatcher();

        var decision = dispatcher.DispatchWebViewNavigating(NavigatingContext);

        decision.Should().Be(NavigationDecision.Allow);
    }

    [Fact]
    public void DispatchWebViewNavigating_InvokesRoutersInRegistrationOrder()
    {
        var calls = new List<string>();
        var dispatcher = CreateDispatcher(
            new RecordingRouter(navigating: _ => { calls.Add("first"); return NavigationDecision.Allow; }),
            new RecordingRouter(navigating: _ => { calls.Add("second"); return NavigationDecision.Allow; }),
            new RecordingRouter(navigating: _ => { calls.Add("third"); return NavigationDecision.Allow; }));

        var decision = dispatcher.DispatchWebViewNavigating(NavigatingContext);

        decision.Should().Be(NavigationDecision.Allow);
        calls.Should().Equal("first", "second", "third");
    }

    [Fact]
    public void DispatchWebViewNavigating_BlockShortCircuitsRemainingRouters()
    {
        var calls = new List<string>();
        var dispatcher = CreateDispatcher(
            new RecordingRouter(navigating: _ => { calls.Add("first"); return NavigationDecision.Allow; }),
            new RecordingRouter(navigating: _ => { calls.Add("blocker"); return NavigationDecision.Block; }),
            new RecordingRouter(navigating: _ => { calls.Add("unreachable"); return NavigationDecision.Allow; }));

        var decision = dispatcher.DispatchWebViewNavigating(NavigatingContext);

        decision.Should().Be(NavigationDecision.Block);
        calls.Should().Equal("first", "blocker");
    }

    [Fact]
    public void DispatchWebViewNavigated_InvokesEveryRouterInRegistrationOrder()
    {
        var calls = new List<string>();
        var dispatcher = CreateDispatcher(
            new RecordingRouter(navigated: _ => calls.Add("first")),
            new RecordingRouter(navigated: _ => calls.Add("second")),
            new RecordingRouter(navigated: _ => calls.Add("third")));

        dispatcher.DispatchWebViewNavigated(NavigatedContext);

        calls.Should().Equal("first", "second", "third");
    }

    private static RynCallbackDispatcher CreateDispatcher(params IRynCallbackRouter[] routers)
    {
        return new RynCallbackDispatcher(routers, EmptyServices);
    }

    private sealed class RecordingRouter(
        Func<WebViewNavigatingContext, NavigationDecision>? navigating = null,
        Action<WebViewNavigatedContext>? navigated = null) : IRynCallbackRouter
    {
        public NavigationDecision OnWebViewNavigating(
            WebViewNavigatingContext context,
            IServiceProvider _) => navigating?.Invoke(context) ?? NavigationDecision.Allow;

        public void OnWebViewNavigated(
            WebViewNavigatedContext context,
            IServiceProvider _) => navigated?.Invoke(context);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type _) => null;
    }
}
