using Ryn.Core;
using Ryn.Ipc;

namespace ViteApp;

internal static class Program
{
    // Usage:
    //   Development: dotnet run -- --url http://localhost:5173
    //   Production:  dotnet run  (loads wwwroot/index.html)
    [System.STAThread]
    private static void Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Uri? devUrl = null;
        var useDevServer = args.Length >= 2
            && args[0] == "--url"
            && Uri.TryCreate(args[1], UriKind.Absolute, out devUrl);

        var builder = RynApplication.CreateBuilder();

        if (useDevServer)
        {
            builder.ConfigureOptions(opts =>
            {
                opts.Title = "ViteApp (dev)";
                opts.Url = devUrl;
                opts.Width = 900;
                opts.Height = 650;
                opts.DevTools = true;
            });
        }
        else
        {
            var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
            builder.ConfigureOptions(opts =>
            {
                opts.Title = "ViteApp";
                opts.Html = html;
                opts.Width = 900;
                opts.Height = 650;
                opts.DevTools = true;
            });
        }

        var app = builder
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddViteAppCommands();
            })
            .Build();

        app.Run();
    }
}
