namespace Ryn.Core.Internal;

/// <summary>
/// Default <see cref="IRynApplicationLifetime"/>. Requests an orderly shutdown through the live
/// <see cref="NativeAppHost"/>, which closes every window so the native event loop unwinds,
/// <see cref="RynApplication.RunAsync"/> returns, and normal disposal runs (PAP-06).
/// </summary>
/// <remarks>
/// Routed through <see cref="NativeApplicationAccessor"/> rather than holding a <see cref="RynApplication"/>
/// reference because the app is constructed <em>after</em> the DI provider is built, whereas this service is
/// resolvable as soon as the provider exists. The host's shutdown is itself thread-safe and queues onto the UI
/// thread, so this is safe to call from any thread (e.g. an updater on a thread-pool/IPC thread). A no-op if
/// the host does not exist yet — there is nothing running to shut down.
/// </remarks>
internal sealed class RynApplicationLifetime(NativeApplicationAccessor accessor) : IRynApplicationLifetime
{
    public void RequestShutdown() => accessor.Host?.RequestShutdown();
}
