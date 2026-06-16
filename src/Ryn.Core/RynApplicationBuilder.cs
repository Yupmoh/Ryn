using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core.Internal;

namespace Ryn.Core;

/// <summary>Builds a <see cref="RynApplication"/> with configured options, services, and plugins.</summary>
public sealed class RynApplicationBuilder
{
    private RynOptions? _programmaticOptions;
    private readonly ServiceCollection _services = new();
    private readonly ConfigurationBuilder _configurationBuilder = new();
    private readonly List<Action<IServiceCollection>> _configureActions = [];
    private readonly List<Action<RynOptions>> _configureOptionsActions = [];
    private bool _built;
    internal RynApplicationBuilder(RynOptions? programmaticOptions)
    {
        _programmaticOptions = programmaticOptions;
        _configurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
    }

    /// <summary>
    /// The single mutable options instance this builder configures. Created on first access if none was
    /// supplied to <see cref="RynApplication.CreateBuilder(RynOptions)"/>. The same instance is what
    /// <see cref="Build"/> merges, so <c>builder.Options.Title = "X"</c> is honored and two reads return
    /// the same object (previously a throwaway instance was returned each access — PAP-04).
    /// </summary>
    public RynOptions Options => _programmaticOptions ??= new RynOptions();

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
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Build"/> is called more than once, or if the resolved options are invalid
    /// (non-positive <see cref="RynOptions.Width"/>/<see cref="RynOptions.Height"/>, or a
    /// <see cref="RynOptions.LocalServerPort"/> outside 1..65535).
    /// </exception>
    public RynApplication Build()
    {
        // Build() mutates the shared ServiceCollection (singletons, logging). A second call would
        // double-register IConfiguration/RynOptions/accessors and produce a second app sharing the first's
        // provider — silent corruption. One builder, one app (ARC-19).
        if (_built)
            throw new InvalidOperationException(
                "Build() has already been called on this builder. Create a new builder via " +
                "RynApplication.CreateBuilder() for each application.");
        _built = true;

        // 1. Build configuration
        var configuration = _configurationBuilder.Build();
        _services.AddSingleton<IConfiguration>(configuration);

        // 2. RynOptions: defaults → config file → embed metadata → programmatic → callbacks.
        // Layering is set-aware: only properties the programmatic instance *explicitly* assigned override
        // the config-bound values, so a bare `new RynOptions { Title = "X" }` no longer drags its default
        // Width=800 over an appsettings Ryn:Width (ARC-03). The callbacks run last and always win.
        var options = new RynOptions();
        BindOptionsFromConfiguration(options, configuration.GetSection("Ryn"));

        // The bundler embeds wwwroot and stamps [assembly: AssemblyMetadata("Ryn.UseEmbeddedContent",
        // "true")] into the entry assembly. Honor that as a default here so a published app serves its
        // embedded content without any code change. Applied as a default only — a programmatic option that
        // explicitly set UseEmbeddedContent (below) or a ConfigureOptions callback still wins, but a
        // programmatic RynOptions that merely *didn't touch* the flag no longer clobbers this true
        // (tri-state via RynOptions.IsSet). AOT-safe: GetCustomAttributes<AssemblyMetadataAttribute> reads
        // attribute data, not reflected types.
        if (HasEmbeddedContentMetadata())
            options.UseEmbeddedContent = true;

        if (_programmaticOptions is not null)
        {
            ApplyProgrammaticOverrides(options, _programmaticOptions);
        }

        foreach (var action in _configureOptionsActions)
        {
            action(options);
        }

        ValidateOptions(options);

        // 3. Logging — register a default console/debug provider bound to the "Logging" config section so
        // framework Critical/Error messages (notably "Plugin failed to initialize", which must never be
        // silent) actually reach the console out of the box. Opt out with RynOptions.DisableDefaultLogging
        // (ARC-15). The continue-on-plugin-failure policy in RynApplication is unchanged — this only makes
        // its existing Error log visible.
        _services.AddLogging(logging =>
        {
            if (!options.DisableDefaultLogging)
                ConsoleLoggerProvider.Configure(logging, configuration.GetSection("Logging"));
        });

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

        // Core enablers for plugin backends (Cluster C / PAP-06). Both forward to the live window via the
        // accessor and are usable from any thread — including before the loop is up (work is queued and runs on
        // the UI thread once it starts). Registered before the user's ConfigureServices so an app or plugin can
        // resolve them; a user registration of the same interface still overrides via last-wins.
        // - IMainThreadDispatcher: marshal native UI calls (tray/audio AppKit work) onto the main thread.
        // - IRynApplicationLifetime: request orderly shutdown (updater relaunch) instead of Environment.Exit.
        _services.AddSingleton<IMainThreadDispatcher>(sp => new MainThreadDispatcher(sp.GetRequiredService<NativeApplicationAccessor>()));
        _services.AddSingleton<IRynApplicationLifetime>(sp => new RynApplicationLifetime(sp.GetRequiredService<NativeApplicationAccessor>()));

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

    /// <summary>
    /// True when the entry assembly carries <c>[assembly: AssemblyMetadata("Ryn.UseEmbeddedContent",
    /// "true")]</c>, which the bundler stamps in alongside the embedded wwwroot. Reading attribute data
    /// is NativeAOT-safe: it does not reflect over or instantiate any application types.
    /// </summary>
    private static bool HasEmbeddedContentMetadata()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null)
            return false;

        foreach (var metadata in entry.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, "Ryn.UseEmbeddedContent", StringComparison.Ordinal)
                && string.Equals(metadata.Value, "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        // Copy ONLY the properties the programmatic instance explicitly assigned, so its untouched defaults
        // do not clobber config-bound values (ARC-03). A property the user set — even to a value that
        // happens to equal the default — still wins, which is the documented "programmatic overrides
        // config" contract. Set-tracking lives in RynOptions (AOT-safe, no reflection).
        CopyIfSet(target, source, nameof(RynOptions.ApplicationId), static (t, s) => t.ApplicationId = s.ApplicationId);
        CopyIfSet(target, source, nameof(RynOptions.Title), static (t, s) => t.Title = s.Title);
        CopyIfSet(target, source, nameof(RynOptions.Width), static (t, s) => t.Width = s.Width);
        CopyIfSet(target, source, nameof(RynOptions.Height), static (t, s) => t.Height = s.Height);
        CopyIfSet(target, source, nameof(RynOptions.Resizable), static (t, s) => t.Resizable = s.Resizable);
        CopyIfSet(target, source, nameof(RynOptions.TitleBarStyle), static (t, s) => t.TitleBarStyle = s.TitleBarStyle);
        CopyIfSet(target, source, nameof(RynOptions.Transparent), static (t, s) => t.Transparent = s.Transparent);
        CopyIfSet(target, source, nameof(RynOptions.Url), static (t, s) => t.Url = s.Url);
        CopyIfSet(target, source, nameof(RynOptions.Html), static (t, s) => t.Html = s.Html);
        CopyIfSet(target, source, nameof(RynOptions.ContentDirectory), static (t, s) => t.ContentDirectory = s.ContentDirectory);
        CopyIfSet(target, source, nameof(RynOptions.UseLocalServer), static (t, s) => t.UseLocalServer = s.UseLocalServer);
        CopyIfSet(target, source, nameof(RynOptions.UseHttps), static (t, s) => t.UseHttps = s.UseHttps);
        CopyIfSet(target, source, nameof(RynOptions.LocalServerPort), static (t, s) => t.LocalServerPort = s.LocalServerPort);
        CopyIfSet(target, source, nameof(RynOptions.IconPath), static (t, s) => t.IconPath = s.IconPath);
        CopyIfSet(target, source, nameof(RynOptions.DevTools), static (t, s) => t.DevTools = s.DevTools);
        CopyIfSet(target, source, nameof(RynOptions.UseEmbeddedContent), static (t, s) => t.UseEmbeddedContent = s.UseEmbeddedContent);
        CopyIfSet(target, source, nameof(RynOptions.PersistWindowState), static (t, s) => t.PersistWindowState = s.PersistWindowState);
        CopyIfSet(target, source, nameof(RynOptions.CaptureUnhandledExceptions), static (t, s) => t.CaptureUnhandledExceptions = s.CaptureUnhandledExceptions);
        CopyIfSet(target, source, nameof(RynOptions.DisableDefaultLogging), static (t, s) => t.DisableDefaultLogging = s.DisableDefaultLogging);

        // Get-only collections aren't config-bound, so a non-empty programmatic collection simply replaces
        // the (empty) target. An empty programmatic collection is treated as "not configured" and left alone.
        if (source.DeepLinkSchemes.Count > 0)
        {
            target.DeepLinkSchemes.Clear();
            foreach (var scheme in source.DeepLinkSchemes) target.DeepLinkSchemes.Add(scheme);
        }

        if (source.AllowedOrigins.Count > 0)
        {
            target.AllowedOrigins.Clear();
            foreach (var origin in source.AllowedOrigins) target.AllowedOrigins.Add(origin);
        }

        if (source.CustomSchemes.Count > 0)
        {
            target.CustomSchemes.Clear();
            foreach (var scheme in source.CustomSchemes) target.CustomSchemes.Add(scheme);
        }
    }

    private static void CopyIfSet(RynOptions target, RynOptions source, string propertyName, Action<RynOptions, RynOptions> copy)
    {
        if (source.IsSet(propertyName))
            copy(target, source);
    }

    private static void ValidateOptions(RynOptions options)
    {
        // Fail fast at Build() with an actionable message instead of a late crash inside the native ready
        // callback (e.g. TcpListener throwing on an out-of-range port) (ARC-19).
        if (options.Width <= 0)
            throw new InvalidOperationException(
                $"RynOptions.Width must be greater than 0 (was {options.Width.ToString(CultureInfo.InvariantCulture)}).");

        if (options.Height <= 0)
            throw new InvalidOperationException(
                $"RynOptions.Height must be greater than 0 (was {options.Height.ToString(CultureInfo.InvariantCulture)}).");

        if (options.LocalServerPort is < 1 or > 65535)
            throw new InvalidOperationException(
                $"RynOptions.LocalServerPort must be in the range 1..65535 (was {options.LocalServerPort.ToString(CultureInfo.InvariantCulture)}).");
    }
}

/// <summary>
/// Minimal, NativeAOT-safe console/debug logging provider registered by default so framework
/// Critical/Error diagnostics are visible without the app pulling in
/// <c>Microsoft.Extensions.Logging.Console</c>. Writes to <see cref="Console.Error"/> (stderr, keeping
/// stdout clean) and mirrors to the IDE debug output. The minimum level is read from the
/// <c>Logging:LogLevel:Default</c> configuration key (default <see cref="LogLevel.Information"/>).
/// </summary>
internal sealed class ConsoleLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minLevel;

    private ConsoleLoggerProvider(LogLevel minLevel) => _minLevel = minLevel;

    /// <summary>Adds the provider and applies the configured minimum level to the logging builder.</summary>
    internal static void Configure(ILoggingBuilder logging, IConfigurationSection loggingSection)
    {
        var minLevel = ParseMinimumLevel(loggingSection);
        logging.SetMinimumLevel(minLevel);

        // Register through DI so the container owns the provider's lifetime and disposes it with the
        // service provider (equivalent to AddProvider, but lets the analyzer see ownership transfer).
        logging.Services.AddSingleton<ILoggerProvider>(_ => new ConsoleLoggerProvider(minLevel));
    }

    private static LogLevel ParseMinimumLevel(IConfigurationSection loggingSection)
    {
        // Honor "Logging:LogLevel:Default" without Microsoft.Extensions.Logging.Configuration (not
        // referenced). Per-category overrides are out of scope for the default provider.
        var configured = loggingSection["LogLevel:Default"];
        return Enum.TryParse<LogLevel>(configured, ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;
    }

    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, _minLevel);

    public void Dispose()
    {
        // Stateless: Console/Debug are process-owned, nothing to release.
    }

    private sealed class ConsoleLogger(string category, LogLevel minLevel) : ILogger
    {
        private static readonly object ConsoleSync = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            var header = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss} {Abbreviate(logLevel)}] {category}: {message}");
            var line = exception is null ? header : header + Environment.NewLine + exception;

            // Single lock so interleaved logs from the UI thread and Task.Run dispatch threads stay intact.
            lock (ConsoleSync)
            {
                Console.Error.WriteLine(line);
            }

            System.Diagnostics.Debug.WriteLine(line);
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
