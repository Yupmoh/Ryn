using System.Runtime.CompilerServices;

namespace Ryn.Core;

/// <summary>Configuration options for a Ryn application window and webview.</summary>
public sealed class RynOptions
{
    // Tracks which scalar properties were explicitly assigned (whether to a non-default or even a
    // default value). The options-layering merge (RynApplicationBuilder.Build) uses this to copy ONLY
    // user-set properties over configuration-bound ones, so a programmatic RynOptions that never touched
    // e.g. Width no longer clobbers an appsettings.json Ryn:Width with the bare default. Ordinal compare:
    // keys are compile-time C# property names. Not part of the public surface; AOT-safe (no reflection).
    private readonly HashSet<string> _setProperties = new(StringComparer.Ordinal);

    // Backing fields. Defaults live here so an untouched property reads its default without being marked
    // "set". Each public setter routes through Set(...) to record the assignment via [CallerMemberName].
    private string _applicationId = "com.ryn.app";
    private string _title = "Ryn Application";
    private int _width = 800;
    private int _height = 600;
    private bool _resizable = true;
    private TitleBarStyle _titleBarStyle = TitleBarStyle.Native;
    private bool _transparent;
    private Uri? _url;
    private string? _html;
    private string? _contentDirectory;
    private bool _useLocalServer;
    private bool _useHttps;
    private int _localServerPort = 7421;
    private string? _iconPath;
    private bool _devTools;
    private bool _useEmbeddedContent;
    private bool _persistWindowState;
    private bool _captureUnhandledExceptions;
    private bool _disableDefaultLogging;

    /// <summary>Reverse-DNS application identifier (e.g. "com.example.myapp").</summary>
    public string ApplicationId { get => _applicationId; set => Set(ref _applicationId, value); }

    /// <summary>The application window title.</summary>
    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>Initial window width in pixels.</summary>
    public int Width { get => _width; set => Set(ref _width, value); }

    /// <summary>Initial window height in pixels.</summary>
    public int Height { get => _height; set => Set(ref _height, value); }

    /// <summary>Whether the window can be resized by the user.</summary>
    public bool Resizable { get => _resizable; set => Set(ref _resizable, value); }

    /// <summary>Controls the window title bar appearance.</summary>
    public TitleBarStyle TitleBarStyle { get => _titleBarStyle; set => Set(ref _titleBarStyle, value); }

    /// <summary>Whether the window background is transparent.</summary>
    public bool Transparent { get => _transparent; set => Set(ref _transparent, value); }

    /// <summary>URL to navigate to on startup. Mutually exclusive with <see cref="Html"/> and <see cref="ContentDirectory"/>.</summary>
    public Uri? Url { get => _url; set => Set(ref _url, value); }

    /// <summary>Raw HTML string to load on startup. Mutually exclusive with <see cref="Url"/> and <see cref="ContentDirectory"/>.</summary>
    public string? Html { get => _html; set => Set(ref _html, value); }

    /// <summary>Directory containing static web content (HTML/CSS/JS) to serve via the ryn:// scheme.</summary>
    public string? ContentDirectory { get => _contentDirectory; set => Set(ref _contentDirectory, value); }

    /// <summary>Whether to serve content through a local HTTP server instead of the custom scheme.</summary>
    public bool UseLocalServer { get => _useLocalServer; set => Set(ref _useLocalServer, value); }

    /// <summary>
    /// Deprecated: the local server is plain HTTP on loopback (which is already a browser "secure context",
    /// so crypto/clipboard APIs work). Kept for source compatibility; setting it has no effect.
    /// </summary>
    public bool UseHttps { get => _useHttps; set => Set(ref _useHttps, value); }

    /// <summary>
    /// Fixed loopback port for the local content/IPC server (<see cref="UseLocalServer"/>). A stable port
    /// means a stable <c>http://localhost:{port}</c> origin you can whitelist in your API's CORS config.
    /// Falls back to nearby ports, then an OS-assigned port, if taken. Default 7421.
    /// </summary>
    public int LocalServerPort { get => _localServerPort; set => Set(ref _localServerPort, value); }

    /// <summary>Path to the window icon file.</summary>
    public string? IconPath { get => _iconPath; set => Set(ref _iconPath, value); }

    /// <summary>Whether browser developer tools are enabled.</summary>
    public bool DevTools { get => _devTools; set => Set(ref _devTools, value); }

    /// <summary>
    /// Whether to extract and serve embedded content resources. When left unset, the bundler-stamped
    /// assembly metadata (<c>Ryn.UseEmbeddedContent</c>) decides; setting it explicitly always wins.
    /// </summary>
    public bool UseEmbeddedContent { get => _useEmbeddedContent; set => Set(ref _useEmbeddedContent, value); }

    /// <summary>Whether to automatically save and restore window position and size.</summary>
    public bool PersistWindowState { get => _persistWindowState; set => Set(ref _persistWindowState, value); }

    /// <summary>
    /// When true, install process-global handlers for AppDomain-unhandled and unobserved-task exceptions
    /// (in addition to the event-loop catch), surfaced via <see cref="RynApplication.UnhandledException"/>.
    /// Lets an app install a crash logger. Default false.
    /// </summary>
    public bool CaptureUnhandledExceptions { get => _captureUnhandledExceptions; set => Set(ref _captureUnhandledExceptions, value); }

    /// <summary>
    /// Opt out of the default console/debug logging providers that <see cref="RynApplication"/> registers.
    /// When true, no provider is added automatically and logging stays silent unless the app registers its
    /// own via <see cref="RynApplicationBuilder.ConfigureServices"/>. Default false (framework Critical/Error
    /// messages — e.g. a plugin that failed to initialize — are visible out of the box).
    /// </summary>
    public bool DisableDefaultLogging { get => _disableDefaultLogging; set => Set(ref _disableDefaultLogging, value); }

    /// <summary>Custom URL schemes to register for deep linking (e.g., "myapp").</summary>
    public IList<string> DeepLinkSchemes { get; } = new List<string>();

    /// <summary>Additional origins allowed to access IPC commands.</summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>
    /// Custom webview URL schemes to register with the engine before the webview is created, so an app
    /// can serve its own scheme via <see cref="RynWebView.RegisterCustomScheme"/>. The reserved <c>ryn</c>
    /// scheme (built-in IPC/content transport) is rejected, and registering a handler for a scheme not
    /// declared here throws — schemes must be declared up front because they can only be registered with
    /// the engine before the webview exists.
    /// </summary>
    public IList<string> CustomSchemes { get; } = new List<string>();

    /// <summary>True when <paramref name="propertyName"/> was explicitly assigned on this instance.</summary>
    internal bool IsSet(string propertyName) => _setProperties.Contains(propertyName);

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        field = value;
        _setProperties.Add(propertyName);
    }
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
