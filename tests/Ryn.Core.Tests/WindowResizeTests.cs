using FluentAssertions;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class WindowResizeTests
{
    [Fact]
    public void HandleNativeResize_UpdatesDimensionsAndRaisesEventOnce()
    {
        using var window = new RynWindow(new RynOptions { Width = 800, Height = 600 });
        WindowResizedEventArgs? received = null;
        var calls = 0;
        window.Resized += (_, args) => { calls++; received = args; };

        window.HandleNativeResize(1024, 768);
        window.HandleNativeResize(1024, 768);

        window.Width.Should().Be(1024);
        window.Height.Should().Be(768);
        calls.Should().Be(1);
        received.Should().NotBeNull();
        received!.Width.Should().Be(1024);
        received.Height.Should().Be(768);
    }

    [Theory]
    [InlineData(0, 768)]
    [InlineData(1024, 0)]
    [InlineData(-1, 768)]
    public void HandleNativeResize_IgnoresInvalidDimensions(int width, int height)
    {
        using var window = new RynWindow(new RynOptions { Width = 800, Height = 600 });
        var calls = 0;
        window.Resized += (_, _) => calls++;

        window.HandleNativeResize(width, height);

        window.Width.Should().Be(800);
        window.Height.Should().Be(600);
        calls.Should().Be(0);
    }
}
