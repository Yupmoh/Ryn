using FluentAssertions;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class RynSchemeResponseTests
{
    [Fact]
    public void TryParseRange_CompleteOpenEndedAndSuffix()
    {
        RynSchemeResponse.TryParseRange("bytes=2-5", 10, out var start, out var count).Should().BeTrue();
        (start, count).Should().Be((2L, 4L));
        RynSchemeResponse.TryParseRange("bytes=2-", 10, out start, out count).Should().BeTrue();
        (start, count).Should().Be((2L, 8L));
        RynSchemeResponse.TryParseRange("bytes=-3", 10, out start, out count).Should().BeTrue();
        (start, count).Should().Be((7L, 3L));
    }

    [Fact]
    public void TryParseRange_RejectsMultiRangeAndMalformed()
    {
        RynSchemeResponse.TryParseRange("bytes=1-2,4-5", 10, out _, out _).Should().BeFalse();
        RynSchemeResponse.TryParseRange("bytes=1--2", 10, out _, out _).Should().BeFalse();
        RynSchemeResponse.TryParseRange("bytes=1-2-", 10, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("bytes=10-11")]
    [InlineData("bytes=bad")]
    [InlineData("items=0-1")]
    public void TryParseRange_RejectsMalformedOrUnsatisfiable(string value)
        => RynSchemeResponse.TryParseRange(value, 10, out _, out _).Should().BeFalse();

    [Fact]
    public void FileRange_MaterializesOnlyRequestedBytesAndAdvertisesBounds()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "0123456789"u8.ToArray());
            foreach (var (range, expected, contentRange) in new[]
            {
                ("bytes=2-5", "2345", "bytes 2-5/10"),
                ("bytes=7-", "789", "bytes 7-9/10"),
                ("bytes=-3", "789", "bytes 7-9/10")
            })
            {
                var response = RynSchemeResponse.FileRange(path, range);
                response.StatusCode.Should().Be(206);
                response.ContentLength.Should().Be(expected.Length);
                response.Headers!["Accept-Ranges"].Should().Be("bytes");
                response.Headers["Content-Range"].Should().Be(contentRange);
                using var reader = new StreamReader(response.Content!, leaveOpen: false);
                reader.ReadToEnd().Should().Be(expected);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileRange_MalformedRangeReturns416WithTotalLength()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "0123456789"u8.ToArray());
            var response = RynSchemeResponse.FileRange(path, "bytes=4-2");
            response.StatusCode.Should().Be(416);
            response.Content.Should().BeNull();
            response.ContentLength.Should().Be(0);
            response.Headers!["Content-Range"].Should().Be("bytes */10");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_IncludesAcceptRangesHeader()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "asset"u8.ToArray());
            var response = RynSchemeResponse.File(path);
            response.Headers!["Accept-Ranges"].Should().Be("bytes");
            response.ContentLength.Should().Be(5);
            response.Content!.Dispose();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Origin: https://app\0X-Ryn-Token: token")]
    [InlineData("Origin: https://app\r\nX-Ryn-Token: token")]
    [InlineData("Origin: https://app\nX-Ryn-Token: token")]
    [InlineData("Origin: https://app\rX-Ryn-Token: token")]
    public void HeaderParser_AcceptsNativeDelimiters(string headers)
    {
        RynWebView.ParseHeaderValue(headers, "X-Ryn-Token").Should().Be("token");
    }
}
