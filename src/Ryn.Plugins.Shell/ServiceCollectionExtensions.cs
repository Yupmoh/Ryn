using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddRynShell(this IServiceCollection services, Action<ShellOptions>? configure = null)
    {
        var options = new ShellOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(sp =>
        {
            var caps = sp.GetService<RynCapabilities>();
            if (caps is not null)
                CapabilityScopeMerger.MergeShellScope(caps, options);
            return options;
        });
        services.TryAddSingleton<ShellExecutionPolicy>();
        services.AddSingleton<ShellPlugin>();
        services.AddSingleton<IRynPlugin>(provider => provider.GetRequiredService<ShellPlugin>());
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ShellCommands)))
            services.AddShellCommands();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(SpawnCommands)))
            services.AddSpawnCommands();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(PtyCommands)))
            services.AddPtyCommands();
        return services;
    }

    /// <summary>
    /// Adds the acknowledged <c>terminal.*</c> transport. Call <see cref="AddRynShell"/> first to configure
    /// the executable allowlist, and explicitly grant the <c>terminal</c> capability in <c>ryn.json</c>.
    /// </summary>
    public static IServiceCollection AddRynTerminal(
        this IServiceCollection services,
        Action<TerminalOptions>? configure = null)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ShellExecutionPolicy)))
            throw new InvalidOperationException("Call AddRynShell before AddRynTerminal so terminal sessions use the configured shell policy.");
        var options = new TerminalOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<TerminalSessionService>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(TerminalCommands)))
            services.AddTerminalCommands();
        return services;
    }
}
