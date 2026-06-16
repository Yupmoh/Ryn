using Ryn.Core;
using Ryn.Ipc;
using RynApp1;

public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
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
