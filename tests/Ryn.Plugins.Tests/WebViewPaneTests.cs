using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.WebViewPane;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class WebViewPaneEvalProtocolTests
{
    [Fact]
    public void BuildEvalScript_InlinesTheExpressionAndTagsTheEvalId()
    {
        var script = WebViewPaneService.BuildEvalScript(42, "document.title");

        // Inlined as an expression (never eval()'d — strict-CSP pages block string evaluation).
        script.Should().Contain("document.title");
        script.Should().NotContain("eval(");
        script.Should().Contain("__rynPaneEval: 42");
        script.Should().Contain("window.saucer.internal.message");
    }

    [Fact]
    public void BuildEvalScript_SupportsIifeStatements()
    {
        var script = WebViewPaneService.BuildEvalScript(1, "(() => { const a = 2; return a + 2; })()");
        script.Should().Contain("(() => { const a = 2; return a + 2; })()");
    }

    [Theory]
    [InlineData("""{"__rynPaneEval":7,"ok":true,"result":{"a":1}}""", 7, true, """{"a":1}""")]
    [InlineData("""{"__rynPaneEval":8,"ok":true}""", 8, true, "null")]
    [InlineData("""{"__rynPaneEval":9,"ok":false,"error":"ReferenceError: x"}""", 9, false, "ReferenceError: x")]
    public void TryParseEvalMessage_ParsesEnvelopes(string message, long id, bool ok, string payload)
    {
        WebViewPaneService.TryParseEvalMessage(message, out var evalId, out var evalOk, out var evalPayload)
            .Should().BeTrue();
        evalId.Should().Be(id);
        evalOk.Should().Be(ok);
        evalPayload.Should().Be(payload);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"some":"other message"}""")]
    [InlineData("""["__rynPaneEval"]""")]
    [InlineData("""{"__rynPaneEval":"not a number"}""")]
    public void TryParseEvalMessage_IgnoresForeignMessages(string message)
    {
        WebViewPaneService.TryParseEvalMessage(message, out _, out _, out _).Should().BeFalse();
    }
}

public sealed class WebViewPaneRequestTests
{
    [Fact]
    public void PaneOpenRequest_DeserializesCamelCase_WithDefaults()
    {
        var request = JsonSerializer.Deserialize(
            """{"x":10,"y":20,"width":640,"height":480,"url":"https://example.com","storagePath":"/tmp/pane","devTools":true,"zoom":1.5,"userAgent":"CoveBot/2.0"}""",
            WebViewPaneJsonContext.Default.PaneOpenRequest)!;

        request.X.Should().Be(10);
        request.Y.Should().Be(20);
        request.Width.Should().Be(640);
        request.Height.Should().Be(480);
        request.Url.Should().Be("https://example.com");
        request.StoragePath.Should().Be("/tmp/pane");
        request.DevTools.Should().BeTrue();
        request.Zoom.Should().Be(1.5);
        request.UserAgent.Should().Be("CoveBot/2.0");

        var defaults = JsonSerializer.Deserialize("{}", WebViewPaneJsonContext.Default.PaneOpenRequest)!;
        defaults.Width.Should().Be(400);
        defaults.Height.Should().Be(300);
        defaults.Zoom.Should().Be(1.0);
        defaults.Url.Should().BeNull();
        defaults.DevTools.Should().BeFalse();
        defaults.UserAgent.Should().BeNull();
    }

    [Fact]
    public void EventPayloads_SerializeCamelCase()
    {
        JsonSerializer.Serialize(new PaneNavigatedEvent(3, "https://a.example"),
                WebViewPaneJsonContext.Default.PaneNavigatedEvent)
            .Should().Be("""{"id":3,"url":"https://a.example"}""");
        JsonSerializer.Serialize(new PaneLoadStateEvent(3, "started"),
                WebViewPaneJsonContext.Default.PaneLoadStateEvent)
            .Should().Be("""{"id":3,"state":"started"}""");
    }
}

public sealed class WebViewPaneFindTests
{
    [Fact]
    public void BuildFind_EscapesTextAndInstallsEngineOnce()
    {
        var script = PaneFindScript.BuildFind("he said \"hi\"\n<b>", forward: true, matchCase: false);

        script.Should().Contain("if (!window.__rynFind)");
        script.Should().Contain("window.__rynFind = (() => {");
        // The needle is embedded as a JSON string literal — quotes, newlines, and HTML-sensitive
        // characters are escaped by STJ's default encoder (" for '"', < for '<').
        script.Should().Contain("he said \\u0022hi\\u0022\\n\\u003Cb\\u003E");
        script.Should().Contain(".find(");
        script.Should().Contain("true, false");
        script.Should().NotContain("eval(");
    }

    [Fact]
    public void BuildNextAndStop_GuardAgainstMissingEngine()
    {
        PaneFindScript.BuildNext(forward: false).Should().Contain("window.__rynFind ? window.__rynFind.next(false)");
        PaneFindScript.BuildStop(clearHighlights: true).Should().Contain("window.__rynFind.stop(true)");
        PaneFindScript.BuildNext(forward: true).Should().Contain("({ matches: 0, activeIndex: -1 })");
    }

    [Fact]
    public void PaneFindResult_RoundTripsCamelCase()
    {
        var parsed = JsonSerializer.Deserialize(
            """{"matches":12,"activeIndex":3}""", WebViewPaneJsonContext.Default.PaneFindResult)!;
        parsed.Matches.Should().Be(12);
        parsed.ActiveIndex.Should().Be(3);

        JsonSerializer.Serialize(new PaneFindResult(0, -1), WebViewPaneJsonContext.Default.PaneFindResult)
            .Should().Be("""{"matches":0,"activeIndex":-1}""");
    }
}

public sealed class WebViewPanePermissionTests
{
    [Theory]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_UNKNOWN, new[] { "unknown" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_AUDIO_MEDIA, new[] { "microphone" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_VIDEO_MEDIA, new[] { "camera" })]
    [InlineData(
        Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_AUDIO_MEDIA
        | Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_VIDEO_MEDIA,
        new[] { "microphone", "camera" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_LOCATION, new[] { "geolocation" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_CLIPBOARD, new[] { "clipboard" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_NOTIFICATION, new[] { "notifications" })]
    [InlineData(Ryn.Interop.saucer_permission_type.SAUCER_PERMISSION_TYPE_DESKTOP_MEDIA, new[] { "screenShare" })]
    public void DescribePermissionKinds_MapsFlags(Ryn.Interop.saucer_permission_type type, string[] expected)
    {
        WebViewPaneService.DescribePermissionKinds(type).Should().Equal(expected);
    }

    [Theory]
    [InlineData(0, "browserProcessExited")]
    [InlineData(1, "renderProcessExited")]
    [InlineData(2, "renderProcessUnresponsive")]
    [InlineData(6, "gpuProcessExited")]
    [InlineData(42, "unknownProcessExited")]
    public void WindowsProcessFailedKinds_MapToReasonStrings(int kind, string expected) =>
        PaneLifecycleInterop.DescribeWindowsProcessFailedKind(kind).Should().Be(expected);

    [Theory]
    [InlineData(0, "crashed")]
    [InlineData(1, "exceededMemoryLimit")]
    [InlineData(2, "terminatedByApi")]
    [InlineData(9, "unknown")]
    public void LinuxTerminationReasons_MapToReasonStrings(int reason, string expected) =>
        PaneLifecycleInterop.DescribeLinuxTerminationReason(reason).Should().Be(expected);

    [Fact]
    public void DownloadEvents_SerializeCamelCase()
    {
        JsonSerializer.Serialize(new PaneDownloadRequestedEvent(1, 7, "https://x/f.zip", "f.zip"),
                WebViewPaneJsonContext.Default.PaneDownloadRequestedEvent)
            .Should().Be("""{"id":1,"downloadId":7,"url":"https://x/f.zip","suggestedName":"f.zip"}""");
        JsonSerializer.Serialize(new PaneDownloadProgressEvent(1, 7, 512, 2048),
                WebViewPaneJsonContext.Default.PaneDownloadProgressEvent)
            .Should().Be("""{"id":1,"downloadId":7,"receivedBytes":512,"totalBytes":2048}""");
        JsonSerializer.Serialize(new PaneDownloadCompletedEvent(1, 7, "/tmp/f.zip"),
                WebViewPaneJsonContext.Default.PaneDownloadCompletedEvent)
            .Should().Be("""{"id":1,"downloadId":7,"path":"/tmp/f.zip"}""");
    }

    [Fact]
    public void ProcessTerminatedEvent_SerializesCamelCase() =>
        JsonSerializer.Serialize(new PaneProcessTerminatedEvent(4, "crashed"),
                WebViewPaneJsonContext.Default.PaneProcessTerminatedEvent)
            .Should().Be("""{"id":4,"reason":"crashed"}""");

    [Fact]
    public void PermissionEvent_SerializesCamelCase()
    {
        JsonSerializer.Serialize(
                new PanePermissionEvent(2, 7, ["camera"], "https://meet.example"),
                WebViewPaneJsonContext.Default.PanePermissionEvent)
            .Should().Be("""{"id":2,"requestId":7,"kinds":["camera"],"url":"https://meet.example"}""");
    }
}

public sealed class WebViewPaneDependencyInjectionTests
{
    [Fact]
    public void AddRynWebViewPane_RegistersServiceCommandsAndPlugin()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynWebViewPane();

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<WebViewPaneService>().Should().NotBeNull();
        sp.GetRequiredService<WebViewPaneCommands>().Should().NotBeNull();
        sp.GetServices<IRynPlugin>().Should().Contain(p => p.Name == "webviewPane");
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ServiceQueries_AreSafeWithoutAnyPanes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynWebViewPane();
        using var sp = services.BuildServiceProvider();

        var service = sp.GetRequiredService<WebViewPaneService>();
        service.List().Should().BeEmpty();
        service.GetUrl(99).Should().BeEmpty();
        service.Invoking(s => s.SetBounds(99, 0, 0, 10, 10)).Should().NotThrow();
        service.Invoking(s => s.SetBackground(99, "#1e1e2e")).Should().NotThrow();
        service.Invoking(s => s.SetBackground(99, "not-a-color")).Should().Throw<ArgumentException>();
        service.Invoking(s => s.SetZoom(99, 2.0)).Should().NotThrow();
        service.Invoking(s => s.CloseAll()).Should().NotThrow();
        (await service.CloseAsync(99)).Should().BeFalse();
        await service.Awaiting(s => s.EvalAsync(99, "1+1")).Should().ThrowAsync<ArgumentException>();
        await service.Awaiting(s => s.SetUserAgentAsync(99, "UA/1.0")).Should().ThrowAsync<ArgumentException>();
        await service.Awaiting(s => s.SetUserAgentAsync(1, "")).Should().ThrowAsync<ArgumentException>();
        (await service.ResolvePermissionAsync(12345, grant: true)).Should().BeFalse();
        await service.Awaiting(s => s.CdpCallAsync(99, "Page.enable", "{}")).Should().ThrowAsync<ArgumentException>();
        await service.Awaiting(s => s.ScreenshotAsync(99)).Should().ThrowAsync<ArgumentException>();
        await service.Awaiting(s => s.SetSuspendedAsync(99, true)).Should().ThrowAsync<ArgumentException>();
        service.Invoking(s => s.ReloadFromCrash(99)).Should().NotThrow();
    }

    private sealed class NoopDispatcher : IMainThreadDispatcher
    {
        public void Post(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
    }
}

/// <summary>
/// Pane bounds are top-left CSS pixels relative to the window content area on every platform; on macOS the
/// Y is converted into AppKit's bottom-left contentView space at apply time.
/// </summary>
public sealed class WebViewPaneBoundsTests
{
    [Theory]
    [InlineData(600, 0, 100, 500)]   // pane at the top of a 600pt window → native Y is near the top (500)
    [InlineData(600, 500, 100, 0)]   // pane at the bottom → native Y 0
    [InlineData(600, 100, 400, 100)] // mid-window rect
    [InlineData(400, 0, 400, 0)]     // full-height pane
    public void ToMacNativeY_FlipsTopLeftIntoBottomLeftSpace(int contentHeight, int y, int height, int expected)
        => WebViewPaneService.ToMacNativeY(contentHeight, y, height).Should().Be(expected);
}

public sealed class PaneColorTests
{
    [Theory]
    [InlineData("#1e1e2e", 0x1e, 0x1e, 0x2e, 255)]
    [InlineData("#1e1e2eff", 0x1e, 0x1e, 0x2e, 255)]
    [InlineData("#1e1e2e80", 0x1e, 0x1e, 0x2e, 0x80)]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#f00c", 255, 0, 0, 0xcc)]
    [InlineData("  #ABCDEF  ", 0xab, 0xcd, 0xef, 255)] // whitespace + uppercase
    [InlineData("rgb(30, 30, 46)", 30, 30, 46, 255)]
    [InlineData("rgba(30, 30, 46, 0.5)", 30, 30, 46, 128)]
    [InlineData("rgba(0,0,0,0)", 0, 0, 0, 0)]
    [InlineData("RGBA(1, 2, 3, 1)", 1, 2, 3, 255)]
    public void TryParse_AcceptsCssColorForms(string input, int r, int g, int b, int a)
    {
        PaneColor.TryParse(input, out var pr, out var pg, out var pb, out var pa).Should().BeTrue();
        (pr, pg, pb, pa).Should().Be(((byte)r, (byte)g, (byte)b, (byte)a));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12345")]           // wrong hex length
    [InlineData("#gggggg")]          // non-hex digits
    [InlineData("1e1e2e")]           // missing #
    [InlineData("rgb(30, 30)")]      // too few components
    [InlineData("rgb(300, 0, 0)")]   // out of byte range
    [InlineData("rgba(0, 0, 0, 2)")] // alpha out of 0–1
    [InlineData("hsl(200, 50%, 50%)")]
    public void TryParse_RejectsMalformedColors(string? input)
        => PaneColor.TryParse(input, out _, out _, out _, out _).Should().BeFalse();
}

/// <summary>
/// Panes must be torn down inside the native close event (before saucer snapshots the closed-event
/// listeners), never first on <see cref="IRynWindow.Closed"/> — see WirePaneTeardown. These cover the
/// interface-fallback wiring; the RynWindow path (CloseApproved) needs a native window.
/// </summary>
public sealed class WebViewPanePluginTeardownTests
{
    [Fact]
    public void WirePaneTeardown_ClosesPanesWhenClosingIsNotCancelled()
    {
        var window = NSubstitute.Substitute.For<IRynWindow>();
        var calls = 0;
        WebViewPanePlugin.WirePaneTeardown(window, () => calls++);

        window.Closing += NSubstitute.Raise.EventWith(window, new WindowClosingEventArgs());

        calls.Should().Be(1, "an allowed close must release panes inside the close event");
    }

    [Fact]
    public void WirePaneTeardown_KeepsPanesAliveWhenAnEarlierHandlerCancelledTheClose()
    {
        var window = NSubstitute.Substitute.For<IRynWindow>();
        var calls = 0;
        WebViewPanePlugin.WirePaneTeardown(window, () => calls++);

        window.Closing += NSubstitute.Raise.EventWith(window, new WindowClosingEventArgs { Cancel = true });

        calls.Should().Be(0, "a cancelled close keeps the window open, so its panes must survive");
    }

    [Fact]
    public void WirePaneTeardown_AlsoClosesOnClosedAsASafetyNet()
    {
        var window = NSubstitute.Substitute.For<IRynWindow>();
        var calls = 0;
        WebViewPanePlugin.WirePaneTeardown(window, () => calls++);

        window.Closed += NSubstitute.Raise.EventWith(window, EventArgs.Empty);

        calls.Should().Be(1);
    }
}
