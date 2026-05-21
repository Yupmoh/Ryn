using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Ryn.Core;

public sealed class RynApplicationBuilder
{
    private readonly RynOptions _options;
    private readonly ServiceCollection _services = new();
    private readonly List<Action<IServiceCollection>> _configureActions = [];
    private readonly List<Func<RynApplication, IRynPlugin>> _pluginFactories = [];

    internal RynApplicationBuilder(RynOptions options)
    {
        _options = options;
        _services.AddSingleton(options);
    }

    public RynOptions Options => _options;

    public RynApplicationBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configureActions.Add(configure);
        return this;
    }

    public RynApplicationBuilder AddPlugin<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>()
        where TPlugin : class, IRynPlugin
    {
        _services.AddSingleton<TPlugin>();
        _pluginFactories.Add(app => app.Services.GetRequiredService<TPlugin>());
        return this;
    }

    public RynApplicationBuilder AddPlugin(Func<IServiceProvider, IRynPlugin> factory)
    {
        _pluginFactories.Add(app => factory(app.Services));
        return this;
    }

    public RynApplication Build()
    {
        foreach (var configure in _configureActions)
        {
            configure(_services);
        }

        var provider = _services.BuildServiceProvider();
        var app = new RynApplication(provider);

        foreach (var factory in _pluginFactories)
        {
            app.AddPlugin(factory(app));
        }

        return app;
    }
}
