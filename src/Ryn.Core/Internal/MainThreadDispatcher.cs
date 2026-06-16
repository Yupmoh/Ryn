namespace Ryn.Core.Internal;

/// <summary>
/// Default <see cref="IMainThreadDispatcher"/>. Routes work to the live <see cref="RynWindow"/>'s main-thread
/// post (<see cref="RynWindow.PostToUi"/> / <see cref="RynWindow.InvokeOnUiAsync"/>), using
/// <see cref="RynWindowAccessor"/> so a caller that fires <b>before the window object exists</b> (e.g. a plugin
/// backend during its <c>InitializeAsync</c>, which runs ahead of window creation) is still served.
/// </summary>
/// <remarks>
/// <para>
/// The buffering is two-staged on purpose. Until the window <em>object</em> is published, work is queued in the
/// accessor via <see cref="RynWindowAccessor.OnReady"/>. When the window is published (on the main thread) the
/// queued closure runs and forwards to <see cref="RynWindow.PostToUi"/>; at that instant the <em>native</em>
/// application usually does not exist yet, so the window buffers it a second time and drains it — in order, on
/// the UI thread — once its native loop is up. The net effect: a pre-loop <see cref="Post"/> always lands on the
/// UI thread once the loop starts, and a <see cref="Post"/> from the UI thread itself runs inline.
/// </para>
/// <para>
/// Registered as a DI singleton by <see cref="RynApplicationBuilder.Build"/> so any service or plugin backend
/// can resolve <see cref="IMainThreadDispatcher"/> and fence its native UI calls onto the main thread.
/// </para>
/// </remarks>
internal sealed class MainThreadDispatcher(RynWindowAccessor accessor) : IMainThreadDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Window object already live → forward straight to its UI post (which itself runs inline when we are on
        // the UI thread, or buffers/posts otherwise). Otherwise queue until the window is published; the queued
        // closure then forwards to PostToUi, which handles the native-not-ready-yet case. The OnReady token is
        // only needed to cancel a queued attach (subscribe/unsubscribe); fire-and-forget work never cancels.
        _ = accessor.OnReady(window => window.PostToUi(action));
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Fast path: window already live → return its real completion task so callers genuinely await the work.
        if (accessor.Window is { } live)
            return live.InvokeOnUiAsync(action);

        // Window not up yet: bridge OnReady (which fires on the main thread when the window publishes) to a
        // TaskCompletionSource so the returned task still completes when the deferred work actually runs.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = accessor.OnReady(window =>
        {
            // Chain the window's UI-post task to our TCS so faults/cancellation propagate to the awaiter.
            _ = window.InvokeOnUiAsync(action).ContinueWith(
                static (t, state) =>
                {
                    var source = (TaskCompletionSource)state!;
                    if (t.IsFaulted) source.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) source.TrySetCanceled();
                    else source.TrySetResult();
                },
                tcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });
        return tcs.Task;
    }
}
