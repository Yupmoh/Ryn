using FluentAssertions;
using Ryn.Core;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class DefaultIconTests
{
    [Fact]
    public void DefaultAppIcon_IsEmbedded_AsPng()
    {
        // RynWindow applies this as the default window/app icon when no IconPath is set; if the embed or
        // its logical name regresses, the default silently disappears — so guard both here.
        using var stream = typeof(RynWindow).Assembly
            .GetManifestResourceStream("Ryn.Core.ryn-icon.png");

        stream.Should().NotBeNull("the default Ryn app icon must be embedded in Ryn.Core");

        var header = new byte[8];
        stream!.ReadExactly(header);
        // PNG signature.
        header.Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }
}
