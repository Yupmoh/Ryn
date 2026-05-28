namespace Ryn.Core;

public interface IRynWindow
{
    public string Title { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Resizable { get; set; }

    public ValueTask ShowAsync(CancellationToken cancellationToken = default);
    public ValueTask HideAsync(CancellationToken cancellationToken = default);
    public ValueTask CloseAsync(CancellationToken cancellationToken = default);
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default);
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default);

    public void Minimize();
    public void ToggleMaximize();
    public void StartDrag();
    public void StartResize(WindowEdge edge);
}

[Flags]
public enum WindowEdge
{
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
}
