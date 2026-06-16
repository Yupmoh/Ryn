using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Updater;

public static class UpdaterServiceCollectionExtensions
{
    public static IServiceCollection AddRynUpdater(this IServiceCollection services, Action<UpdaterOptions>? configure = null)
    {
        var options = new UpdaterOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        // Resolve the lifetime explicitly so Apply requests an orderly shutdown (PAP-06) rather than
        // hard-exiting. GetService (not GetRequiredService) keeps the service usable if a host ever omits the
        // lifetime registration — it then falls back to Environment.Exit, the prior behaviour.
        services.AddSingleton(sp => new UpdaterService(
            sp.GetRequiredService<UpdaterOptions>(),
            sp.GetService<IRynApplicationLifetime>()));
        services.AddSingleton<UpdaterCommands>();
        services.AddSingleton<IRynPlugin, UpdaterPlugin>();
        services.AddUpdaterCommands(); // generated

        return services;
    }
}
