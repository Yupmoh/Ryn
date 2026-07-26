using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Plugins.Notification;

if (!OperatingSystem.IsLinux())
    return;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(options =>
    {
        options.Title = "Ryn Notification Linux Smoke";
        options.Html = "<html><body>ready</body></html>";
    })
    .ConfigureServices(services =>
    {
        services.AddRynNotification();
        services.AddSingleton<IRynPlugin, ReadyShutdownPlugin>();
    })
    .Build();

try
{
    app.Run();
}
finally
{
    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed class ReadyShutdownPlugin(
    IMainThreadDispatcher dispatcher,
    IRynApplicationLifetime lifetime) : IRynPlugin
{
    public string Name => "notification-linux-smoke";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        dispatcher.Post(() =>
        {
            Console.WriteLine("RYN_NOTIFICATION_LINUX_READY");
            lifetime.RequestShutdown();
        });
        return ValueTask.CompletedTask;
    }
}
