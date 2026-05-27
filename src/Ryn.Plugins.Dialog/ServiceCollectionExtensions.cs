using Microsoft.Extensions.DependencyInjection;

namespace Ryn.Plugins.Dialog;

public static class DialogServiceCollectionExtensions
{
    public static IServiceCollection AddRynDialog(this IServiceCollection services)
    {
        services.AddSingleton<DialogPlugin>();
        services.AddDialogCommands(); // generated
        services.AddPickerCommands(); // generated
        return services;
    }
}
