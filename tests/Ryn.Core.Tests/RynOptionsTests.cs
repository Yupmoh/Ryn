using FluentAssertions;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class RynOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var options = new RynOptions();

        options.ApplicationId.Should().Be("com.ryn.app");
        options.Title.Should().Be("Ryn Application");
        options.Width.Should().Be(800);
        options.Height.Should().Be(600);
        options.X.Should().Be(0);
        options.Y.Should().Be(0);
        options.IsMaximized.Should().BeFalse();
        options.IsSet(nameof(RynOptions.X)).Should().BeFalse();
        options.IsSet(nameof(RynOptions.Y)).Should().BeFalse();
        options.IsSet(nameof(RynOptions.IsMaximized)).Should().BeFalse();
        options.Resizable.Should().BeTrue();
        options.TitleBarStyle.Should().Be(TitleBarStyle.Native);
        options.Transparent.Should().BeFalse();
        options.Backdrop.Should().Be(BackdropMaterial.None);
        options.ClickThrough.Should().BeFalse();
        options.Url.Should().BeNull();
        options.DevTools.Should().BeFalse();
    }

    [Fact]
    public void Backdrop_IsTrackedAsExplicitlySet()
    {
        var options = new RynOptions();
        options.IsSet(nameof(RynOptions.Backdrop)).Should().BeFalse();

        options.Backdrop = BackdropMaterial.Mica;

        options.Backdrop.Should().Be(BackdropMaterial.Mica);
        options.IsSet(nameof(RynOptions.Backdrop)).Should().BeTrue();
    }

    [Fact]
    public void Placement_IsTrackedAsExplicitlySet()
    {
        var options = new RynOptions();

        options.X = 200;
        options.Y = 120;
        options.IsMaximized = true;

        options.X.Should().Be(200);
        options.Y.Should().Be(120);
        options.IsMaximized.Should().BeTrue();
        options.IsSet(nameof(RynOptions.X)).Should().BeTrue();
        options.IsSet(nameof(RynOptions.Y)).Should().BeTrue();
        options.IsSet(nameof(RynOptions.IsMaximized)).Should().BeTrue();
    }
}
