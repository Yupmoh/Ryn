using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class LinuxDisplayTests
{
    [Theory]
    [InlineData("GdkWaylandDisplay", true)]
    [InlineData("GdkX11Display", false)]
    [InlineData(null, false)]
    public void IsNativeWaylandType_RecognizesOnlyWaylandDisplay(string? typeName, bool expected)
    {
        LinuxDisplay.IsNativeWaylandType(typeName).Should().Be(expected);
    }
}
