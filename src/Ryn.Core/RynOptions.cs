namespace Ryn.Core;

public sealed class RynOptions
{
    public string ApplicationId { get; set; } = "com.ryn.app";
    public string Title { get; set; } = "Ryn Application";
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public bool Resizable { get; set; } = true;
    public TitleBarStyle TitleBarStyle { get; set; } = TitleBarStyle.Native;
    public bool Transparent { get; set; }
    public Uri? Url { get; set; }
    public string? Html { get; set; }
    public string? ContentDirectory { get; set; }
    public bool UseLocalServer { get; set; }
    public bool UseHttps { get; set; }
    public string? IconPath { get; set; }
    public bool DevTools { get; set; }
    public IList<string> AllowedOrigins { get; } = new List<string>();
}

public enum TitleBarStyle
{
    Native,
    Hidden,
    Overlay,
    Frameless,
}
