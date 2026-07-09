using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.WebViewPane;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynWebViewPane(this IServiceCollection services)
    {
        services.AddSingleton(sp => new WebViewPaneService(
            sp.GetRequiredService<IMainThreadDispatcher>(),
            sp));
        services.AddSingleton<WebViewPaneCommands>();
        services.AddSingleton<IRynPlugin, WebViewPanePlugin>();
        services.AddWebViewPaneCommands();

        return services;
    }
}
