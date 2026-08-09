using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class CustomSchemeConfigurationTests
{
    [Fact]
    public void ConfigureCustomScheme_RejectsReservedScheme()
    {
        var builder = RynApplication.CreateBuilder();

        var act = () => builder.ConfigureCustomScheme("ryn", _ => ValueTask.FromResult(RynSchemeResponse.NotFound()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConfigureCustomScheme_RejectsCaseInsensitiveDuplicates()
    {
        var builder = RynApplication.CreateBuilder();
        builder.ConfigureCustomScheme("assets", _ => ValueTask.FromResult(RynSchemeResponse.NotFound()));

        var act = () => builder.ConfigureCustomScheme("ASSETS", _ => ValueTask.FromResult(RynSchemeResponse.NotFound()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ConfigureCustomScheme_PreservesDeclaration()
    {
        var builder = RynApplication.CreateBuilder();
        builder.ConfigureCustomScheme("assets", _ => ValueTask.FromResult(RynSchemeResponse.NotFound()));

        await using var app = builder.Build();
        app.Services.GetRequiredService<RynOptions>().CustomSchemes.Should().ContainSingle("assets");
    }
}
