using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class WindowMoveTests
{
    [Fact]
    public void UpdatePosition_EmitsOnlyWhenCoordinatesChange()
    {
        using var window = new RynWindow(new RynOptions());
        var moves = new List<(int X, int Y)>();
        window.Moved += (_, args) => moves.Add((args.X, args.Y));

        window.UpdatePosition(120, 240, isWindowed: true).Should().BeTrue();
        window.UpdatePosition(120, 240, isWindowed: true).Should().BeFalse();
        window.UpdatePosition(121, 240, isWindowed: true).Should().BeTrue();

        moves.Should().Equal((120, 240), (121, 240));
    }

    [Fact]
    public void UpdatePosition_MaximizedGeometry_IsIgnored()
    {
        using var window = new RynWindow(new RynOptions());
        var calls = 0;
        window.Moved += (_, _) => calls++;

        window.UpdatePosition(10, 20, isWindowed: false).Should().BeFalse();

        calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true, false, false, false, WindowMoveObserver.Backend.Windows)]
    [InlineData(false, true, false, false, WindowMoveObserver.Backend.MacOS)]
    [InlineData(false, false, true, false, WindowMoveObserver.Backend.X11Polling)]
    [InlineData(false, false, true, true, WindowMoveObserver.Backend.None)]
    [InlineData(false, false, false, false, WindowMoveObserver.Backend.None)]
    internal void SelectBackend_UsesTruthfulPlatformContract(
        bool isWindows,
        bool isMacOS,
        bool isLinux,
        bool isNativeWayland,
        WindowMoveObserver.Backend expected)
    {
        WindowMoveObserver.SelectBackend(isWindows, isMacOS, isLinux, isNativeWayland)
            .Should().Be(expected);
    }
}
