namespace Ryn.Core.Internal;

/// <summary>
/// Default <see cref="IRynApplicationLifetime"/>. Requests an orderly shutdown by closing the live window
/// through <see cref="RynWindowAccessor"/>, which drives the native event loop to unwind so
/// <see cref="RynApplication.RunAsync"/> returns and normal disposal runs (PAP-06).
/// </summary>
/// <remarks>
/// Routed through the accessor rather than holding a <see cref="RynApplication"/> reference because the app is
/// constructed <em>after</em> the DI provider is built, whereas this service is resolvable as soon as the
/// provider exists. <see cref="RynWindow.RequestClose"/> is itself thread-safe and queues onto the UI thread,
/// so this is safe to call from any thread (e.g. an updater running on a thread-pool/IPC thread). A no-op if
/// the window does not exist yet — there is nothing running to shut down.
/// </remarks>
internal sealed class RynApplicationLifetime(RynWindowAccessor accessor) : IRynApplicationLifetime
{
    public void RequestShutdown() => accessor.Window?.RequestClose();
}
