namespace Ryn.Core;

/// <summary>Represents the native application window.</summary>
public interface IRynWindow
{
    /// <summary>A stable identifier for this window, unique within the application. The main window is 1.</summary>
    public int Id { get; }
    /// <summary>The window title text.</summary>
    public string Title { get; set; }
    /// <summary>The window width in pixels.</summary>
    public int Width { get; set; }
    /// <summary>The window height in pixels.</summary>
    public int Height { get; set; }
    /// <summary>Whether the window can be resized by the user.</summary>
    public bool Resizable { get; set; }
    /// <summary>Occurs before the window closes.</summary>
    public event EventHandler<WindowClosingEventArgs>? Closing;
    /// <summary>Occurs after the window has been confirmed closed.</summary>
    public event EventHandler? Closed;
    /// <summary>Occurs after the window has been resized.</summary>
    public event EventHandler<WindowResizedEventArgs>? Resized;
    /// <summary>Occurs when the window gains focus.</summary>
    public event EventHandler? Focused;
    /// <summary>Occurs when the window loses focus.</summary>
    public event EventHandler? Blurred;
    /// <summary>Occurs after the window has been moved.</summary>
    public event EventHandler<WindowMovedEventArgs>? Moved;
    /// <summary>Occurs when the window state changes.</summary>
    public event EventHandler<WindowStateChangedEventArgs>? StateChanged;
    /// <summary>Current system color scheme.</summary>
    public AppTheme Theme { get; }
    /// <summary>Occurs when the system color scheme changes.</summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    /// <summary>Shows the window if it is hidden.</summary>
    public ValueTask ShowAsync(CancellationToken cancellationToken = default);
    /// <summary>Hides the window without closing it.</summary>
    public ValueTask HideAsync(CancellationToken cancellationToken = default);
    /// <summary>Closes the window and exits the event loop.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default);
    /// <summary>Waits until the window is closed by the user or programmatically.</summary>
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default);
    /// <summary>Navigates the webview to the specified URL.</summary>
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);
    /// <summary>Evaluates a JavaScript expression in the webview and returns the result.</summary>
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default);
    /// <summary>Closes the window synchronously.</summary>
    public void Close();
    /// <summary>Minimizes the window.</summary>
    public void Minimize();
    /// <summary>Toggles between maximized and restored window states.</summary>
    public void ToggleMaximize();
    /// <summary>Moves the window's top-left corner to the given screen coordinates (in points).</summary>
    public void Move(int x, int y);
    /// <summary>Enters or leaves fullscreen mode.</summary>
    public void SetFullscreen(bool fullscreen);
    /// <summary>Sets whether the window stays above other windows.</summary>
    public void SetAlwaysOnTop(bool alwaysOnTop);
    /// <summary>
    /// Sets the main page zoom, clamped to 0.25–5.0. This is engine page zoom rather than legacy CSS zoom,
    /// so DOM geometry, hit testing, title-bar regions, and webview-pane bounds remain in sync.
    /// </summary>
    public void SetPageZoom(double factor);
    /// <summary>Returns the main page zoom factor currently tracked by the window.</summary>
    public double GetPageZoom();
    /// <summary>Sets whether the window ignores mouse input, letting clicks fall through to whatever is
    /// beneath it (for overlay/HUD-style windows). The page keeps running — timers, IPC and script still
    /// work — it just receives no mouse events, so re-enabling input needs a non-mouse trigger.</summary>
    public void SetClickThrough(bool clickThrough);
    /// <summary>
    /// Sets the window's translucent backdrop material. The material renders behind a transparent webview
    /// background, so give the page a (semi-)transparent background to see it. Degrades to
    /// <see cref="BackdropMaterial.None"/> where the OS can't render it — query <see cref="GetBackdrop"/> to check.
    /// </summary>
    public void SetBackdrop(BackdropMaterial material);
    /// <summary>
    /// Starts a native macOS window drag anchored to the retained left-mousedown event, verified against the
    /// verdict point (<paramref name="x"/>, <paramref name="y"/> in viewport-top-left CSS pixels). Driven by
    /// the injected title-bar script when the live DOM rules a mousedown point draggable — because the drag
    /// starts from the ORIGINAL event, the IPC delay costs nothing and the window never desyncs from the
    /// cursor. No effect off macOS (use <see cref="StartDrag"/> there); see docs/custom-title-bars.md.
    /// </summary>
    public void BeginNativeDrag(double x, double y);
    /// <summary>
    /// Positions the macOS traffic-light buttons — the close button's top-left, in points from the window's
    /// top-left — to vertically center them in a taller custom title bar. Re-applied on resize. No effect
    /// off macOS.
    /// </summary>
    public void SetTrafficLightPosition(TrafficLightPosition position);
    /// <summary>The backdrop material currently applied — may be <see cref="BackdropMaterial.None"/> if the
    /// requested material degraded on this OS.</summary>
    public BackdropMaterial GetBackdrop();
    /// <summary>Centers the window on its current screen.</summary>
    public void Center();
    /// <summary>
    /// Initiates a window drag operation (for frameless windows on Windows/Linux). For dragging a title bar
    /// from HTML, prefer the <c>data-webview-drag</c> attribute; on macOS the injected script routes through
    /// <see cref="BeginNativeDrag"/>, which anchors to the real mousedown event and cannot lag the cursor.
    /// See docs/custom-title-bars.md.
    /// </summary>
    public void StartDrag();
    /// <summary>Initiates a window resize operation from the specified edge.</summary>
    public void StartResize(WindowEdge edge);
}
/// <summary>Specifies which edge or corner of the window to resize from.</summary>
[Flags]
public enum WindowEdge
{
    Top = 1, Bottom = 2, Left = 4, Right = 8,
    TopLeft = Top | Left, TopRight = Top | Right,
    BottomLeft = Bottom | Left, BottomRight = Bottom | Right,
}
