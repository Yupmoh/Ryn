namespace Ryn.Core;

/// <summary>
/// Opens and enumerates application windows at runtime. Injected into command classes (and resolvable by any
/// service) so code can open secondary windows without holding a <see cref="RynApplication"/> reference.
/// </summary>
public interface IRynWindowManager
{
    /// <summary>
    /// Opens a new window and returns it. Safe to call from any thread; native window creation is marshalled
    /// onto the UI thread and the call blocks until the window exists. Throws if the application is not running.
    /// </summary>
    public IRynWindow OpenWindow(RynWindowOptions options);

    /// <summary>
    /// Opens a new window without blocking, completing once it has been created on the UI thread. Throws if the
    /// application is not running.
    /// </summary>
    public Task<IRynWindow> OpenWindowAsync(RynWindowOptions options);

    /// <summary>All currently-open windows, main first. Empty before the application is running.</summary>
    public IReadOnlyList<IRynWindow> Windows { get; }
}
