namespace Ryn.Core.Internal;

/// <summary>
/// Default <see cref="IMainThreadDispatcher"/>. Marshals work onto the application's UI thread through the
/// live <see cref="NativeAppHost"/> (published on <see cref="NativeApplicationAccessor"/> before plugins
/// initialize), so a caller that fires <b>before the native loop is up</b> — e.g. a plugin backend during its
/// <c>InitializeAsync</c> — is still served: the host buffers the work and drains it, in submission order, on
/// the UI thread once the loop starts.
/// </summary>
/// <remarks>
/// Registered as a DI singleton by <see cref="RynApplicationBuilder.Build"/> so any service or plugin backend
/// can resolve <see cref="IMainThreadDispatcher"/> and fence its native UI calls onto the main thread. A no-op
/// when no host exists yet (the app was never run) — there is no event loop to post to.
/// </remarks>
internal sealed class MainThreadDispatcher(NativeApplicationAccessor accessor) : IMainThreadDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        accessor.Host?.PostToUi(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return accessor.Host?.InvokeOnUiAsync(action) ?? Task.CompletedTask;
    }
}
