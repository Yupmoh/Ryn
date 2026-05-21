namespace Ryn.Core;

public sealed class RynOptions
{
    public string ApplicationId { get; set; } = "com.ryn.app";
    public string Title { get; set; } = "Ryn Application";
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public bool Resizable { get; set; } = true;
    public bool Frameless { get; set; }
    public bool Transparent { get; set; }
    public Uri? Url { get; set; }
    public bool DevTools { get; set; }
}
