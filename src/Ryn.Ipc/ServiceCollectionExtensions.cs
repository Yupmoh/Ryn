using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core;

namespace Ryn.Ipc;

public static class RynIpcServiceCollectionExtensions
{
    public static IServiceCollection AddRynCommands(this IServiceCollection services)
    {
        // Resolve a logger so RynCapabilitiesLoader can emit its one-time "release build is failing
        // closed because ryn.json is absent" warning. Without a logger that warning never fires.
        services.AddSingleton(sp => RynCapabilitiesLoader.Load(
            sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(RynCapabilitiesLoader).FullName!)));
        services.AddSingleton<IIpcObserver, LoggingIpcObserver>();
        services.AddSingleton<RynCommandDispatcher>();
        services.AddSingleton<CommandDispatchHandler>(sp =>
            sp.GetRequiredService<RynCommandDispatcher>().DispatchAsync);
        services.AddSingleton<WindowCommands>();
        services.AddWindowCommands();
        services.AddSingleton<ConsoleForwardCommands>();
        services.AddConsoleForwardCommands();
        return services;
    }
}
