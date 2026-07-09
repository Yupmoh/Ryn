using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Badge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynBadge(this IServiceCollection services)
    {
        services.AddSingleton(sp => new BadgeService(
            sp.GetRequiredService<IMainThreadDispatcher>(),
            sp));
        services.AddSingleton<BadgeCommands>();
        services.AddSingleton<IRynPlugin, BadgePlugin>();
        services.AddBadgeCommands();

        return services;
    }
}
