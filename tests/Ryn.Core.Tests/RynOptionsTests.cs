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
        options.Resizable.Should().BeTrue();
        options.TitleBarStyle.Should().Be(TitleBarStyle.Native);
        options.Transparent.Should().BeFalse();
        options.ClickThrough.Should().BeFalse();
        options.Url.Should().BeNull();
        options.DevTools.Should().BeFalse();
    }
}
