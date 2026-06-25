using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// Tests for the multi-window surface that can be exercised without starting the native event loop: the
/// per-window options projection, the ambient "current window" routing seam (the mechanism behind window.*
/// commands acting on the originating window), the window manager / accessor fallbacks, and the
/// <see cref="RynApplication"/> public API before the loop is running. The full open-a-window IPC round trip
/// is verified end-to-end by the AOT MultiWindow sample.
/// </summary>
public sealed class MultiWindowTests
{
    // ---- RynWindowOptions projection ----------------------------------------------------------------

    [Fact]
    public void RynWindowOptions_HasSensibleDefaults()
    {
        var options = new RynWindowOptions();

        options.Title.Should().Be("Ryn Window");
        options.Width.Should().Be(800);
        options.Height.Should().Be(600);
        options.Resizable.Should().BeTrue();
        options.TitleBarStyle.Should().Be(TitleBarStyle.Native);
        options.AllowedOrigins.Should().BeEmpty();
        options.CustomSchemes.Should().BeEmpty();
    }

    [Fact]
    public void ToRynOptions_MapsEveryPerWindowField()
    {
        var options = new RynWindowOptions
        {
            Title = "Second",
            Width = 321,
            Height = 222,
            MinWidth = 200,
            MinHeight = 150,
            MaxWidth = 1920,
            MaxHeight = 1080,
            Resizable = false,
            TitleBarStyle = TitleBarStyle.Hidden,
            Transparent = true,
            Url = new Uri("https://example.test/app"),
            UseLocalServer = true,
            LocalServerPort = 9001,
            IconPath = "/tmp/icon.png",
            DevTools = true,
            PersistWindowState = true,
        };
        options.AllowedOrigins.Add("https://allowed.test");
        options.CustomSchemes.Add("custom");

        var projected = options.ToRynOptions();

        projected.Title.Should().Be("Second");
        projected.Width.Should().Be(321);
        projected.Height.Should().Be(222);
        projected.MinWidth.Should().Be(200);
        projected.MinHeight.Should().Be(150);
        projected.MaxWidth.Should().Be(1920);
        projected.MaxHeight.Should().Be(1080);
        projected.Resizable.Should().BeFalse();
        projected.TitleBarStyle.Should().Be(TitleBarStyle.Hidden);
        projected.Transparent.Should().BeTrue();
        projected.Url.Should().Be(new Uri("https://example.test/app"));
        projected.UseLocalServer.Should().BeTrue();
        projected.LocalServerPort.Should().Be(9001);
        projected.IconPath.Should().Be("/tmp/icon.png");
        projected.DevTools.Should().BeTrue();
        projected.PersistWindowState.Should().BeTrue();
        projected.AllowedOrigins.Should().ContainSingle().Which.Should().Be("https://allowed.test");
        projected.CustomSchemes.Should().ContainSingle().Which.Should().Be("custom");
    }

    [Fact]
    public void ToRynOptions_CarriesHtmlContent()
    {
        var projected = new RynWindowOptions { Html = "<h1>hi</h1>" }.ToRynOptions();

        projected.Html.Should().Be("<h1>hi</h1>");
        projected.Url.Should().BeNull();
    }

    // ---- Ambient current-window routing (the per-window dispatch seam) -------------------------------

    [Fact]
    public async Task CurrentWindow_Value_FlowsAcrossTheDispatchHop()
    {
        CurrentWindow.Value = null;
        var window = Substitute.For<IRynWindow>();

        // Mirror what RynWebView.ExecuteCommandAsync does: set the ambient, then hop to a worker thread.
        CurrentWindow.Value = window;
        var observed = await Task.Run(() => CurrentWindow.Value);

        observed.Should().BeSameAs(window, "the ambient flows into the dispatched command via ExecutionContext");
        CurrentWindow.Value = null;
    }

    [Fact]
    public async Task CurrentWindow_ConcurrentDispatches_DoNotCollide()
    {
        CurrentWindow.Value = null;
        var windowA = Substitute.For<IRynWindow>();
        var windowB = Substitute.For<IRynWindow>();

        // Two independent dispatches each stamp their own originating window; neither must see the other's.
        var a = Task.Run(async () => { CurrentWindow.Value = windowA; await Task.Yield(); return CurrentWindow.Value; });
        var b = Task.Run(async () => { CurrentWindow.Value = windowB; await Task.Yield(); return CurrentWindow.Value; });

        (await a).Should().BeSameAs(windowA);
        (await b).Should().BeSameAs(windowB);
        CurrentWindow.Value.Should().BeNull("an inner dispatch's ambient must not leak back to the caller");
    }

    [Fact]
    public void CurrentWindowAccessor_PrefersTheAmbientOriginatingWindow()
    {
        var accessor = new CurrentWindowAccessor(new NativeApplicationAccessor());
        var window = Substitute.For<IRynWindow>();
        window.Id.Returns(5);

        CurrentWindow.Value = window;
        try
        {
            accessor.Current.Should().BeSameAs(window);
            accessor.Current.Id.Should().Be(5);
        }
        finally
        {
            CurrentWindow.Value = null;
        }
    }

    [Fact]
    public void CurrentWindowAccessor_ThrowsWhenNoAmbientAndNoRunningHost()
    {
        CurrentWindow.Value = null;
        var accessor = new CurrentWindowAccessor(new NativeApplicationAccessor());

        var act = () => accessor.Current;

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- RynWindowManager fallbacks (no running host) ------------------------------------------------

    [Fact]
    public void RynWindowManager_ListsNoWindows_BeforeTheLoopIsRunning()
    {
        var manager = new RynWindowManager(new NativeApplicationAccessor());

        manager.Windows.Should().BeEmpty();
    }

    [Fact]
    public void RynWindowManager_OpenWindow_ThrowsBeforeTheLoopIsRunning()
    {
        var manager = new RynWindowManager(new NativeApplicationAccessor());

        var act = () => manager.OpenWindow(new RynWindowOptions());

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- RynApplication public surface before RunAsync ----------------------------------------------

    [Fact]
    public async Task Application_WindowsCollection_IsEmptyBeforeRun()
    {
        await using var app = RynApplication.CreateBuilder().Build();

        app.Windows.Should().BeEmpty();
    }

    [Fact]
    public async Task Application_MainWindowAndOpenWindow_ThrowBeforeRun()
    {
        await using var app = RynApplication.CreateBuilder().Build();

        var mainWindow = () => app.MainWindow;
        var openWindow = () => app.OpenWindow(new RynWindowOptions());

        mainWindow.Should().Throw<InvalidOperationException>();
        openWindow.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Build_RegistersWindowManagerAndCurrentWindowAccessor()
    {
        await using var app = RynApplication.CreateBuilder().Build();

        app.Services.GetService<IRynWindowManager>().Should().NotBeNull();
        app.Services.GetService<CurrentWindowAccessor>().Should().NotBeNull();
    }
}
