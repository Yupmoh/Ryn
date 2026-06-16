namespace Ryn.Core;

/// <summary>
/// Lets a service or plugin request an orderly application shutdown without calling
/// <see cref="System.Environment.Exit(int)"/>. Resolve it from DI (registered as a singleton by
/// <see cref="RynApplication"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequestShutdown"/> closes the window on the UI thread, which lets the native event loop unwind
/// so <see cref="RynApplication.RunAsync"/> returns and the normal disposal chain runs
/// (<c>plugins → webview → window → app</c>, including window-state save and any plugin
/// <see cref="System.IAsyncDisposable"/>/<see cref="System.IDisposable"/> cleanup). This is the supported way
/// for an in-app component — for example an auto-updater that must relaunch the app — to stop the process
/// cleanly instead of hard-exiting from a background thread and skipping disposal.
/// </para>
/// <para>
/// Safe to call from any thread and idempotent. Returns immediately; shutdown proceeds asynchronously on the
/// UI thread. A caller that needs to block until the app has fully stopped should await
/// <see cref="IRynWindow.WaitForCloseAsync"/>.
/// </para>
/// </remarks>
public interface IRynApplicationLifetime
{
    /// <summary>
    /// Requests an orderly shutdown: closes the window on the UI thread so the event loop exits and
    /// <see cref="RynApplication.RunAsync"/> returns, running normal disposal. No-op if the application is not
    /// running or has already begun shutting down.
    /// </summary>
    public void RequestShutdown();
}
