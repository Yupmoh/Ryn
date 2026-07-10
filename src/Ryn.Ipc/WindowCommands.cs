using System.Globalization;
using Ryn.Core;

namespace Ryn.Ipc;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class WindowCommands
#pragma warning restore CA1812
{
    private readonly CurrentWindowAccessor _windows;
    private readonly IRynWindowManager _manager;

    public WindowCommands(CurrentWindowAccessor windows, IRynWindowManager manager)
    {
        _windows = windows;
        _manager = manager;
    }

    [RynCommand("window.close")]
    public void Close() => _windows.Current.Close();

    [RynCommand("window.minimize")]
    public void Minimize() => _windows.Current.Minimize();

    [RynCommand("window.toggleMaximize")]
    public void ToggleMaximize() => _windows.Current.ToggleMaximize();

    /// <summary>Programmatic window drag. For title bars, prefer the <c>data-webview-drag</c> attribute, which
    /// drags natively inside the mousedown with no IPC lag (see docs/custom-title-bars.md).</summary>
    [RynCommand("window.startDrag")]
    public void StartDrag() => _windows.Current.StartDrag();

    [RynCommand("window.startResize")]
    public void StartResize(int edge) => _windows.Current.StartResize((WindowEdge)edge);

    [RynCommand("window.setTitle")]
    public void SetTitle(string title) => _windows.Current.Title = title;

    [RynCommand("window.setSize")]
    public void SetSize(int width, int height)
    {
        _windows.Current.Width = width;
        _windows.Current.Height = height;
    }

    [RynCommand("window.setPosition")]
    public void SetPosition(int x, int y) => _windows.Current.Move(x, y);

    [RynCommand("window.setFullscreen")]
    public void SetFullscreen(bool fullscreen) => _windows.Current.SetFullscreen(fullscreen);

    [RynCommand("window.setAlwaysOnTop")]
    public void SetAlwaysOnTop(bool alwaysOnTop) => _windows.Current.SetAlwaysOnTop(alwaysOnTop);

    /// <summary>Makes the calling window ignore mouse input (clicks fall through to what's beneath). The page
    /// keeps running and can still invoke commands — it just gets no mouse events — so re-enabling input needs
    /// a non-mouse trigger (a timer or event in this page, C#, or another window).</summary>
    [RynCommand("window.setClickThrough")]
    public void SetClickThrough(bool clickThrough) => _windows.Current.SetClickThrough(clickThrough);

    /// <summary>
    /// Publishes the title bar's draggable and interactive rectangles (viewport-top-left CSS pixels, each a
    /// flat [x,y,w,h] run). Normally driven by the injected <c>data-webview-drag</c> script; exposed so an app
    /// can manage drag regions itself. See <c>IRynWindow.SetTitleBarDragRegions</c>.
    /// </summary>
    [RynCommand("window.setTitleBarDragRegions")]
    public void SetTitleBarDragRegions(double[]? drag, double[]? ignore) =>
        _windows.Current.SetTitleBarDragRegions(drag ?? [], ignore ?? []);

    /// <summary>Sets the window backdrop: "none", "blur", "acrylic", or "mica" (unknown values are ignored).</summary>
    [RynCommand("window.setBackdrop")]
    public void SetBackdrop(string material)
    {
        if (TryParseBackdrop(material, out var value))
            _windows.Current.SetBackdrop(value);
    }

    /// <summary>Returns the effective backdrop material as a lowercase string (may be "none" if it degraded).</summary>
    [RynCommand("window.getBackdrop")]
    public string GetBackdrop() => _windows.Current.GetBackdrop() switch
    {
        BackdropMaterial.Blur => "blur",
        BackdropMaterial.Acrylic => "acrylic",
        BackdropMaterial.Mica => "mica",
        _ => "none",
    };

    private static bool TryParseBackdrop(string material, out BackdropMaterial value)
    {
        value = BackdropMaterial.None;
        if (string.Equals(material, "none", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(material, "blur", StringComparison.OrdinalIgnoreCase)) { value = BackdropMaterial.Blur; return true; }
        if (string.Equals(material, "acrylic", StringComparison.OrdinalIgnoreCase)) { value = BackdropMaterial.Acrylic; return true; }
        if (string.Equals(material, "mica", StringComparison.OrdinalIgnoreCase)) { value = BackdropMaterial.Mica; return true; }
        return false;
    }

    [RynCommand("window.center")]
    public void Center() => _windows.Current.Center();

    /// <summary>Returns the id of the window whose page invoked this command.</summary>
    [RynCommand("window.current")]
    public int Current() => _windows.Current.Id;

    /// <summary>Returns the ids of all currently-open windows.</summary>
    [RynCommand("window.list")]
    public int[] List() => _manager.Windows.Select(w => w.Id).ToArray();

    /// <summary>
    /// Opens a new window and returns its id. Each field is an optional named argument so JS calls it naturally
    /// as <c>window.__ryn.invoke('window.open', { title, width, height, html })</c>; omitted fields fall back to
    /// the window defaults. Provide one of <paramref name="url"/>/<paramref name="html"/>/
    /// <paramref name="contentDirectory"/> for the window's content.
    /// </summary>
    [RynCommand("window.open")]
    public string Open(
        string? title = null,
        int? width = null,
        int? height = null,
        bool? resizable = null,
        bool? devTools = null,
        string? url = null,
        string? html = null,
        string? contentDirectory = null)
    {
        var options = new RynWindowOptions();
        if (title is not null) options.Title = title;
        if (width is { } w) options.Width = w;
        if (height is { } h) options.Height = h;
        if (resizable is { } r) options.Resizable = r;
        if (devTools is { } d) options.DevTools = d;
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed)) options.Url = parsed;
        if (html is not null) options.Html = html;
        if (contentDirectory is not null) options.ContentDirectory = contentDirectory;

        return _manager.OpenWindow(options).Id.ToString(CultureInfo.InvariantCulture);
    }
}
