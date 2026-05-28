using Ryn.Core;

namespace Ryn.Ipc;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class WindowCommands
#pragma warning restore CA1812
{
    private readonly IRynWindow _window;

    public WindowCommands(IRynWindow window) => _window = window;

    [RynCommand("window.close")]
    public void Close() => _window.Close();

    [RynCommand("window.minimize")]
    public void Minimize() => _window.Minimize();

    [RynCommand("window.toggleMaximize")]
    public void ToggleMaximize() => _window.ToggleMaximize();

    [RynCommand("window.startDrag")]
    public void StartDrag() => _window.StartDrag();

    [RynCommand("window.startResize")]
    public void StartResize(int edge) => _window.StartResize((WindowEdge)edge);

    [RynCommand("window.setTitle")]
    public void SetTitle(string title) => _window.Title = title;

    [RynCommand("window.setSize")]
    public void SetSize(int width, int height)
    {
        _window.Width = width;
        _window.Height = height;
    }
}
