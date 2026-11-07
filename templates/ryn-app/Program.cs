#if !ANDROID && !IOS
using Ryn.Core;
using Ryn.Ipc;
using RynApp1;

namespace RynApp1;

public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.Title = "RynApp1";
                opts.Width = 900;
                opts.Height = 650;
                opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();
                services.AddAppCommands();
            })
            .Build();

        app.Run();
    }
}
#endif
