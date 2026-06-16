namespace Ryn.Core.Internal;

/// <summary>
/// Default <see cref="IRynWindowManager"/>. Forwards to the live <see cref="NativeAppHost"/> via
/// <see cref="NativeApplicationAccessor"/>, so it is resolvable from DI before the loop is up and never holds a
/// stale window reference. Throws on open while the application is not running; reports an empty window list.
/// </summary>
internal sealed class RynWindowManager(NativeApplicationAccessor accessor) : IRynWindowManager
{
    public IRynWindow OpenWindow(RynWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = accessor.Host
            ?? throw new InvalidOperationException("Cannot open a window before the application is running.");
        return host.OpenWindow(options);
    }

    public Task<IRynWindow> OpenWindowAsync(RynWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = accessor.Host
            ?? throw new InvalidOperationException("Cannot open a window before the application is running.");
        return host.OpenWindowAsync(options);
    }

    public IReadOnlyList<IRynWindow> Windows => accessor.Host?.Windows ?? [];
}
