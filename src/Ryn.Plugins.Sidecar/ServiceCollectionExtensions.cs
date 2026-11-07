using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Sidecar;

public static class SidecarServiceCollectionExtensions
{
    public static IServiceCollection AddRynSidecars(
        this IServiceCollection services,
        Action<SidecarOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SidecarOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton<SidecarManager>();
        services.AddSingleton<ISidecarManager>(provider => provider.GetRequiredService<SidecarManager>());
        services.AddSingleton<SidecarPlugin>();
        services.AddSingleton<IRynPlugin>(provider => provider.GetRequiredService<SidecarPlugin>());
        return services;
    }
}

#pragma warning disable CA1812 // Instantiated by DI
internal sealed class SidecarPlugin : IRynPlugin, IAsyncDisposable
#pragma warning restore CA1812
{
    private readonly SidecarManager _manager;

    public SidecarPlugin(SidecarManager manager) => _manager = manager;

    public string Name => "Sidecar";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Resolve and validate host definitions before the native event loop starts. Sidecars are started
        // explicitly by the host so readiness waits never block application initialization.
        _ = _manager;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _manager.DisposeAsync();
}
