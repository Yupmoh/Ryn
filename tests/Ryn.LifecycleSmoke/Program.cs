using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(options =>
            {
                options.ApplicationId = "com.yupmoh.ryn.lifecycle-smoke";
                options.Title = "Ryn Lifecycle Smoke";
                options.Html = "<html><body>ready</body></html>";
                options.PersistWindowState = false;
            })
            .ConfigureServices(services => services.AddSingleton<IRynPlugin, ReadyShutdownPlugin>())
            .Build();

        try
        {
            app.Run();
            Console.WriteLine("RYN_LIFECYCLE_STOPPED");
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

internal sealed class ReadyShutdownPlugin(
    IMainThreadDispatcher dispatcher,
    IRynApplicationLifetime lifetime) : IRynPlugin
{
    public string Name => "lifecycle-smoke";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        dispatcher.Post(() =>
        {
            Console.WriteLine("RYN_LIFECYCLE_READY");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                lifetime.RequestShutdown();
            });
        });
        return ValueTask.CompletedTask;
    }
}
