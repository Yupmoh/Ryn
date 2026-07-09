namespace Ryn.Plugins.MenuBar.Backends;

// Linux (and anything else): no menu bar backend. GTK apps conventionally use header bars, and saucer owns
// the GtkWindow's child hierarchy, so injecting a GtkMenuBar is deliberately out of scope for now.
#pragma warning disable CS0067 // Events unused on stub — required by interface
internal sealed class StubMenuBarBackend : IMenuBarBackend
{
    public event Action<string>? MenuItemClicked;
    public event Action<string>? RoleActivated;

    public void SetMenu(IReadOnlyList<MenuBarItem> items) { }
    public void Dispose() { }
}
