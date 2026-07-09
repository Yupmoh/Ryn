namespace Ryn.Plugins.GlobalShortcut;

internal interface IGlobalShortcutBackend : IDisposable
{
    /// <summary>
    /// Registers a system-wide hotkey. <paramref name="canonical"/> is the parser's canonical form and is
    /// echoed back through <see cref="Activated"/>. Returns <c>false</c> when the key can't be mapped or the
    /// OS rejects the registration (typically because another app owns it).
    /// </summary>
    public bool Register(ParsedAccelerator accelerator, string canonical);

    /// <summary>Removes a previously registered hotkey. Returns <c>false</c> if it wasn't registered.</summary>
    public bool Unregister(string canonical);

    /// <summary>Raised with the canonical accelerator when a registered hotkey fires.</summary>
    public event Action<string>? Activated;
}
