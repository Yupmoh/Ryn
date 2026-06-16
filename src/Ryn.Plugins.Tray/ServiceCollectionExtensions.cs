using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Tray;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynTray(this IServiceCollection services, Action<TrayOptions>? configure = null)
    {
        var options = new TrayOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(sp => new TrayService(
            sp.GetRequiredService<TrayOptions>(),
            sp.GetRequiredService<IMainThreadDispatcher>()));
        services.AddSingleton<TrayCommands>();
        services.AddSingleton<IRynPlugin, TrayPlugin>();
        services.AddTrayCommands();

        return services;
    }
}
