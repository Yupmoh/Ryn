using Ryn.Core.Internal;

namespace Ryn.Core;

/// <summary>
/// Resolves the window an IPC command originated from, so a command (e.g. the built-in <c>window.*</c> set)
/// acts on the window whose page invoked it rather than always on the main window. Injected into command
/// classes via DI. Returns the ambient originating window when called on an IPC dispatch path, falling back to
/// the main window for any other caller (or before the ambient is set).
/// </summary>
public sealed class CurrentWindowAccessor
{
    private readonly NativeApplicationAccessor _appAccessor;

    internal CurrentWindowAccessor(NativeApplicationAccessor appAccessor) => _appAccessor = appAccessor;

    /// <summary>
    /// The window the in-flight IPC command came from, or the main window when there is no originating window
    /// (a non-IPC caller, or the ambient has not been set). Throws if the application is not running.
    /// </summary>
    public IRynWindow Current =>
        CurrentWindow.Value
        ?? _appAccessor.Host?.MainWindow
        ?? throw new InvalidOperationException("No window is available; the application is not running.");
}
