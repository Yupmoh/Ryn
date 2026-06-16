namespace Ryn.Core;

/// <summary>
/// Marshals work onto the application's main/UI thread — the thread that runs the native event loop and the
/// only thread on which native UI toolkits (macOS AppKit, Linux GTK, Windows message pump) may be touched.
/// </summary>
/// <remarks>
/// <para>
/// Resolve this from DI (it is registered as a singleton by <see cref="RynApplication"/>) and use it to fence
/// any native UI call made from a non-UI thread — e.g. an IPC command running on a thread-pool thread that
/// mutates an <c>NSStatusItem</c>/<c>NSMenu</c> (tray) or an <c>NSSound</c> (audio). Touching those APIs off
/// the main thread is undefined behavior; routing through this dispatcher makes the call safe.
/// </para>
/// <para>
/// Both members are safe to call from any thread, and may be called <b>before</b> the window/event loop is up:
/// the action is queued and runs (in submission order) on the UI thread once the loop starts. When the caller
/// is already on the UI thread the action runs inline (synchronously), so ordering relative to surrounding
/// UI-thread work is preserved and there is no deferral. After the application has shut down, posted actions
/// are dropped (<see cref="Post"/> is a no-op and <see cref="InvokeAsync"/> completes without running).
/// </para>
/// </remarks>
public interface IMainThreadDispatcher
{
    /// <summary>
    /// Queues <paramref name="action"/> to run on the UI thread, returning immediately (fire-and-forget). Runs
    /// inline if the caller is already on the UI thread. Exceptions thrown by <paramref name="action"/> are
    /// routed to the application's unhandled-exception surface rather than crossing the native boundary.
    /// </summary>
    public void Post(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the UI thread and returns a task that completes when it has
    /// actually run (or faults if it throws). Runs inline and returns a completed task if the caller is already
    /// on the UI thread. If the application is not (or no longer) running the task completes without running.
    /// </summary>
    public Task InvokeAsync(Action action);
}
