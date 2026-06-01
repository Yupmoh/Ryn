using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core.Internal;

namespace Ryn.Core;

/// <summary>Builds a <see cref="RynApplication"/> with configured options, services, and plugins.</summary>
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

    /// <summary>The current options instance, or a new default if none were provided.</summary>
    public RynOptions Options => _programmaticOptions ?? new RynOptions();

    /// <summary>The configuration builder for adding configuration sources (e.g. JSON files).</summary>
    public IConfigurationBuilder Configuration => _configurationBuilder;

    /// <summary>Registers services with the dependency injection container.</summary>
    public RynApplicationBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configureActions.Add(configure);
        return this;
    }

    /// <summary>Configures application options such as window size, title, and content source.</summary>
    public RynApplicationBuilder ConfigureOptions(Action<RynOptions> configure)
    {
        _configureOptionsActions.Add(configure);
        return this;
    }

    /// <summary>Registers a plugin by type. The plugin is resolved from DI and initialized before the window opens.</summary>
    public RynApplicationBuilder AddPlugin<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>()
        where TPlugin : class, IRynPlugin
    {
        _services.AddSingleton<TPlugin>();
        _services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<TPlugin>());
        return this;
    }

    /// <summary>Registers a plugin using a factory delegate.</summary>
    public RynApplicationBuilder AddPlugin(Func<IServiceProvider, IRynPlugin> factory)
    {
        _services.AddSingleton<IRynPlugin>(factory);
        return this;
    }

    /// <summary>Builds the application, wiring up configuration, DI, options, and plugins.</summary>
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

        if (options.UseEmbeddedContent && options.ContentDirectory is null)
        {
            var extractedDir = Internal.EmbeddedContentExtractor.TryExtract();
            if (extractedDir is not null)
                options.ContentDirectory = extractedDir;
        }

        _services.AddSingleton(options);

        // 4. Window accessor + interface factories
        _services.AddSingleton<RynWindowAccessor>();
        // Deferred proxies so IRynWindow/IRynWebView can be injected into any service — even ones built
        // before the native window exists. Members forward to the real window once RunAsync has started.
        _services.AddSingleton<IRynWindow>(sp => new DeferredRynWindow(sp.GetRequiredService<RynWindowAccessor>()));
        _services.AddSingleton<IRynWebView>(sp => new DeferredRynWebView(sp.GetRequiredService<RynWindowAccessor>()));

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
        // Copy ALL options. Previously several (notably ContentDirectory) were silently dropped, so
        // `CreateBuilder(new RynOptions { ContentDirectory = "wwwroot" })` was ignored.
        target.ApplicationId = source.ApplicationId;
        target.Title = source.Title;
        target.Width = source.Width;
        target.Height = source.Height;
        target.Resizable = source.Resizable;
        target.TitleBarStyle = source.TitleBarStyle;
        target.Transparent = source.Transparent;
        target.Url = source.Url;
        target.Html = source.Html;
        target.ContentDirectory = source.ContentDirectory;
        target.UseLocalServer = source.UseLocalServer;
        target.UseHttps = source.UseHttps;
        target.LocalServerPort = source.LocalServerPort;
        target.IconPath = source.IconPath;
        target.DevTools = source.DevTools;
        target.UseEmbeddedContent = source.UseEmbeddedContent;
        target.PersistWindowState = source.PersistWindowState;
        target.CaptureUnhandledExceptions = source.CaptureUnhandledExceptions;

        // Get-only collections: clear + copy contents rather than reassign.
        target.DeepLinkSchemes.Clear();
        foreach (var scheme in source.DeepLinkSchemes) target.DeepLinkSchemes.Add(scheme);

        target.AllowedOrigins.Clear();
        foreach (var origin in source.AllowedOrigins) target.AllowedOrigins.Add(origin);
    }
}
