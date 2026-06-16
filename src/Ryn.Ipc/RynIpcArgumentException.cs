namespace Ryn.Ipc;

/// <summary>
/// Thrown by a generated command router when a JSON argument cannot satisfy the target parameter:
/// a required argument is absent, or a JSON null was supplied for a non-nullable parameter. The
/// message names both the command and the offending parameter so the failure is actionable at the
/// IPC boundary instead of surfacing as an opaque <see cref="System.Collections.Generic.KeyNotFoundException"/>
/// or <see cref="System.NullReferenceException"/> from deep inside the handler.
/// </summary>
public sealed class RynIpcArgumentException : Exception
{
    public RynIpcArgumentException() { }

    public RynIpcArgumentException(string command, string parameter, string reason)
        : base($"Command '{command}' argument '{parameter}': {reason}")
    {
        Command = command;
        Parameter = parameter;
        Reason = reason;
    }

    public RynIpcArgumentException(string message) : base(message) { }

    public RynIpcArgumentException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>The IPC command name whose invocation failed argument binding.</summary>
    public string? Command { get; }

    /// <summary>The camelCase parameter name (as seen in the JSON args) that could not be bound.</summary>
    public string? Parameter { get; }

    /// <summary>A short human-readable reason (e.g. "required argument is missing", "argument must not be null").</summary>
    public string? Reason { get; }
}
