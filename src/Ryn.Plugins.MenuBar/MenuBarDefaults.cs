namespace Ryn.Plugins.MenuBar;

/// <summary>
/// Builds the platform-standard menus and expands the top-level convenience roles (<c>appMenu</c>,
/// <c>editMenu</c>, <c>windowMenu</c>) into concrete item trees, so backends only ever see item-level roles.
/// </summary>
internal static class MenuBarDefaults
{
    /// <summary>The full standard menu set: App (macOS), Edit, Window.</summary>
    public static IReadOnlyList<MenuBarItem> CreateDefault(string appName) =>
    [
        CreateAppMenu(appName),
        CreateEditMenu(),
        CreateWindowMenu(),
    ];

    /// <summary>
    /// Replaces top-level <c>appMenu</c>/<c>editMenu</c>/<c>windowMenu</c> roles with the standard menus;
    /// everything else passes through untouched.
    /// </summary>
    public static IReadOnlyList<MenuBarItem> ExpandTopLevelRoles(IReadOnlyList<MenuBarItem> items, string appName)
    {
        List<MenuBarItem>? expanded = null;
        for (var i = 0; i < items.Count; i++)
        {
            var replacement = items[i].Role switch
            {
                "appMenu" => CreateAppMenu(appName),
                "editMenu" => CreateEditMenu(),
                "windowMenu" => CreateWindowMenu(),
                _ => null,
            };
            if (replacement is not null && expanded is null)
            {
                expanded = [.. items.Take(i)];
            }
            expanded?.Add(replacement ?? items[i]);
        }
        return expanded ?? items;
    }

    private static MenuBarItem CreateAppMenu(string appName) => new()
    {
        Label = appName,
        Items =
        [
            new MenuBarItem { Role = "about" },
            new MenuBarItem { Separator = true },
            new MenuBarItem { Role = "hide" },
            new MenuBarItem { Role = "hideOthers" },
            new MenuBarItem { Role = "showAll" },
            new MenuBarItem { Separator = true },
            new MenuBarItem { Role = "quit" },
        ],
    };

    private static MenuBarItem CreateEditMenu() => new()
    {
        Label = "Edit",
        Items =
        [
            new MenuBarItem { Role = "undo" },
            new MenuBarItem { Role = "redo" },
            new MenuBarItem { Separator = true },
            new MenuBarItem { Role = "cut" },
            new MenuBarItem { Role = "copy" },
            new MenuBarItem { Role = "paste" },
            new MenuBarItem { Role = "selectAll" },
        ],
    };

    private static MenuBarItem CreateWindowMenu() => new()
    {
        Label = "Window",
        Items =
        [
            new MenuBarItem { Role = "minimize" },
            new MenuBarItem { Role = "zoom" },
            new MenuBarItem { Separator = true },
            new MenuBarItem { Role = "front" },
            new MenuBarItem { Separator = true },
            new MenuBarItem { Role = "close" },
        ],
    };
}
