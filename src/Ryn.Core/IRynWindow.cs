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
    /// <summary>Initiates a window drag operation (for frameless windows).</summary>
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
