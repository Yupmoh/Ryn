using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Interop.Tests;

/// <summary>
/// Locks in the pure-managed marshalling chokepoint that every externally-influenced string
/// (window titles, URLs, schemes, headers) flows through before crossing the saucer C ABI.
/// These tests require no native library; native-load behaviour is covered by Ryn.Integration.Tests.
/// </summary>
public sealed class Utf8StringTests
{
    [Fact]
    public unsafe void Create_Ascii_RoundTripsThroughNulTerminatedBuffer()
    {
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create("hello", buf);

        try
        {
            str.ByteCount.Should().Be(5);
            Utf8String.ToManaged(str.Pointer).Should().Be("hello");
        }
        finally
        {
            str.Dispose();
        }
    }

    [Theory]
    [InlineData("Hello, 世界! 🌍")]      // CJK + supplementary-plane emoji (4-byte UTF-8)
    [InlineData("café résumé naïve")]   // Latin-1 supplement (2-byte UTF-8)
    [InlineData("Ωμέγα Ω")]             // Greek
    [InlineData(" trailing ")] // non-breaking spaces, no embedded NUL
    public unsafe void Create_MultiByteUtf8_RoundTrips(string value)
    {
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create(value, buf);

        try
        {
            // ByteCount is the UTF-8 byte length, which exceeds the char count for multi-byte input.
            str.ByteCount.Should().Be(System.Text.Encoding.UTF8.GetByteCount(value));
            Utf8String.ToManaged(str.Pointer).Should().Be(value);
        }
        finally
        {
            str.Dispose();
        }
    }

    [Fact]
    public unsafe void Create_EmptyString_ProducesZeroLengthButValidPointer()
    {
        Span<byte> buf = stackalloc byte[16];
        var str = Utf8String.Create("", buf);

        try
        {
            str.ByteCount.Should().Be(0);
            ((nint)str.Pointer).Should().NotBe(nint.Zero);
            Utf8String.ToManaged(str.Pointer).Should().BeEmpty();
        }
        finally
        {
            str.Dispose();
        }
    }

    [Fact]
    public unsafe void Create_StringLongerThanStackBuffer_FallsBackToPooledBufferAndRoundTrips()
    {
        // Force the pooled/pinned path: byteCount + 1 must exceed the stack buffer length.
        var longString = new string('x', 512);
        Span<byte> buf = stackalloc byte[64];
        var str = Utf8String.Create(longString, buf);

        try
        {
            str.ByteCount.Should().Be(512);
            Utf8String.ToManaged(str.Pointer).Should().Be(longString);
        }
        finally
        {
            str.Dispose();
        }
    }

    // --- INT-06 regression: embedded NUL must be rejected, not silently truncated ---------------

    [Theory]
    [InlineData("a\0b")]       // NUL in the middle
    [InlineData("\0leading")]  // NUL at the start
    [InlineData("trailing\0")] // NUL at the end
    [InlineData("\0")]         // NUL only
    public void Create_EmbeddedNul_ThrowsArgumentException(string hostileValue)
    {
        // saucer reads the buffer as a NUL-terminated C string. Before the INT-06 fix an embedded NUL
        // silently truncated the title/URL/header at the native boundary; it must now throw instead.
        // Create throws before constructing anything, so no Utf8String/Dispose needs cleanup here.
        var ex = TryCreate(hostileValue);

        ex.Should().BeOfType<ArgumentException>()
            .Which.Message.Should().Contain("NUL");
        ((ArgumentException)ex!).ParamName.Should().Be("value");
    }

    [Fact]
    public unsafe void Create_StringWithoutNul_DoesNotThrow()
    {
        // The negative control for the INT-06 check: a normal string still round-trips.
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create("no nul here", buf);

        try
        {
            Utf8String.ToManaged(str.Pointer).Should().Be("no nul here");
        }
        finally
        {
            str.Dispose();
        }
    }

    [Fact]
    public void Create_NullValue_ThrowsArgumentNullException()
    {
        var ex = TryCreate(null!);

        ex.Should().BeOfType<ArgumentNullException>()
            .Which.ParamName.Should().Be("value");
    }

    // --- ToManaged overloads -------------------------------------------------------------------

    [Fact]
    public unsafe void ToManaged_NullPointer_ReturnsEmpty()
    {
        Utf8String.ToManaged((sbyte*)null).Should().BeEmpty();
    }

    [Fact]
    public unsafe void ToManaged_WithExplicitLength_ReturnsLeadingSubstring()
    {
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create("hello world", buf);

        try
        {
            Utf8String.ToManaged(str.Pointer, 5).Should().Be("hello");
        }
        finally
        {
            str.Dispose();
        }
    }

    [Fact]
    public unsafe void ToManaged_WithNonPositiveLength_ReturnsEmpty()
    {
        Span<byte> buf = stackalloc byte[256];
        var str = Utf8String.Create("hello", buf);

        try
        {
            Utf8String.ToManaged(str.Pointer, 0).Should().BeEmpty();
            Utf8String.ToManaged(str.Pointer, -1).Should().BeEmpty();
        }
        finally
        {
            str.Dispose();
        }
    }

    /// <summary>
    /// Invokes <see cref="Utf8String.Create"/> for an input expected to be rejected and returns the
    /// thrown <see cref="ArgumentException"/> (or <c>null</c>). Kept as a plain method so
    /// <c>stackalloc</c> stays out of the lambda/closure context and the returned <c>ref struct</c>
    /// never escapes. Only the argument-validation family is caught; any other exception fails the test.
    /// </summary>
    private static ArgumentException? TryCreate(string value)
    {
        try
        {
            Span<byte> buf = stackalloc byte[256];
            // Reaching here means validation passed unexpectedly; dispose so no pooled buffer leaks.
            var str = Utf8String.Create(value, buf);
            str.Dispose();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex;
        }
    }
}
