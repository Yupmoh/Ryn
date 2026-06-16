using FluentAssertions;
using NSubstitute;
using Ryn.Core;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Regression tests for <see cref="EventBatcher"/>:
/// <list type="bullet">
/// <item>INT-04 — a flush whose <see cref="IRynWebView.EmitEvent(string,string)"/> throws the teardown
/// exceptions (<see cref="ObjectDisposedException"/> / <see cref="InvalidOperationException"/>) must NOT
/// crash and must NOT count the un-delivered batch as flushed.</item>
/// <item>ARC-16 — <see cref="EventBatcher"/> is assignable to <see cref="IEventMetrics"/> and its counters
/// reflect added / flushed / dropped.</item>
/// </list>
/// The timer fires every ~16ms; tests drive the flush deterministically via the synchronous
/// <c>FlushNow()</c> seam (same <c>EmitBatch</c> code the timer runs) and additionally confirm the live
/// timer path survives a throwing webview by polling within a bounded budget.
/// </summary>
public sealed class EventBatcherRegressionTests
{
    // ---- ARC-16: metrics surface ----

    [Fact]
    public void EventBatcher_IsAssignableTo_IEventMetrics()
    {
        using var batcher = new EventBatcher(Substitute.For<IRynWebView>(), "evt");
        batcher.Should().BeAssignableTo<IEventMetrics>();
    }

    [Fact]
    public void Counters_ReadThroughIEventMetrics_ReflectAddedAndFlushed()
    {
        var webView = Substitute.For<IRynWebView>();
        using var batcher = new EventBatcher(webView, "evt");

        batcher.Add("{\"a\":1}");
        batcher.Add("{\"a\":2}");
        batcher.Add("{\"a\":3}");

        // Read through the IEventMetrics surface (the point of ARC-16): the counters are observable via the
        // public interface, not just the concrete type.
        ((IEventMetrics)batcher).AddedCount.Should().Be(3);
        ((IEventMetrics)batcher).FlushedCount.Should().Be(0, "nothing has been flushed yet");
        ((IEventMetrics)batcher).DroppedCount.Should().Be(0);

        batcher.FlushNow();

        ((IEventMetrics)batcher).FlushedCount.Should().Be(3, "all enqueued items were emitted once flushed");
        ((IEventMetrics)batcher).DroppedCount.Should().Be(0);
        webView.Received(1).EmitEvent("evt", Arg.Is<string>(s => s.StartsWith('[')));
    }

    [Fact]
    public void Counters_ReflectDropped_WhenOverCapacity()
    {
        var webView = Substitute.For<IRynWebView>();
        using var batcher = new EventBatcher(webView, "evt", capacity: 2);

        batcher.Add("a");
        batcher.Add("b");
        batcher.Add("c"); // over capacity → dropped
        batcher.Add("d"); // over capacity → dropped

        ((IEventMetrics)batcher).AddedCount.Should().Be(2);
        ((IEventMetrics)batcher).DroppedCount.Should().Be(2);
    }

    // ---- INT-04: throwing webview during flush does not crash and does not count as delivered ----

    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(InvalidOperationException))]
    public void FlushNow_WhenEmitThrowsTeardownException_DoesNotCrash_AndDoesNotCountFlushed(Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);

        var webView = Substitute.For<IRynWebView>();
        webView.When(w => w.EmitEvent(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw MakeTeardownException(exceptionType));

        using var batcher = new EventBatcher(webView, "evt");
        batcher.Add("{\"x\":1}");

        var act = () => batcher.FlushNow();
        act.Should().NotThrow("teardown exceptions on the emit path must be swallowed");

        ((IEventMetrics)batcher).AddedCount.Should().Be(1);
        ((IEventMetrics)batcher).FlushedCount.Should().Be(0,
            "a batch dropped on teardown must not be counted as delivered");
    }

    [Fact]
    public void TimerFlush_WhenEmitThrowsTeardownException_DoesNotCrashProcess()
    {
        // Exercise the real 16ms timer path (not FlushNow): a throwing EmitEvent on a background flush must
        // not escape and tear down the process. We add an item, then wait (bounded) for the timer to have
        // attempted at least one emit, asserting no unhandled exception surfaced and nothing was counted.
        var webView = Substitute.For<IRynWebView>();
        var emitAttempts = 0;
        webView.When(w => w.EmitEvent(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ =>
            {
                Interlocked.Increment(ref emitAttempts);
                throw new ObjectDisposedException("webview");
            });

        using var batcher = new EventBatcher(webView, "evt");
        batcher.Add("{\"x\":1}");

        // Poll up to ~2s for the background timer to fire at least once. Deterministic in practice (timer is
        // 16ms) without relying on a fixed sleep.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref emitAttempts) == 0 && DateTime.UtcNow < deadline)
            Thread.Yield();

        emitAttempts.Should().BeGreaterThan(0, "the background flush timer should have attempted an emit");
        ((IEventMetrics)batcher).FlushedCount.Should().Be(0, "every emit threw, so nothing was delivered");
    }

    [Fact]
    public void AfterDispose_AddIsNoOp_AndDoesNotEmit()
    {
        // EmitEvent-via-flush after Dispose is a no-op: the disposed batcher's timer is gone and Add short-
        // circuits, so even a webview that would throw is never touched.
        var webView = Substitute.For<IRynWebView>();
        webView.When(w => w.EmitEvent(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new ObjectDisposedException("webview"));

        var batcher = new EventBatcher(webView, "evt");
        batcher.Dispose();

        var act = () =>
        {
            batcher.Add("{\"x\":1}");
            batcher.FlushNow();
        };
        act.Should().NotThrow();

        webView.DidNotReceive().EmitEvent(Arg.Any<string>(), Arg.Any<string>());
        ((IEventMetrics)batcher).AddedCount.Should().Be(0, "a disposed batcher accepts no new items");
    }

    private static Exception MakeTeardownException(Type t) =>
        t == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("webview")
            : new InvalidOperationException("webview not available");
}
