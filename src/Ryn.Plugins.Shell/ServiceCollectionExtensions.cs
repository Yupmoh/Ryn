using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddRynShell(this IServiceCollection services, Action<ShellOptions>? configure = null)
    {
        var options = new ShellOptions();
        configure?.Invoke(options);
        services.AddSingleton(sp =>
        {
            var caps = sp.GetService<RynCapabilities>();
            if (caps is not null)
                CapabilityScopeMerger.MergeShellScope(caps, options);
            return options;
        });
        services.AddSingleton<ShellPlugin>();
        services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<ShellPlugin>());
        services.AddShellCommands(); // generated
        services.AddSpawnCommands(); // generated
        return services;
    }
}
