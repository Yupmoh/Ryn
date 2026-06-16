using FluentAssertions;
using Ryn.Plugins.FileSystem;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PLG-03 (containment correctness): the scope check must use the framework's
/// single containment helper (<c>RynPath.IsContainedIn</c>), which compares against
/// <c>root + DirectorySeparator</c> rather than a bare <c>StartsWith(root)</c>. A bare prefix check would
/// (wrongly) treat a sibling directory whose name merely *starts with* the allowed path — e.g. allowed
/// <c>/scope</c>, target <c>/scope-evil</c> — as in-scope. These tests assert the sibling-prefix case is
/// rejected while a genuine child is allowed, on the real <see cref="PathValidator"/> / <see cref="FileSystemCommands"/>.
/// The existing 4 symlink/traversal tests stay green; this adds the sibling-prefix dimension they did not cover.
/// </summary>
public sealed class PathValidatorSiblingTests : IDisposable
{
    private readonly string _root;
    private readonly string _siblingPrefix;

    public PathValidatorSiblingTests()
    {
        var baseName = $"ryn-scope-{Guid.NewGuid():N}";
        _root = Path.Combine(Path.GetTempPath(), baseName);
        // A sibling directory whose path shares the root's full string as a prefix but is NOT under it.
        _siblingPrefix = Path.Combine(Path.GetTempPath(), baseName + "-evil");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_siblingPrefix);
    }

    private FileSystemCommands ScopedToRoot() =>
        new(new PathValidator(new FileSystemOptions { AllowedPaths = [_root] }));

    [Fact]
    public void SiblingDirectorySharingPrefix_IsRejected()
    {
        var fs = ScopedToRoot();

        // A real file living in the sibling-prefix directory — lexically it "starts with" the root string
        // but it is genuinely outside the scope, so it must be denied.
        var sibling = Path.Combine(_siblingPrefix, "secret.txt");
        File.WriteAllText(sibling, "outside");

        var act = () => fs.ReadTextFile(sibling);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void WriteIntoSiblingPrefix_IsRejected()
    {
        var fs = ScopedToRoot();

        var target = Path.Combine(_siblingPrefix, "planted.txt");
        var act = () => fs.WriteTextFile(target, "x");
        act.Should().Throw<UnauthorizedAccessException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void GenuineChild_IsAllowed()
    {
        var fs = ScopedToRoot();

        // The exact root prefix as a real child must still be permitted (we did not over-tighten).
        var child = Path.Combine(_root, "ok.txt");
        fs.WriteTextFile(child, "hello");
        fs.ReadTextFile(child).Should().Be("hello");
    }

    [Fact]
    public void RootItself_IsAllowed()
    {
        var fs = ScopedToRoot();

        // Reading a file written directly at the root (equality case, not the child case) is allowed.
        var atRoot = Path.Combine(_root, "root-file.txt");
        File.WriteAllText(atRoot, "r");
        fs.ReadTextFile(atRoot).Should().Be("r");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { /* cleanup */ }
        try { Directory.Delete(_siblingPrefix, true); } catch (IOException) { /* cleanup */ }
    }
}
