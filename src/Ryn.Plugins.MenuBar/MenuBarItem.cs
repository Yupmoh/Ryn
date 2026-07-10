namespace Ryn.Plugins.MenuBar;

/// <summary>
/// One entry in the application menu. Top-level items are the menus themselves (a <see cref="Label"/> plus
/// <see cref="Items"/>); nested items are the entries inside a menu. An item is one of four shapes:
/// a <b>role</b> item (<see cref="Role"/> set — behavior, label, and accelerator come from the platform),
/// a <b>custom</b> item (<see cref="Id"/> + <see cref="Label"/> — clicking raises <c>menubar.itemClicked</c>),
/// a <b>separator</b> (<see cref="Separator"/> is <c>true</c>), or a <b>submenu</b> (<see cref="Items"/> set).
/// </summary>
public sealed class MenuBarItem
{
    /// <summary>Identifier reported via the <c>menubar.itemClicked</c> event when a custom item is clicked.</summary>
    public string? Id { get; init; }

    /// <summary>Display text. Optional for role items, which fall back to the platform-standard label.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// A platform-standard behavior: <c>about</c>, <c>quit</c>, <c>hide</c>, <c>hideOthers</c>, <c>showAll</c>,
    /// <c>undo</c>, <c>redo</c>, <c>cut</c>, <c>copy</c>, <c>paste</c>, <c>selectAll</c>, <c>delete</c>,
    /// <c>minimize</c>, <c>zoom</c>, <c>close</c>, <c>front</c>, or <c>toggleFullScreen</c>. Top-level items
    /// additionally accept <c>appMenu</c>, <c>editMenu</c>, and <c>windowMenu</c>, which expand to the full
    /// platform-standard menus. On macOS roles dispatch through the responder chain (so <c>copy</c> reaches
    /// native fields and the webview alike); elsewhere Ryn emulates them.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Keyboard shortcut, e.g. <c>"CmdOrCtrl+Shift+A"</c>. Modifiers: <c>Cmd</c>, <c>Ctrl</c>, <c>CmdOrCtrl</c>,
    /// <c>Alt</c>, <c>Shift</c>. On macOS this is a live key equivalent; on Windows it is displayed in the menu
    /// (keys reach the focused webview, where the app can also handle them).
    /// </summary>
    public string? Accelerator { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Separator { get; init; }

    /// <summary>Child items; set on top-level menus and submenus.</summary>
    public IReadOnlyList<MenuBarItem>? Items { get; init; }

    /// <summary>
    /// Whether this nested item is a submenu (has children) versus a clickable leaf. An item that arrives
    /// with an <b>empty</b> <see cref="Items"/> list — the shape a JSON <c>items: []</c> deserializes to,
    /// which frontends emit for every leaf via <c>children.map()</c> — is a leaf, not a dead submenu. Only
    /// consulted for nested items; a top-level entry is always a menu.
    /// </summary>
    internal bool IsSubmenu => Items is { Count: > 0 };
}
