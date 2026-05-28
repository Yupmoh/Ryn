using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core.Internal;

namespace Ryn.Core;

public sealed class RynApplicationBuilder
{
    private readonly RynOptions? _programmaticOptions;
    private readonly ServiceCollection _services = new();
    private readonly ConfigurationBuilder _configurationBuilder = new();
    private readonly List<Action<IServiceCollection>> _configureActions = [];
    private readonly List<Action<RynOptions>> _configureOptionsActions = [];
    internal RynApplicationBuilder(RynOptions? programmaticOptions)
    {
        _programmaticOptions = programmaticOptions;
        _configurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
    }

    public RynOptions Options => _programmaticOptions ?? new RynOptions();

    public IConfigurationBuilder Configuration => _configurationBuilder;

    public RynApplicationBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configureActions.Add(configure);
        return this;
    }

    public RynApplicationBuilder ConfigureOptions(Action<RynOptions> configure)
    {
        _configureOptionsActions.Add(configure);
        return this;
    }

    public RynApplicationBuilder AddPlugin<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>()
        where TPlugin : class, IRynPlugin
    {
        _services.AddSingleton<TPlugin>();
        _services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<TPlugin>());
        return this;
    }

    public RynApplicationBuilder AddPlugin(Func<IServiceProvider, IRynPlugin> factory)
    {
        _services.AddSingleton<IRynPlugin>(factory);
        return this;
    }

    public RynApplication Build()
    {
        // 1. Build configuration
        var configuration = _configurationBuilder.Build();
        _services.AddSingleton<IConfiguration>(configuration);

        // 2. Logging
        _services.AddLogging();

        // 3. RynOptions: defaults → config file → programmatic → callbacks
        var options = new RynOptions();
        BindOptionsFromConfiguration(options, configuration.GetSection("Ryn"));

        if (_programmaticOptions is not null)
        {
            ApplyProgrammaticOverrides(options, _programmaticOptions);
        }

        foreach (var action in _configureOptionsActions)
        {
            action(options);
        }

        _services.AddSingleton(options);

        // 4. Window accessor + interface factories
        _services.AddSingleton<RynWindowAccessor>();
        _services.AddSingleton<IRynWindow>(sp =>
            sp.GetRequiredService<RynWindowAccessor>().Window
            ?? throw new InvalidOperationException("Window is not available. It is only accessible after RunAsync begins."));
        _services.AddSingleton<IRynWebView>(sp =>
            (sp.GetRequiredService<RynWindowAccessor>().Window
            ?? throw new InvalidOperationException("WebView is not available. It is only accessible after RunAsync begins.")).WebView);

        _services.AddSingleton<NativeApplicationAccessor>();

        // 5. User service configuration
        foreach (var configure in _configureActions)
        {
            configure(_services);
        }

        // 6. Build provider and app
        var provider = _services.BuildServiceProvider();
        var app = new RynApplication(provider);

        foreach (var plugin in provider.GetServices<IRynPlugin>())
        {
            app.AddPlugin(plugin);
        }

        return app;
    }

    private static void BindOptionsFromConfiguration(RynOptions options, IConfigurationSection section)
    {
        if (section[nameof(RynOptions.ApplicationId)] is { } appId)
            options.ApplicationId = appId;

        if (section[nameof(RynOptions.Title)] is { } title)
            options.Title = title;

        if (section[nameof(RynOptions.Width)] is { } width && int.TryParse(width, CultureInfo.InvariantCulture, out var w))
            options.Width = w;

        if (section[nameof(RynOptions.Height)] is { } height && int.TryParse(height, CultureInfo.InvariantCulture, out var h))
            options.Height = h;

        if (section[nameof(RynOptions.Resizable)] is { } resizable && bool.TryParse(resizable, out var r))
            options.Resizable = r;

        if (section[nameof(RynOptions.TitleBarStyle)] is { } titleBarStyle && Enum.TryParse<TitleBarStyle>(titleBarStyle, true, out var tbs))
            options.TitleBarStyle = tbs;

        if (section[nameof(RynOptions.Transparent)] is { } transparent && bool.TryParse(transparent, out var t))
            options.Transparent = t;

        if (section[nameof(RynOptions.DevTools)] is { } devTools && bool.TryParse(devTools, out var d))
            options.DevTools = d;

        // Url intentionally excluded — Uri binding needs TypeConverter reflection, keep code-only
    }

    private static void ApplyProgrammaticOverrides(RynOptions target, RynOptions source)
    {
        target.ApplicationId = source.ApplicationId;
        target.Title = source.Title;
        target.Width = source.Width;
        target.Height = source.Height;
        target.Resizable = source.Resizable;
        target.TitleBarStyle = source.TitleBarStyle;
        target.Transparent = source.Transparent;
        target.Url = source.Url;
        target.Html = source.Html;
        target.DevTools = source.DevTools;
    }
}
