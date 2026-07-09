using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.MenuBar;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynMenuBar(this IServiceCollection services, Action<MenuBarOptions>? configure = null)
    {
        var options = new MenuBarOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(sp => new MenuBarService(
            sp.GetRequiredService<MenuBarOptions>(),
            sp.GetRequiredService<IMainThreadDispatcher>(),
            sp));
        services.AddSingleton<MenuBarCommands>();
        services.AddSingleton<IRynPlugin, MenuBarPlugin>();
        services.AddMenuBarCommands();

        return services;
    }
}
