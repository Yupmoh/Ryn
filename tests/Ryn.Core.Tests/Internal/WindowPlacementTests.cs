using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Create-time origin clamping: a saved/configured X/Y must land on a visible monitor
/// before first paint. Covers the Windows <c>-32000</c> sentinel and a state file from
/// a now-disconnected display.
/// </summary>
public sealed class WindowPlacementTests
{
    private static readonly WindowPlacement.ScreenBounds Primary = new(0, 0, 1920, 1080);
    private static readonly WindowPlacement.ScreenBounds Secondary = new(1920, 0, 1280, 1024);

    [Fact]
    public void Clamp_KeepsOnScreenOrigin()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(200, 120, 1280, 720, [Primary]);

        x.Should().Be(200);
        y.Should().Be(120);
    }

    [Fact]
    public void Clamp_PinsMinus32000OntoFirstMonitor()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(-32000, -32000, 1280, 720, [Primary, Secondary]);

        x.Should().Be(0);
        y.Should().Be(0);
    }

    [Fact]
    public void Clamp_MovesDisconnectedMonitorOntoFirstVisible()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(4000, 100, 800, 600, [Primary]);

        x.Should().Be(1120);
        y.Should().Be(100);
    }

    [Fact]
    public void Clamp_PrefersContainingSecondaryMonitor()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(2100, 80, 800, 600, [Primary, Secondary]);

        x.Should().Be(2100);
        y.Should().Be(80);
    }

    [Fact]
    public void Clamp_UsesIntersectingMonitorWhenOriginIsJustOff()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(-40, 80, 800, 600, [Primary]);

        x.Should().Be(0);
        y.Should().Be(80);
    }

    [Fact]
    public void Clamp_WithoutScreens_LeavesOriginUnchanged()
    {
        var (x, y) = WindowPlacement.ClampToVisibleMonitor(-32000, 12, 800, 600, []);

        x.Should().Be(-32000);
        y.Should().Be(12);
    }
}
