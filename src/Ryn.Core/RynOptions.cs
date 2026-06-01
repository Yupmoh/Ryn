namespace Ryn.Core;

/// <summary>Configuration options for a Ryn application window and webview.</summary>
public sealed class RynOptions
{
    /// <summary>Reverse-DNS application identifier (e.g. "com.example.myapp").</summary>
    public string ApplicationId { get; set; } = "com.ryn.app";

    /// <summary>The application window title.</summary>
    public string Title { get; set; } = "Ryn Application";

    /// <summary>Initial window width in pixels.</summary>
    public int Width { get; set; } = 800;

    /// <summary>Initial window height in pixels.</summary>
    public int Height { get; set; } = 600;

    /// <summary>Whether the window can be resized by the user.</summary>
    public bool Resizable { get; set; } = true;

    /// <summary>Controls the window title bar appearance.</summary>
    public TitleBarStyle TitleBarStyle { get; set; } = TitleBarStyle.Native;

    /// <summary>Whether the window background is transparent.</summary>
    public bool Transparent { get; set; }

    /// <summary>URL to navigate to on startup. Mutually exclusive with <see cref="Html"/> and <see cref="ContentDirectory"/>.</summary>
    public Uri? Url { get; set; }

    /// <summary>Raw HTML string to load on startup. Mutually exclusive with <see cref="Url"/> and <see cref="ContentDirectory"/>.</summary>
    public string? Html { get; set; }

    /// <summary>Directory containing static web content (HTML/CSS/JS) to serve via the ryn:// scheme.</summary>
    public string? ContentDirectory { get; set; }

    /// <summary>Whether to serve content through a local HTTP server instead of the custom scheme.</summary>
    public bool UseLocalServer { get; set; }

    /// <summary>
    /// Deprecated: the local server is plain HTTP on loopback (which is already a browser "secure context",
    /// so crypto/clipboard APIs work). Kept for source compatibility; setting it has no effect.
    /// </summary>
    public bool UseHttps { get; set; }

    /// <summary>
    /// Fixed loopback port for the local content/IPC server (<see cref="UseLocalServer"/>). A stable port
    /// means a stable <c>http://localhost:{port}</c> origin you can whitelist in your API's CORS config.
    /// Falls back to nearby ports, then an OS-assigned port, if taken. Default 7421.
    /// </summary>
    public int LocalServerPort { get; set; } = 7421;

    /// <summary>Path to the window icon file.</summary>
    public string? IconPath { get; set; }

    /// <summary>Whether browser developer tools are enabled.</summary>
    public bool DevTools { get; set; }

    /// <summary>Whether to extract and serve embedded content resources.</summary>
    public bool UseEmbeddedContent { get; set; }

    /// <summary>Whether to automatically save and restore window position and size.</summary>
    public bool PersistWindowState { get; set; }

    /// <summary>
    /// When true, install process-global handlers for AppDomain-unhandled and unobserved-task exceptions
    /// (in addition to the event-loop catch), surfaced via <see cref="RynApplication.UnhandledException"/>.
    /// Lets an app install a crash logger. Default false.
    /// </summary>
    public bool CaptureUnhandledExceptions { get; set; }

    /// <summary>Custom URL schemes to register for deep linking (e.g., "myapp").</summary>
    public IList<string> DeepLinkSchemes { get; } = new List<string>();

    /// <summary>Additional origins allowed to access IPC commands.</summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();
}

/// <summary>Controls the appearance and behavior of the window title bar.</summary>
public enum TitleBarStyle
{
    /// <summary>Standard platform-native title bar.</summary>
    Native,

    /// <summary>Title bar is hidden but window chrome (traffic lights/buttons) remains.</summary>
    Hidden,

    /// <summary>Title bar content overlays the webview with transparent background.</summary>
    Overlay,

    /// <summary>No title bar or window chrome; the webview fills the entire window.</summary>
    Frameless,
}
