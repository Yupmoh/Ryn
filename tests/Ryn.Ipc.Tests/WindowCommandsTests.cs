using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ryn.Core;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Drives the new window setter commands through the full JS-to-C# path: serialized JSON args go through
/// RynCommandDispatcher, the source-generated WindowCommandsRouter, the [RynCommand] handler, and out to the
/// originating window resolved from the ambient <see cref="CurrentWindow"/>. Asserts each forwards to the
/// correct <see cref="IRynWindow"/> member with the camelCase args parsed correctly.
/// </summary>
public sealed class WindowCommandsTests
{
    [Fact]
    public async Task SetFullscreen_ForwardsToCurrentWindow()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.setFullscreen", JsonArgs("{\"fullscreen\":true}")); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).SetFullscreen(true);
    }

    [Fact]
    public async Task SetAlwaysOnTop_ForwardsToCurrentWindow()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.setAlwaysOnTop", JsonArgs("{\"alwaysOnTop\":true}")); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).SetAlwaysOnTop(true);
    }

    [Fact]
    public async Task SetClickThrough_ForwardsToCurrentWindow()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.setClickThrough", JsonArgs("{\"clickThrough\":true}")); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).SetClickThrough(true);
    }

    [Fact]
    public async Task SetPageZoom_ForwardsToCurrentWindow()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.setPageZoom", JsonArgs("{\"factor\":1.25}")); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).SetPageZoom(1.25);
    }

    [Fact]
    public async Task GetPageZoom_ReturnsCurrentWindowValue()
    {
        var (dispatcher, window) = BuildWindowDispatcher();
        window.GetPageZoom().Returns(0.8);

        CurrentWindow.Value = window;
        string result;
        try { result = await dispatcher.DispatchAsync("window.getPageZoom", ReadOnlyMemory<byte>.Empty); }
        finally { CurrentWindow.Value = null; }

        result.Should().Be("0.8");
    }

    [Fact]
    public async Task Center_ForwardsToCurrentWindow()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.center", ReadOnlyMemory<byte>.Empty); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).Center();
    }

    [Fact]
    public async Task SetPosition_ForwardsToMoveWithParsedCoordinates()
    {
        var (dispatcher, window) = BuildWindowDispatcher();

        CurrentWindow.Value = window;
        try { await dispatcher.DispatchAsync("window.setPosition", JsonArgs("{\"x\":10,\"y\":20}")); }
        finally { CurrentWindow.Value = null; }

        window.Received(1).Move(10, 20);
    }

    private static (RynCommandDispatcher Dispatcher, IRynWindow Window) BuildWindowDispatcher()
    {
        var window = Substitute.For<IRynWindow>();
        var services = new ServiceCollection();
        services.AddSingleton(new CurrentWindowAccessor(new NativeApplicationAccessor()));
        services.AddSingleton(Substitute.For<IRynWindowManager>());
        services.AddWindowCommands();
        var provider = services.BuildServiceProvider();

        var routers = provider.GetServices<ICommandRouter>().ToArray();
        return (new RynCommandDispatcher(routers, provider, RynCapabilities.AllowAll()), window);
    }

    private static ReadOnlyMemory<byte> JsonArgs(string json) => Encoding.UTF8.GetBytes(json);
}
