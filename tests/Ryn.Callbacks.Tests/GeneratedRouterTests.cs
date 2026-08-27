using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Xunit;

namespace Ryn.Callbacks.Tests;

public sealed class GeneratedRouterTests
{
    [Fact]
    public void GeneratedRouter_RegistersAndDispatchesStaticAndInstanceCallbacks()
    {
        GeneratedCallbackHandlers.LastNavigatingContext = null;
        using var services = new ServiceCollection()
            .AddRynCallbacks()
            .AddGeneratedCallbackHandlers()
            .BuildServiceProvider();
        var dispatcher = services.GetRequiredService<RynCallbackDispatcher>();
        var navigating = new WebViewNavigatingContext(
            new Uri("https://example.test/blocked"),
            IsNewWindow: true,
            IsRedirect: false,
            IsUserInitiated: true);
        var navigated = new WebViewNavigatedContext(new Uri("https://example.test/landed"));

        var decision = dispatcher.DispatchWebViewNavigating(navigating);
        dispatcher.DispatchWebViewNavigated(navigated);

        decision.Should().Be(NavigationDecision.Block);
        GeneratedCallbackHandlers.LastNavigatingContext.Should().Be(navigating);
        var handler = services.GetRequiredService<GeneratedCallbackHandlers>();
        handler.NavigatedCalls.Should().Be(1);
        handler.LastNavigatedContext.Should().Be(navigated);
    }
}

internal sealed class GeneratedCallbackHandlers
{
    internal static WebViewNavigatingContext? LastNavigatingContext { get; set; }

    internal int NavigatedCalls { get; private set; }

    internal WebViewNavigatedContext? LastNavigatedContext { get; private set; }

    [RynCallback(RynCallbackKind.WebViewNavigating)]
    internal static NavigationDecision OnNavigating(WebViewNavigatingContext context)
    {
        LastNavigatingContext = context;
        return NavigationDecision.Block;
    }

    [RynCallback(RynCallbackKind.WebViewNavigated)]
    internal void OnNavigated(WebViewNavigatedContext context)
    {
        NavigatedCalls++;
        LastNavigatedContext = context;
    }
}
