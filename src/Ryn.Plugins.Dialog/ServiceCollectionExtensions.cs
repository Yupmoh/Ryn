using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Dialog;

public static class DialogServiceCollectionExtensions
{
    public static IServiceCollection AddRynDialog(this IServiceCollection services)
    {
        services.AddSingleton<DialogPlugin>();
        services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<DialogPlugin>());
        services.AddDialogCommands(); // generated
        services.AddPickerCommands(); // generated
        return services;
    }
}
