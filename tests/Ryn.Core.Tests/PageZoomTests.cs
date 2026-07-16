using FluentAssertions;
using Ryn.Core;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class PageZoomTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.1, 0.25)]
    [InlineData(7.0, 5.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void Clamp_ReturnsSanePageZoom(double factor, double expected) =>
        WebViewPageZoom.Clamp(factor).Should().Be(expected);

    [Fact]
    public void StandaloneWindow_TracksClampedPageZoomWithoutNativeHost()
    {
        using var window = new RynWindow(new RynOptions());

        window.SetPageZoom(8.0);

        window.GetPageZoom().Should().Be(5.0);
    }

    [Fact]
    public void DeferredWindow_ForwardsPageZoomToLiveWindow()
    {
        var accessor = new RynWindowAccessor();
        var deferred = new DeferredRynWindow(accessor);
        using var window = new RynWindow(new RynOptions());
        accessor.Window = window;

        deferred.SetPageZoom(0.8);

        deferred.GetPageZoom().Should().Be(0.8);
        window.GetPageZoom().Should().Be(0.8);
    }
}
