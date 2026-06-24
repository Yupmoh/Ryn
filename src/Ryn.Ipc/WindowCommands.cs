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
