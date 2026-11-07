using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Clipboard;
using Ryn.Plugins.Dialog;
using Ryn.Plugins.FileSystem;
using Ryn.Plugins.Notification;
using Ryn.Plugins.Shell;

namespace DevKit;

internal static class Program
{
    [System.STAThread]
    private static void Main()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));

        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.Title = "Ryn DevKit";
                opts.Width = 1280;
                opts.Height = 860;
                opts.Html = html;
                opts.DevTools = true;
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddDevKitCommands();
                services.AddRynFileSystem(fs =>
                {
                    fs.AllowedPaths.Add(AppContext.BaseDirectory);
                    fs.AllowedPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                });
                services.AddRynDialog();
                services.AddRynClipboard();
                services.AddRynShell(shell =>
                {
                    // Only fixed-output, side-effect-free utilities are allowlisted. We deliberately do NOT
                    // allowlist interpreters (bash/sh/cmd/powershell), `env` (leaks host secrets), or `cat`
                    // (arbitrary file read/exfil): allowlisting any of those turns shell.execute into an
                    // arbitrary-code / arbitrary-read primitive and defeats the sandbox.
                    shell.AllowedCommands.AddRange(["echo", "date", "whoami", "uname", "ls", "pwd"]);
                });
                services.AddRynNotification();
            })
            .Build();

        app.Run();
    }
}
