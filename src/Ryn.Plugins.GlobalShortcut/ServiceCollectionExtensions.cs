using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.GlobalShortcut;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynGlobalShortcut(this IServiceCollection services)
    {
        services.AddSingleton(sp => new GlobalShortcutService(
            sp.GetRequiredService<IMainThreadDispatcher>()));
        services.AddSingleton<GlobalShortcutCommands>();
        services.AddSingleton<IRynPlugin, GlobalShortcutPlugin>();
        services.AddGlobalShortcutCommands();

        return services;
    }
}
