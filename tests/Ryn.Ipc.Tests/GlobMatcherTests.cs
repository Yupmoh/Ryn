using FluentAssertions;
using Ryn.Ipc;
using Xunit;

namespace Ryn.Ipc.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("/data/*", "/data/file.txt", true)]
    [InlineData("/data/*", "/data/sub/file.txt", false)]   // * does not cross '/'
    [InlineData("/data/**", "/data/sub/deep/file.txt", true)] // ** crosses '/'
    [InlineData("/data/**", "/data", true)]                 // ** matches zero segments
    [InlineData("/data/*.txt", "/data/notes.txt", true)]
    [InlineData("/data/*.txt", "/data/notes.md", false)]
    [InlineData("/data/?.txt", "/data/a.txt", true)]
    [InlineData("/data/?.txt", "/data/ab.txt", false)]
    [InlineData("/data/*", "/other/file.txt", false)]
    public void IsMatch_HonorsGlobSemantics(string pattern, string path, bool expected)
    {
        GlobMatcher.IsMatch(pattern, path, ignoreCase: false).Should().Be(expected);
    }

    [Fact]
    public void IsMatch_CaseInsensitive_WhenRequested()
    {
        GlobMatcher.IsMatch("/Data/*", "/data/x", ignoreCase: true).Should().BeTrue();
        GlobMatcher.IsMatch("/Data/*", "/data/x", ignoreCase: false).Should().BeFalse();
    }

    [Fact]
    public void IsGlob_DetectsMetacharacters()
    {
        GlobMatcher.IsGlob("/a/b").Should().BeFalse();
        GlobMatcher.IsGlob("/a/*").Should().BeTrue();
        GlobMatcher.IsGlob("/a/?").Should().BeTrue();
    }
}
