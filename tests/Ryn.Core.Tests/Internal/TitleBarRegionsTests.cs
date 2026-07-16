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

    [Theory]
    [InlineData(1.25, 2295, true)]
    [InlineData(0.8, 1469, true)]
    public void Scale_IgnoreRegionCatchesNativeClickAtPageZoom(double factor, double nativeX, bool expected)
    {
        double[] drag = [0, 0, 1920, 48];
        double[] ignore = [1836, 8, 64, 32];
        var scaledDrag = TitleBarRegions.Scale(drag, factor);
        var scaledIgnore = TitleBarRegions.Scale(ignore, factor);

        TitleBarRegions.Contains(scaledIgnore, nativeX, 16 * factor).Should().Be(expected);
        TitleBarRegions.IsDraggable(scaledDrag, scaledIgnore, nativeX, 16 * factor).Should().BeFalse();
    }

    [Fact]
    public void Scale_DropsIncompleteTrailingValuesAndDoesNotMutateInput()
    {
        double[] input = [10, 20, 30, 40, 99];

        TitleBarRegions.Scale(input, 0.8).Should().Equal(8, 16, 24, 32);
        input.Should().Equal(10, 20, 30, 40, 99);
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
