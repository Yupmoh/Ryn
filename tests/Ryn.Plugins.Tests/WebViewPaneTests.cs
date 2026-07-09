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
            """{"x":10,"y":20,"width":640,"height":480,"url":"https://example.com","storagePath":"/tmp/pane","devTools":true,"zoom":1.5}""",
            WebViewPaneJsonContext.Default.PaneOpenRequest)!;

        request.X.Should().Be(10);
        request.Y.Should().Be(20);
        request.Width.Should().Be(640);
        request.Height.Should().Be(480);
        request.Url.Should().Be("https://example.com");
        request.StoragePath.Should().Be("/tmp/pane");
        request.DevTools.Should().BeTrue();
        request.Zoom.Should().Be(1.5);

        var defaults = JsonSerializer.Deserialize("{}", WebViewPaneJsonContext.Default.PaneOpenRequest)!;
        defaults.Width.Should().Be(400);
        defaults.Height.Should().Be(300);
        defaults.Zoom.Should().Be(1.0);
        defaults.Url.Should().BeNull();
        defaults.DevTools.Should().BeFalse();
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
        service.Invoking(s => s.SetZoom(99, 2.0)).Should().NotThrow();
        service.Invoking(s => s.CloseAll()).Should().NotThrow();
        (await service.CloseAsync(99)).Should().BeFalse();
        await service.Awaiting(s => s.EvalAsync(99, "1+1")).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class NoopDispatcher : IMainThreadDispatcher
    {
        public void Post(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
    }
}
