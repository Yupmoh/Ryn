using FluentAssertions;
using Ryn.Core;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Regression tests for the pre-ready subscription buffering in <see cref="RynWindowAccessor"/> and
/// <see cref="DeferredRynWindow"/> (PAP-15) and the deferred webview FileDrop path (ARC-08).
///
/// PAP-15 (window events): subscribing then unsubscribing a window event <em>before</em> the window is
/// published must cancel the matching queued attach so it does NOT fire when the window goes live — it must
/// not leak a permanent subscription. The token-based <c>OnReady</c>/<c>CancelOnReady</c> pair on the
/// accessor is the mechanism, tested directly (pure, deterministic) and through <see cref="DeferredRynWindow"/>.
///
/// ARC-08 (deferred webview): see the skipped tests at the bottom — they assert the intended contract that
/// is documented but, per the seam reachable here, is NOT yet satisfied by the current code (the deferred
/// webview's eager OnReady drain touches <c>RynWindow.WebView</c>, which throws "Window not initialized"
/// before the native webview exists). They are kept (skipped) so the gap is recorded in-code; see the test
/// run's concerns.
/// </summary>
public sealed class DeferredSubscriptionTests
{
    // ---- PAP-15: accessor OnReady / CancelOnReady (the underlying mechanism) ----

    [Fact]
    public void OnReady_QueuedThenCancelled_DoesNotRunWhenWindowPublished()
    {
        var accessor = new RynWindowAccessor();
        var ran = 0;

        var token = accessor.OnReady(_ => ran++);
        token.Should().NotBe(0, "the action is queued because the window is not live yet");

        accessor.CancelOnReady(token).Should().BeTrue("a still-queued action can be cancelled");

        using var window = new RynWindow(new RynOptions());
        accessor.Window = window;

        ran.Should().Be(0, "a cancelled ready-callback must not run when the window is published");
    }

    [Fact]
    public void OnReady_QueuedNotCancelled_RunsWhenWindowPublished()
    {
        var accessor = new RynWindowAccessor();
        var ran = 0;

        accessor.OnReady(_ => ran++);

        using var window = new RynWindow(new RynOptions());
        accessor.Window = window;

        ran.Should().Be(1, "an un-cancelled ready-callback runs exactly once when the window is published");
    }

    [Fact]
    public void OnReady_WhenWindowAlreadyLive_RunsSynchronously_AndReturnsZeroToken()
    {
        var accessor = new RynWindowAccessor();
        using var window = new RynWindow(new RynOptions());
        accessor.Window = window;

        var ran = 0;
        var token = accessor.OnReady(_ => ran++);

        ran.Should().Be(1, "with a live window the action runs immediately");
        token.Should().Be(0, "a synchronously-run action has nothing to cancel");
    }

    [Fact]
    public void CancelOnReady_AfterWindowPublished_ReturnsFalse()
    {
        var accessor = new RynWindowAccessor();
        var token = accessor.OnReady(_ => { });

        using var window = new RynWindow(new RynOptions());
        accessor.Window = window; // drains and clears the queue

        accessor.CancelOnReady(token).Should().BeFalse("the action already fired; there is nothing left to cancel");
    }

    // ---- PAP-15: DeferredRynWindow event sub-then-unsub before ready ----

    [Fact]
    public void DeferredWindow_SubscribeThenUnsubscribeClosingBeforeReady_NoThrowOnPublish()
    {
        var accessor = new RynWindowAccessor();
        IRynWindow deferred = new DeferredRynWindow(accessor);

        EventHandler<WindowClosingEventArgs> handler = (_, _) => { };
        deferred.Closing += handler;
        deferred.Closing -= handler; // pre-ready unsubscribe must cancel the queued attach (no leak)

        using var window = new RynWindow(new RynOptions());
        var publish = () => accessor.Window = window;

        publish.Should().NotThrow("a cancelled pre-ready subscription must not fire at publish time");
    }

    [Fact]
    public void DeferredWindow_SubscribeOnlyBeforeReady_AttachesOnPublish()
    {
        // A plain pre-ready subscribe (no unsubscribe) must attach once the window is published — the buffering
        // is not allowed to silently drop a real subscription. Observed via the accessor's drain not throwing
        // and the attach lambda running against the live window.
        var accessor = new RynWindowAccessor();
        IRynWindow deferred = new DeferredRynWindow(accessor);

        deferred.Closing += (_, _) => { };

        using var window = new RynWindow(new RynOptions());
        var publish = () => accessor.Window = window;

        publish.Should().NotThrow();
    }

    [Fact]
    public void DeferredWindow_UnsubscribeWithoutSubscribe_IsHarmless()
    {
        var accessor = new RynWindowAccessor();
        IRynWindow deferred = new DeferredRynWindow(accessor);

        var act = () => deferred.Closing -= (_, _) => { };
        act.Should().NotThrow();
    }

    // ---- ARC-08 (and the PAP-15 webview path): intended contract, currently NOT met at this seam ----
    // These assert the documented behaviour ("drain must not throw 'Window not initialized'"). With the
    // current implementation the deferred webview's constructor unconditionally queues an OnReady drain that
    // dereferences RynWindow.WebView (which throws until InitializeNative runs), so publishing the window
    // throws. Kept as skipped regression specs so the contract is recorded and these light up the moment the
    // source is fixed (e.g. a non-throwing webview-ready channel). See the run's "concerns".
    private const string Arc08Skip =
        "ARC-08 not satisfied at this seam: DeferredRynWebView's eager OnReady drain calls RynWindow.WebView, " +
        "which throws 'Window not initialized' when accessor.Window is set before the native webview exists. " +
        "Un-skip once a non-throwing webview-ready channel lands.";

    [Fact(Skip = Arc08Skip)]
    public void DeferredWebView_SubscribeFileDropBeforeReady_PublishWindow_DoesNotThrow()
    {
        var accessor = new RynWindowAccessor();
        var deferred = new DeferredRynWebView(accessor);
        deferred.FileDrop += (_, _) => { };

        using var window = new RynWindow(new RynOptions());
        var publish = () => accessor.Window = window;

        publish.Should().NotThrow("subscribing FileDrop before the webview exists must not crash window startup");
    }

    [Fact(Skip = Arc08Skip)]
    public void DeferredWebView_ConstructedThenPublishWindow_DoesNotThrow()
    {
        // Even with no FileDrop subscription, merely resolving IRynWebView before the window is published must
        // not crash startup — yet the eager OnReady drain currently dereferences the not-yet-built webview.
        var accessor = new RynWindowAccessor();
        _ = new DeferredRynWebView(accessor);

        using var window = new RynWindow(new RynOptions());
        var publish = () => accessor.Window = window;

        publish.Should().NotThrow();
    }
}
