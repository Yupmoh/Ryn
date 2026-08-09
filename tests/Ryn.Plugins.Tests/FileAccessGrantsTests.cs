using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using Ryn.Core;
using Ryn.Plugins.FileSystem;

namespace Ryn.Plugins.Tests;

public sealed class FileAccessGrantsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ryn-grants-" + Guid.NewGuid().ToString("N"));
    private readonly FileAccessGrants _grants = new();

    public FileAccessGrantsTests() { Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, "a.txt"), "a"); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Grant_AllowsExactFileAndRejectsForgedToken()
    {
        var token = _grants.Grant(Path.Combine(_root, "a.txt"));
        var validator = new PathValidator(new FileSystemOptions(), _grants);
        validator.Resolve(token).Should().EndWith("a.txt");
        FluentActions.Invoking(() => validator.Resolve(token + "-forged")).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void DirectoryGrant_IsContainedAndRejectsSiblingPrefix()
    {
        var token = _grants.Grant(_root);
        var validator = new PathValidator(new FileSystemOptions(), _grants);
        validator.Resolve(token + "/a.txt").Should().EndWith("a.txt");
        FluentActions.Invoking(() => validator.Resolve(token + "/../outside.txt")).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void OpenGrant_DeniesWriteAndDelete()
    {
        var token = _grants.Grant(Path.Combine(_root, "a.txt"), FileAccessOperation.Read);
        FluentActions.Invoking(() => _grants.Resolve(token, FileAccessOperation.CreateWrite)).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => _grants.Resolve(token, FileAccessOperation.Delete)).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void SaveGrant_AllowsOnlyExactAbsentTarget()
    {
        var target = Path.Combine(_root, "new.txt");
        var token = _grants.Grant(target, FileAccessOperation.CreateWrite, allowMissing: true);
        _grants.Resolve(token, FileAccessOperation.CreateWrite).Should().EndWith("new.txt");
        FluentActions.Invoking(() => _grants.Resolve(token, FileAccessOperation.Read)).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => _grants.Resolve(token + "/sibling.txt", FileAccessOperation.CreateWrite)).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void DirectorySymlinkEscape_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ryn-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "outside");
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            var token = _grants.Grant(_root, FileAccessOperation.Read);
            FluentActions.Invoking(() => _grants.Resolve(token + "/link/secret.txt", FileAccessOperation.Read)).Should().Throw<UnauthorizedAccessException>();
        }
        finally { try { Directory.Delete(link); Directory.Delete(outside, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void ExistingOutsideTargetThroughIntermediateLink_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ryn-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "outside");
        var link = Path.Combine(_root, "existing-link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            var token = _grants.Grant(_root, FileAccessOperation.Read);
            FluentActions.Invoking(() => _grants.Resolve(token + "/existing-link/secret.txt", FileAccessOperation.Read))
                .Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            try { Directory.Delete(link); Directory.Delete(outside, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void EnumerateScope_DoesNotGrantRead()
    {
        var token = _grants.Grant(_root, FileAccessOperation.Enumerate);
        _grants.Resolve(token, FileAccessOperation.Enumerate).Should().EndWith(Path.GetFileName(_root));
        FluentActions.Invoking(() => _grants.Resolve(token, FileAccessOperation.Read)).Should().Throw<UnauthorizedAccessException>();
    }
}
