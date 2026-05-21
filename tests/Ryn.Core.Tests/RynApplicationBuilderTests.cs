using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Substitute.For<IRynWindow>());
        });

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
            services.AddSingleton(Substitute.For<IRynWindow>());
        });

        await using var app = builder.Build();

        app.Services.GetService<ITestService>().Should().NotBeNull();
    }

    private interface ITestService;
    private sealed class TestService : ITestService;
}
