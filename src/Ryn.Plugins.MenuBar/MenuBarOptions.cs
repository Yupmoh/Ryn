namespace Ryn.Plugins.MenuBar;

public sealed class MenuBarOptions
{
    /// <summary>
    /// Apply the platform-standard App/Edit/Window menu at startup so Cmd-Q, Cmd-C, Cmd-V and friends work
    /// before (or without) an explicit <c>menubar.setMenu</c> call. Effective on macOS only — Windows and
    /// Linux apps conventionally have no menu bar until one is set. Default <c>true</c>.
    /// </summary>
    public bool ApplyDefaultMenu { get; set; } = true;

    /// <summary>
    /// Application name used in role labels such as "Quit {AppName}". Defaults to the process name.
    /// </summary>
    public string? AppName { get; set; }
}
