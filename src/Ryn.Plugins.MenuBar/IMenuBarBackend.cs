namespace Ryn.Plugins.MenuBar;

internal interface IMenuBarBackend : IDisposable
{
    /// <summary>
    /// Replaces the application menu with <paramref name="items"/> (top-level roles already expanded).
    /// An empty list removes the menu where the platform allows it.
    /// </summary>
    public void SetMenu(IReadOnlyList<MenuBarItem> items);

    /// <summary>Raised with the item id when a custom (non-role) item is clicked.</summary>
    public event Action<string>? MenuItemClicked;

    /// <summary>
    /// Raised with the role name when a role item needs Ryn to perform the behavior. macOS never raises this
    /// (roles dispatch natively through the responder chain); Windows raises it for every role click.
    /// </summary>
    public event Action<string>? RoleActivated;
}
