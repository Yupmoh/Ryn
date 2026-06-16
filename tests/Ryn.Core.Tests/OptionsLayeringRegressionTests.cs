using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ryn.Core.Tests;

/// <summary>
/// Regression tests for the options-layering and builder-lifecycle fixes:
/// <list type="bullet">
/// <item>ARC-03 — a bare programmatic <see cref="RynOptions"/> must override only the properties it
/// explicitly set; its untouched defaults must NOT clobber config-bound values.</item>
/// <item>PAP-04 — <see cref="RynApplicationBuilder.Options"/> is a single cached mutable instance, and a
/// mutation through it survives <c>Build()</c>.</item>
/// <item>ARC-19 — <c>Build()</c> twice throws; invalid Width/Height/LocalServerPort throw actionable
/// messages at <c>Build()</c> time.</item>
/// </list>
/// These are written as a fresh, self-contained fixture (no shared helpers) so a regression in the merge
/// order or the second-Build guard fails here even if other builder tests are changed.
/// </summary>
public sealed class OptionsLayeringRegressionTests
{
    // ---- ARC-03: set-aware programmatic override ----

    [Fact]
    public async Task ConfigWidth_PlusProgrammaticTitleOnly_KeepsBoth()
    {
        // appsettings Ryn:Width=1200 + CreateBuilder(new RynOptions{Title="X"}) must yield Width==1200 AND
        // Title=="X". Previously the programmatic instance's untouched default Width=800 clobbered the config.
        var builder = RynApplication.CreateBuilder(new RynOptions { Title = "X" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ryn:Width"] = "1200",
        });

        await using var app = builder.Build();
        var resolved = app.Services.GetRequiredService<RynOptions>();

        resolved.Width.Should().Be(1200, "config width must survive when the programmatic instance never set Width");
        resolved.Title.Should().Be("X", "the explicitly-set programmatic Title must win");
    }

    [Fact]
    public async Task ProgrammaticPropertySetToDefaultValue_StillWinsOverConfig()
    {
        // The "programmatic overrides config" contract holds even when the programmatic value happens to equal
        // the framework default: setting Width=800 explicitly must override an appsettings Width=1200.
        var builder = RynApplication.CreateBuilder(new RynOptions { Width = 800 });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ryn:Width"] = "1200",
        });

        await using var app = builder.Build();
        var resolved = app.Services.GetRequiredService<RynOptions>();

        resolved.Width.Should().Be(800, "an explicitly-assigned programmatic value wins even if it equals the default");
    }

    // ---- PAP-04: stable cached Options instance ----

    [Fact]
    public void Options_ReturnsSameCachedInstance()
    {
        var builder = RynApplication.CreateBuilder();
        ReferenceEquals(builder.Options, builder.Options)
            .Should().BeTrue("Options must be one cached instance, not a throwaway per access");
    }

    [Fact]
    public void Options_WhenSupplied_ReturnsTheSuppliedInstance()
    {
        var supplied = new RynOptions { Title = "Supplied" };
        var builder = RynApplication.CreateBuilder(supplied);

        ReferenceEquals(builder.Options, supplied).Should().BeTrue();
    }

    [Fact]
    public async Task MutatingOptionsProperty_SurvivesBuild()
    {
        // builder.Options.Title = "X" then Build() must produce Title == "X" (the mutation is not discarded).
        var builder = RynApplication.CreateBuilder();
        builder.Options.Title = "Mutated";
        builder.Options.Width = 1366;

        await using var app = builder.Build();
        var resolved = app.Services.GetRequiredService<RynOptions>();

        resolved.Title.Should().Be("Mutated");
        resolved.Width.Should().Be(1366);
    }

    // ---- ARC-19: build-once + validation ----

    [Fact]
    public async Task Build_CalledTwice_Throws()
    {
        var builder = RynApplication.CreateBuilder();
        await using var app = builder.Build();

        var second = builder.Build;
        second.Should().Throw<InvalidOperationException>()
            .WithMessage("*already been called*");
    }

    [Fact]
    public void Build_WithNonPositiveWidth_ThrowsActionableMessage()
    {
        var builder = RynApplication.CreateBuilder(new RynOptions { Width = 0 });

        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Width*greater than 0*");
    }

    [Fact]
    public void Build_WithNegativeHeight_ThrowsActionableMessage()
    {
        var builder = RynApplication.CreateBuilder(new RynOptions { Height = -10 });

        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Height*greater than 0*");
    }

    [Fact]
    public void Build_WithPortAbove65535_ThrowsActionableMessage()
    {
        var builder = RynApplication.CreateBuilder(new RynOptions { LocalServerPort = 70000 });

        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*LocalServerPort*1..65535*");
    }

    [Fact]
    public void Build_WithPortZero_ThrowsActionableMessage()
    {
        var builder = RynApplication.CreateBuilder(new RynOptions { LocalServerPort = 0 });

        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*LocalServerPort*1..65535*");
    }
}
