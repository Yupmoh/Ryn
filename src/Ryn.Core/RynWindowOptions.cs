namespace Ryn.Core;

/// <summary>
/// Per-window configuration passed to <see cref="RynApplication.OpenWindow(RynWindowOptions)"/> and the
/// <c>window.open</c> IPC command. It is the per-window subset of <see cref="RynOptions"/> — the application
/// already exists when a window is opened, so app-global settings (application id, deep-link schemes, embedded
/// content, exception/logging policy) live only on <see cref="RynOptions"/> and are not repeated here.
/// </summary>
public sealed class RynWindowOptions
{
    /// <summary>The window title text.</summary>
    public string Title { get; set; } = "Ryn Window";

    /// <summary>Initial window width in pixels.</summary>
    public int Width { get; set; } = 800;

    /// <summary>Initial window height in pixels.</summary>
    public int Height { get; set; } = 600;

    /// <summary>Minimum width in pixels the user can resize to (0 = no minimum). Enforced natively.</summary>
    public int MinWidth { get; set; }

    /// <summary>Minimum height in pixels the user can resize to (0 = no minimum). Enforced natively.</summary>
    public int MinHeight { get; set; }

    /// <summary>Maximum width in pixels the user can resize to (0 = no maximum). Enforced natively.</summary>
    public int MaxWidth { get; set; }

    /// <summary>Maximum height in pixels the user can resize to (0 = no maximum). Enforced natively.</summary>
    public int MaxHeight { get; set; }

    /// <summary>Whether the window can be resized by the user.</summary>
    public bool Resizable { get; set; } = true;

    /// <summary>Controls the window title bar appearance.</summary>
    public TitleBarStyle TitleBarStyle { get; set; } = TitleBarStyle.Native;

    /// <summary>Whether the window background is transparent.</summary>
    public bool Transparent { get; set; }

    /// <summary>Whether the webview renders with GPU hardware acceleration (default true). Set false only as a
    /// compatibility escape hatch for flaky GPU drivers or headless/virtualized environments — it makes canvas,
    /// WebGL and WebGPU much slower. Applied once, before the webview is created.</summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>Engine-specific webview flags applied before creation — the lever for experimental rendering
    /// features (e.g. <c>--enable-unsafe-webgpu</c> on Windows/Chromium). Syntax is not portable across
    /// platforms; see <see cref="RynOptions.BrowserFlags"/> for the per-engine format.</summary>
    public IList<string> BrowserFlags { get; } = new List<string>();

    /// <summary>URL to navigate to on open. Mutually exclusive with <see cref="Html"/> and <see cref="ContentDirectory"/>.</summary>
    public Uri? Url { get; set; }

    /// <summary>Raw HTML string to load on open. Mutually exclusive with <see cref="Url"/> and <see cref="ContentDirectory"/>.</summary>
    public string? Html { get; set; }

    /// <summary>Directory of static web content to serve via the ryn:// scheme.</summary>
    public string? ContentDirectory { get; set; }

    /// <summary>Whether to serve content through a local HTTP server instead of the custom scheme.</summary>
    public bool UseLocalServer { get; set; }

    /// <summary>Fixed loopback port for the local content/IPC server. Falls back to a free port if taken.</summary>
    public int LocalServerPort { get; set; } = 7421;

    /// <summary>Path to the window icon file.</summary>
    public string? IconPath { get; set; }

    /// <summary>Whether browser developer tools are enabled for this window.</summary>
    public bool DevTools { get; set; }

    /// <summary>Whether to save and restore this window's position and size. Secondary windows persist under a
    /// per-window key so they do not collide with the main window's state.</summary>
    public bool PersistWindowState { get; set; }

    /// <summary>Additional origins allowed to access IPC commands from this window.</summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>Custom webview URL schemes to attach handlers for. Must already be registered (declared on the
    /// main window's <see cref="RynOptions.CustomSchemes"/>) before the first webview was created.</summary>
    public IList<string> CustomSchemes { get; } = new List<string>();

    /// <summary>
    /// Projects these per-window options onto a <see cref="RynOptions"/> instance for the window to consume.
    /// App-global fields are left at their defaults — the application already exists, so they are unused for a
    /// secondary window; the host supplies a per-window state key separately.
    /// </summary>
    internal RynOptions ToRynOptions()
    {
        var options = new RynOptions
        {
            Title = Title,
            Width = Width,
            Height = Height,
            MinWidth = MinWidth,
            MinHeight = MinHeight,
            MaxWidth = MaxWidth,
            MaxHeight = MaxHeight,
            Resizable = Resizable,
            TitleBarStyle = TitleBarStyle,
            Transparent = Transparent,
            HardwareAcceleration = HardwareAcceleration,
            Url = Url,
            Html = Html,
            ContentDirectory = ContentDirectory,
            UseLocalServer = UseLocalServer,
            LocalServerPort = LocalServerPort,
            IconPath = IconPath,
            DevTools = DevTools,
            PersistWindowState = PersistWindowState,
        };
        foreach (var origin in AllowedOrigins) options.AllowedOrigins.Add(origin);
        foreach (var scheme in CustomSchemes) options.CustomSchemes.Add(scheme);
        foreach (var flag in BrowserFlags) options.BrowserFlags.Add(flag);
        return options;
    }
}
