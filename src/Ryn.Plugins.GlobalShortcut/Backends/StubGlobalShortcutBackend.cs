namespace Ryn.Plugins.GlobalShortcut.Backends;

// Linux (and anything else): no global hotkey backend yet. XGrabKey only works under X11 (not Wayland,
// where the xdg-desktop-portal GlobalShortcuts interface is the sanctioned route); until that portal
// support lands, registration honestly reports failure instead of silently never firing.
#pragma warning disable CS0067 // Events unused on stub — required by interface
internal sealed class StubGlobalShortcutBackend : IGlobalShortcutBackend
{
    public event Action<string>? Activated;

    public bool Register(ParsedAccelerator accelerator, string canonical) => false;
    public bool Unregister(string canonical) => false;
    public void Dispose() { }
}
