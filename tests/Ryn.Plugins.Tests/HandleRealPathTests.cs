using FluentAssertions;
using Ryn.Plugins.FileSystem;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Cover for the fd-realpath hardening that closes the PLG-03 validate→open TOCTOU race. The re-verify after
/// open compares the real path of the OPEN handle against the authorized scope, so these tests pin the two
/// properties that make that work: (1) an open handle resolves to its canonical path, and (2) a handle opened
/// through a symlink resolves to the symlink's TARGET — which is precisely what a by-name re-check cannot see
/// and what catches an escaping-symlink swap. A null/invalid handle yields null so the caller degrades to the
/// by-name re-check rather than failing.
/// </summary>
public sealed class HandleRealPathTests : IDisposable
{
    private readonly string _dir;

    public HandleRealPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ryn-fdpath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void TryGet_ReturnsCanonicalPathOfOpenHandle()
    {
        var file = Path.Combine(_dir, "real.txt");
        File.WriteAllText(file, "x");

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
        var real = HandleRealPath.TryGet(stream.SafeFileHandle);

        real.Should().NotBeNull();
        PathValidator.Canonicalize(real!).Should().Be(PathValidator.Canonicalize(file));
    }

    [Fact]
    public void TryGet_ResolvesSymlinkToRealTarget()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_dir, "link.txt");
        File.CreateSymbolicLink(link, target);

        using var stream = new FileStream(link, FileMode.Open, FileAccess.Read);
        var real = HandleRealPath.TryGet(stream.SafeFileHandle);

        // The handle's real path is the TARGET, not the symlink we opened by name. This is the property the
        // PLG-03 re-verify relies on: had a component been swapped to an escaping link, the handle would name
        // the escaped target and the scope check would fail.
        real.Should().NotBeNull();
        PathValidator.Canonicalize(real!).Should().Be(PathValidator.Canonicalize(target));
    }

    [Fact]
    public void TryGet_NullHandle_ReturnsNull()
    {
        HandleRealPath.TryGet(null).Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* cleanup */ }
    }
}
