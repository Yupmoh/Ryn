using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Dialog;
using Ryn.Plugins.FileSystem;

namespace MarkdownEditor;

internal static class Program
{
    [System.STAThread]
    private static void Main()
    {
        // The open/save dialogs return absolute paths under the user's home folder, and the
        // FileSystem plugin only honours reads/writes that fall inside its AllowedPaths. Scope
        // the plugin to the home directory so the files the dialog hands back are in range —
        // without this the default scope is the app's bin directory and every open/save fails
        // with UnauthorizedAccessException.
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));

        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.Title = "Ryn Markdown Editor";
                opts.Width = 1100;
                opts.Height = 750;
                opts.Html = html;
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddRynFileSystem(fs => fs.AllowedPaths.Add(homeDir));
                services.AddRynDialog();
            })
            .Build();

        app.Run();
    }
}
