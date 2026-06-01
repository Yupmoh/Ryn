namespace Ryn.Core.Internal;

/// <summary>
/// An <see cref="IRynWindow"/> that can be injected into any service at any time — including services
/// constructed before the native window exists. Members forward to the real window once it is available;
/// event subscriptions made early are attached when the window becomes ready. Using a member before the
/// window exists throws a clear error (you cannot, e.g., show a window that has not been created yet).
/// </summary>
internal sealed class DeferredRynWindow(RynWindowAccessor accessor) : IRynWindow
{
    private RynWindow Live => accessor.Window
        ?? throw new InvalidOperationException(
            "The window is not available yet. IRynWindow can be injected anywhere, but its members are only usable after RunAsync has started.");

    public string Title { get => Live.Title; set => Live.Title = value; }
    public int Width { get => Live.Width; set => Live.Width = value; }
    public int Height { get => Live.Height; set => Live.Height = value; }
    public bool Resizable { get => Live.Resizable; set => Live.Resizable = value; }
    public AppTheme Theme => Live.Theme;

    public ValueTask ShowAsync(CancellationToken cancellationToken = default) => Live.ShowAsync(cancellationToken);
    public ValueTask HideAsync(CancellationToken cancellationToken = default) => Live.HideAsync(cancellationToken);
    public ValueTask CloseAsync(CancellationToken cancellationToken = default) => Live.CloseAsync(cancellationToken);
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) => Live.WaitForCloseAsync(cancellationToken);
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => Live.NavigateAsync(url, cancellationToken);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) => Live.EvaluateJavaScriptAsync(script, cancellationToken);
    public void Close() => Live.Close();
    public void Minimize() => Live.Minimize();
    public void ToggleMaximize() => Live.ToggleMaximize();
    public void StartDrag() => Live.StartDrag();
    public void StartResize(WindowEdge edge) => Live.StartResize(edge);

    // Events: defer the subscription until the window exists; forward removals to the live window.
    public event EventHandler<WindowClosingEventArgs>? Closing
    {
        add { var h = value; accessor.OnReady(w => w.Closing += h); }
        remove { if (accessor.Window is { } w) w.Closing -= value; }
    }

    public event EventHandler? Closed
    {
        add { var h = value; accessor.OnReady(w => w.Closed += h); }
        remove { if (accessor.Window is { } w) w.Closed -= value; }
    }

    public event EventHandler<WindowResizedEventArgs>? Resized
    {
        add { var h = value; accessor.OnReady(w => w.Resized += h); }
        remove { if (accessor.Window is { } w) w.Resized -= value; }
    }

    public event EventHandler? Focused
    {
        add { var h = value; accessor.OnReady(w => w.Focused += h); }
        remove { if (accessor.Window is { } w) w.Focused -= value; }
    }

    public event EventHandler? Blurred
    {
        add { var h = value; accessor.OnReady(w => w.Blurred += h); }
        remove { if (accessor.Window is { } w) w.Blurred -= value; }
    }

    public event EventHandler<WindowMovedEventArgs>? Moved
    {
        add { var h = value; accessor.OnReady(w => w.Moved += h); }
        remove { if (accessor.Window is { } w) w.Moved -= value; }
    }

    public event EventHandler<WindowStateChangedEventArgs>? StateChanged
    {
        add { var h = value; accessor.OnReady(w => w.StateChanged += h); }
        remove { if (accessor.Window is { } w) w.StateChanged -= value; }
    }

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged
    {
        add { var h = value; accessor.OnReady(w => w.ThemeChanged += h); }
        remove { if (accessor.Window is { } w) w.ThemeChanged -= value; }
    }
}
