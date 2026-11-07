using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.FileSystem;

namespace FileManager;

internal static class Program
{
    [System.STAThread]
    private static void Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The FileSystem plugin doesn't expand '~', so hand the frontend the real home path.
        // Forward slashes keep the JS path logic happy and still resolve on Windows.
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"))
            .Replace("__RYN_HOME__", homeDir.Replace('\\', '/'), StringComparison.Ordinal);

        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.Title = "Ryn File Manager";
                opts.Width = 1000;
                opts.Height = 700;
                opts.Html = html;
                opts.TitleBarStyle = TitleBarStyle.Overlay; // content goes edge-to-edge; traffic lights float over the toolbar
                opts.DevTools = true;
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddRynFileSystem(fs => fs.AllowedPaths.Add(homeDir));
            })
            .Build();

        app.Run();
    }
}
