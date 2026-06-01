namespace Ryn.Core;

/// <summary>Carries an exception surfaced by <see cref="RynApplication.UnhandledException"/>.</summary>
public sealed class RynUnhandledExceptionEventArgs(Exception exception) : EventArgs
{
    /// <summary>The unhandled exception.</summary>
    public Exception Exception { get; } = exception;
}
