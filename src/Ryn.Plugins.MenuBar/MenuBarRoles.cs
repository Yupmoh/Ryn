namespace Ryn.Plugins.MenuBar;

/// <summary>How a role behaves on platforms without responder-chain dispatch (everywhere but macOS).</summary>
internal enum RoleKind
{
    /// <summary>macOS-only concept (hide, showAll, front, about); no-op elsewhere.</summary>
    MacOnly,
    /// <summary>Quits the application.</summary>
    Quit,
    /// <summary>Acts on the main window (close, minimize, zoom, toggleFullScreen).</summary>
    Window,
    /// <summary>Edit command executed in the webview (undo, cut, copy, paste, ...).</summary>
    Edit,
}

internal sealed record MenuBarRole(
    string Name,
    string MacSelector,
    string DefaultLabel,
    string? DefaultAccelerator,
    RoleKind Kind);

internal static class MenuBarRoles
{
    // "{0}" in DefaultLabel is replaced with the app name. Accelerators use CmdOrCtrl so the same defaults
    // read correctly on every platform.
    private static readonly Dictionary<string, MenuBarRole> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["about"] = new("about", "orderFrontStandardAboutPanel:", "About {0}", null, RoleKind.MacOnly),
        ["quit"] = new("quit", "terminate:", "Quit {0}", "CmdOrCtrl+Q", RoleKind.Quit),
        ["hide"] = new("hide", "hide:", "Hide {0}", "Cmd+H", RoleKind.MacOnly),
        ["hideOthers"] = new("hideOthers", "hideOtherApplications:", "Hide Others", "Cmd+Alt+H", RoleKind.MacOnly),
        ["showAll"] = new("showAll", "unhideAllApplications:", "Show All", null, RoleKind.MacOnly),
        ["undo"] = new("undo", "undo:", "Undo", "CmdOrCtrl+Z", RoleKind.Edit),
        ["redo"] = new("redo", "redo:", "Redo", "CmdOrCtrl+Shift+Z", RoleKind.Edit),
        ["cut"] = new("cut", "cut:", "Cut", "CmdOrCtrl+X", RoleKind.Edit),
        ["copy"] = new("copy", "copy:", "Copy", "CmdOrCtrl+C", RoleKind.Edit),
        ["paste"] = new("paste", "paste:", "Paste", "CmdOrCtrl+V", RoleKind.Edit),
        ["selectAll"] = new("selectAll", "selectAll:", "Select All", "CmdOrCtrl+A", RoleKind.Edit),
        ["delete"] = new("delete", "delete:", "Delete", null, RoleKind.Edit),
        ["minimize"] = new("minimize", "performMiniaturize:", "Minimize", "CmdOrCtrl+M", RoleKind.Window),
        ["zoom"] = new("zoom", "performZoom:", "Zoom", null, RoleKind.Window),
        ["close"] = new("close", "performClose:", "Close Window", "CmdOrCtrl+W", RoleKind.Window),
        ["front"] = new("front", "arrangeInFront:", "Bring All to Front", null, RoleKind.MacOnly),
        ["toggleFullScreen"] = new("toggleFullScreen", "toggleFullScreen:", "Toggle Full Screen", "Ctrl+Cmd+F", RoleKind.Window),
    };

    public static bool TryGet(string role, out MenuBarRole result) => Roles.TryGetValue(role, out result!);

    public static string ResolveLabel(MenuBarRole role, string? explicitLabel, string appName) =>
        explicitLabel ?? string.Format(System.Globalization.CultureInfo.InvariantCulture, role.DefaultLabel, appName);
}
