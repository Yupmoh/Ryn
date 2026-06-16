namespace Ryn.Core.Internal;

/// <summary>
/// Holds process-global native application state for services resolved from DI: the raw application handle
/// (for advanced native interop) and the live <see cref="NativeAppHost"/>. The host is published by
/// <see cref="RynApplication.RunAsync"/> before plugins initialize, so <see cref="IMainThreadDispatcher"/>
/// and <see cref="IRynApplicationLifetime"/> can marshal work onto — and request shutdown of — the one
/// application/loop even from a plugin backend running before the loop is up.
/// </summary>
internal sealed class NativeApplicationAccessor
{
    internal nint ApplicationHandle { get; set; }

    /// <summary>The live application host, or null before <see cref="RynApplication.RunAsync"/> creates it.</summary>
    internal NativeAppHost? Host { get; set; }
}
