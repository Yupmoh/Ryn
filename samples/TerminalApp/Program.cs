using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Shell;


#pragma warning disable CA2007
await using var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Ryn Terminal";
        opts.Width = 1100;
        opts.Height = 720;
        opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        // ⚠️ SECURITY: This sample is a terminal emulator, so running an interactive shell IS its
        // entire purpose. Allowlisting an interpreter like this is the ONE case where it is intended —
        // and it means the frontend can run arbitrary commands. NEVER copy this pattern into a normal
        // app: allowlisting bash/sh/cmd/powershell disables the shell sandbox. For a normal app, use
        // ShellOptions.CommandScopes (per-argument argv templates) instead of AllowedCommands.
        services.AddRynShell(shell =>
        {
            var defaultShell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
            shell.AllowedCommands.Add(defaultShell);
        });
        services.AddRynTerminal();
    })
    .Build();

await app.RunAsync();
#pragma warning restore CA2007
