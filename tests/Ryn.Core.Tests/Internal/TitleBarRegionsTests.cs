using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

public sealed class TitleBarRegionsTests
{
    // One drag strip [80,0 160x44] and one interactive control (a button) inside it at [100,8 100x28].
    private static readonly double[] Drag = [80, 0, 160, 44];
    private static readonly double[] Ignore = [100, 8, 100, 28];

    [Theory]
    [InlineData(160, 4, true)]    // inside the drag strip, above the button → draggable
    [InlineData(230, 40, true)]   // inside the drag strip, right of the button → draggable
    [InlineData(150, 20, false)]  // over the button (ignore rect) → not draggable (click reaches the DOM)
    [InlineData(340, 20, false)]  // right of the strip entirely → not draggable
    [InlineData(20, 20, false)]   // over the traffic-light area → not draggable
    public void IsDraggable_RespectsDragAndIgnoreRegions(double x, double y, bool expected) =>
        TitleBarRegions.IsDraggable(Drag, Ignore, x, y).Should().Be(expected);

    [Fact]
    public void IsDraggable_FalseWhenNoDragRegions()
    {
        TitleBarRegions.IsDraggable([], [], 100, 10).Should().BeFalse();
        TitleBarRegions.Contains([], 0, 0).Should().BeFalse();
    }

    [Fact]
    public void Contains_HandlesMultipleRects()
    {
        double[] rects = [0, 0, 10, 10, 100, 100, 10, 10];
        TitleBarRegions.Contains(rects, 5, 5).Should().BeTrue();
        TitleBarRegions.Contains(rects, 105, 105).Should().BeTrue();
        TitleBarRegions.Contains(rects, 50, 50).Should().BeFalse();
    }
}
