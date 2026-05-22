using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class RynApplicationBuilderTests
{
    [Fact]
    public void CreateBuilder_WithDefaults_ReturnsBuilder()
    {
        var builder = RynApplication.CreateBuilder();

        builder.Should().NotBeNull();
        builder.Options.Should().NotBeNull();
    }

    [Fact]
    public void CreateBuilder_WithOptions_SetsOptions()
    {
        var options = new RynOptions { Title = "Test", Width = 1024, Height = 768 };

        var builder = RynApplication.CreateBuilder(options);

        builder.Options.Title.Should().Be("Test");
        builder.Options.Width.Should().Be(1024);
        builder.Options.Height.Should().Be(768);
    }

    [Fact]
    public async Task Build_RegistersOptionsInDI()
    {
        var options = new RynOptions { Title = "DI Test" };
        var builder = RynApplication.CreateBuilder(options);

        await using var app = builder.Build();

        var resolved = app.Services.GetRequiredService<RynOptions>();
        resolved.Title.Should().Be("DI Test");
    }

    [Fact]
    public async Task ConfigureServices_AddsServices()
    {
        var builder = RynApplication.CreateBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ITestService, TestService>();
        });

        await using var app = builder.Build();

        app.Services.GetService<ITestService>().Should().NotBeNull();
    }

    [Fact]
    public void Options_DefaultApplicationId()
    {
        var options = new RynOptions();
        options.ApplicationId.Should().Be("com.ryn.app");
    }

    [Fact]
    public async Task Build_RegistersLogging()
    {
        var builder = RynApplication.CreateBuilder();

        await using var app = builder.Build();

        var loggerFactory = app.Services.GetService<ILoggerFactory>();
        loggerFactory.Should().NotBeNull();
    }

    [Fact]
    public async Task Build_RegistersConfiguration()
    {
        var builder = RynApplication.CreateBuilder();

        await using var app = builder.Build();

        var config = app.Services.GetService<IConfiguration>();
        config.Should().NotBeNull();
    }

    [Fact]
    public async Task Build_BindsOptionsFromConfiguration()
    {
        var builder = RynApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ryn:Title"] = "Config Title",
            ["Ryn:Width"] = "1920",
        });

        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<RynOptions>();
        options.Title.Should().Be("Config Title");
        options.Width.Should().Be(1920);
    }

    [Fact]
    public async Task Build_ProgrammaticOptionsOverrideConfig()
    {
        var builder = RynApplication.CreateBuilder(new RynOptions { Title = "Programmatic" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ryn:Title"] = "Config Title",
        });

        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<RynOptions>();
        options.Title.Should().Be("Programmatic");
    }

    [Fact]
    public async Task ConfigureOptions_AppliesAfterConfigBinding()
    {
        var builder = RynApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ryn:Title"] = "From Config",
        });
        builder.ConfigureOptions(opts => opts.Width = 1920);

        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<RynOptions>();
        options.Title.Should().Be("From Config");
        options.Width.Should().Be(1920);
    }

    [Fact]
    public async Task Build_RegistersWindowAccessor()
    {
        var builder = RynApplication.CreateBuilder();

        await using var app = builder.Build();

        var accessor = app.Services.GetService<RynWindowAccessor>();
        accessor.Should().NotBeNull();
    }

    [Fact]
    public async Task IRynWindow_ThrowsBeforeRunAsync()
    {
        var builder = RynApplication.CreateBuilder();

        await using var app = builder.Build();

        var act = () => app.Services.GetRequiredService<IRynWindow>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not available*");
    }

    [Fact]
    public async Task PluginInitOrder_IsDeterministic()
    {
        var initOrder = new List<string>();
        var builder = RynApplication.CreateBuilder();
        builder.AddPlugin(_ => new TrackingPlugin("A", initOrder));
        builder.AddPlugin(_ => new TrackingPlugin("B", initOrder));
        builder.AddPlugin(_ => new TrackingPlugin("C", initOrder));

        await using var app = builder.Build();

        // Simulate plugin init (without running the full saucer event loop)
        foreach (var plugin in app.Services.GetServices<IRynPlugin>())
        {
            // Plugins are registered via AddPlugin, stored in _plugins list
            // We verify order via the app's internal plugin list
        }

        // The plugins are added in order A, B, C — they should init in that order
        // We can't call RunAsync without native libs, but we can verify the plugin
        // registration order by checking the tracking list after manual init
        initOrder.Should().BeEmpty(); // Not initialized yet

        // Manually trigger init to verify order
        var pluginField = typeof(RynApplication)
            .GetField("_plugins", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var plugins = (List<IRynPlugin>)pluginField!.GetValue(app)!;
        foreach (var plugin in plugins)
        {
            await plugin.InitializeAsync();
        }

        initOrder.Should().Equal("A", "B", "C");
    }

    private interface ITestService;
    private sealed class TestService : ITestService;

    private sealed class TrackingPlugin(string name, List<string> tracker) : IRynPlugin
    {
        public string Name => name;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            tracker.Add(name);
            return ValueTask.CompletedTask;
        }
    }
}
