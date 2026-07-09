using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Notification;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddRynNotification(this IServiceCollection services)
    {
        services.AddSingleton(_ => new NotificationService());
        services.AddSingleton<NotificationPlugin>();
        services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<NotificationPlugin>());
        services.AddNotificationCommands(); // generated
        return services;
    }
}
