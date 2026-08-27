using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Callbacks;

public static class RynCallbackServiceCollectionExtensions
{
    /// <summary>Adds source-generated callback dispatch to the application.</summary>
    public static IServiceCollection AddRynCallbacks(this IServiceCollection services)
    {
        services.AddSingleton<RynCallbackDispatcher>();
        services.AddSingleton<WebViewNavigatingHandler>(sp =>
            sp.GetRequiredService<RynCallbackDispatcher>().DispatchWebViewNavigating);
        services.AddSingleton<WebViewNavigatedHandler>(sp =>
            sp.GetRequiredService<RynCallbackDispatcher>().DispatchWebViewNavigated);
        return services;
    }
}
